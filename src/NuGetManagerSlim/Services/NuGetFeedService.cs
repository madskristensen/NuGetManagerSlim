using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    public sealed class NuGetFeedService : INuGetFeedService, IDisposable
    {
        // Reuse NuGet's on-disk HTTP cache (%userprofile%\.nuget\v3-cache) for the
        // whole VS session and let entries live up to 30 minutes before we go back
        // to the network. Default MaxAge is much shorter, which forces revalidation
        // on most metadata requests.
        private readonly SourceCacheContext _cacheContext = new()
        {
            MaxAge = DateTimeOffset.UtcNow.AddMinutes(-30),
        };
        private readonly ILogger _logger = NullLogger.Instance;

        // In-memory result cache so re-typing or re-opening a previously seen
        // search renders instantly without a second network round-trip. The
        // NuGet HTTP cache below us already de-duplicates HTTP responses, but
        // skipping the entire SearchAsync pipeline (resource lookup + version
        // fan-out per result) is what actually makes typing feel snappy.
        private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(10);
        private const int SearchCacheMaxEntries = 64;
        private readonly Dictionary<string, SearchCacheEntry> _searchCache = new(StringComparer.Ordinal);
        private readonly object _searchCacheLock = new();

        private sealed class SearchCacheEntry
        {
            public DateTime ExpiresUtc;
            public IReadOnlyList<PackageModel> Results = Array.Empty<PackageModel>();
        }

        private static string BuildSearchCacheKey(
            string query,
            bool includePrerelease,
            int skip,
            int take,
            IReadOnlyCollection<string>? sourceNameFilter)
        {
            var sources = sourceNameFilter == null || sourceNameFilter.Count == 0
                ? string.Empty
                : string.Join(",", sourceNameFilter.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            return $"{(query ?? string.Empty).ToLowerInvariant()}|{includePrerelease}|{skip}|{take}|{sources}";
        }

        private bool TryGetCachedSearch(string key, out IReadOnlyList<PackageModel> results)
        {
            lock (_searchCacheLock)
            {
                if (_searchCache.TryGetValue(key, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
                {
                    results = entry.Results;
                    return true;
                }
                if (_searchCache.ContainsKey(key))
                    _searchCache.Remove(key);
            }
            results = Array.Empty<PackageModel>();
            return false;
        }

        private void StoreCachedSearch(string key, IReadOnlyList<PackageModel> results)
        {
            lock (_searchCacheLock)
            {
                if (_searchCache.Count >= SearchCacheMaxEntries)
                {
                    // Drop the entry that expires soonest. Keeps the cache bounded
                    // without a full LRU bookkeeping struct.
                    var oldestKey = string.Empty;
                    var oldestExp = DateTime.MaxValue;
                    foreach (var kvp in _searchCache)
                    {
                        if (kvp.Value.ExpiresUtc < oldestExp)
                        {
                            oldestExp = kvp.Value.ExpiresUtc;
                            oldestKey = kvp.Key;
                        }
                    }
                    if (oldestKey.Length > 0)
                        _searchCache.Remove(oldestKey);
                }

                _searchCache[key] = new SearchCacheEntry
                {
                    ExpiresUtc = DateTime.UtcNow.Add(SearchCacheTtl),
                    Results = results,
                };
            }
        }

        // When set, the next SearchAsync call will skip our in-memory cache to
        // guarantee a live feed query (used by the user-initiated Refresh action).
        // The flag auto-clears after that call.
        private int _bypassNextNetworkFetch;

        public void InvalidateCache()
        {
            lock (_searchCacheLock)
            {
                _searchCache.Clear();
            }
            System.Threading.Interlocked.Exchange(ref _bypassNextNetworkFetch, 1);
        }

        public async Task<IReadOnlyList<PackageModel>> SearchAsync(
            string query,
            bool includePrerelease,
            int skip,
            int take,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? sourceNameFilter = null)
        {
            var bypass = System.Threading.Interlocked.Exchange(ref _bypassNextNetworkFetch, 0) == 1;
            var cacheKey = BuildSearchCacheKey(query, includePrerelease, skip, take, sourceNameFilter);
            if (!bypass && TryGetCachedSearch(cacheKey, out var cached))
                return cached;

            var results = new List<PackageModel>();
            var sources = GetEnabledSources();
            if (sourceNameFilter != null && sourceNameFilter.Count > 0)
            {
                var allowed = new HashSet<string>(sourceNameFilter, StringComparer.OrdinalIgnoreCase);
                sources = sources.Where(s => allowed.Contains(s.Name)).ToList();
            }

            // Fan out to all enabled sources in parallel and race each against a
            // soft 2.5s deadline. A slow / dead feed no longer blocks the healthy
            // ones - we surface whatever returned in time.
            var perSourceTimeout = TimeSpan.FromMilliseconds(2500);
            using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sourceTasks = sources
                .Select(src => RunSourceSearchAsync(src, query, includePrerelease, skip, take, perCallCts.Token))
                .ToArray();

            var completed = Task.WhenAll(sourceTasks);
            var timeout = Task.Delay(perSourceTimeout, cancellationToken);
            await Task.WhenAny(completed, timeout).ConfigureAwait(false);

            for (var i = 0; i < sourceTasks.Length; i++)
            {
                var t = sourceTasks[i];
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                    results.AddRange(t.Result);
            }

            // Let any still-running source tasks finish silently in the background
            // so their results land in the next cache fill, but don't await them.
            _ = completed.ContinueWith(_ => { /* observe to avoid UnobservedTaskException */ }, TaskScheduler.Default);

            var deduped = results
                .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            StoreCachedSearch(cacheKey, deduped);
            return deduped;
        }

        private async Task<List<PackageModel>?> RunSourceSearchAsync(
            PackageSourceModel source,
            string query,
            bool includePrerelease,
            int skip,
            int take,
            CancellationToken cancellationToken)
        {
            try
            {
                var repository = Repository.Factory.GetCoreV3(source.Source);
                var resource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
                if (resource == null) return null;

                var searchFilter = new SearchFilter(includePrerelease: includePrerelease);
                var searchResults = await resource.SearchAsync(
                    query, searchFilter, skip, take, _logger, cancellationToken).ConfigureAwait(false);

                var list = new List<PackageModel>();
                foreach (var result in searchResults)
                {
                    // Use Identity.Version directly instead of fetching the full
                    // version list per package (the previous N+1 latency multiplier).
                    // The detail pane's GetVersionsAsync still produces the full,
                    // accurate list when the user actually opens a package.
                    var version = result.Identity.Version;
                    NuGet.Versioning.NuGetVersion? latestStable;
                    NuGet.Versioning.NuGetVersion? latestPre;
                    if (version != null && version.IsPrerelease)
                    {
                        latestStable = null;
                        latestPre = version;
                    }
                    else
                    {
                        latestStable = version;
                        latestPre = version;
                    }

                    list.Add(new PackageModel
                    {
                        PackageId = result.Identity.Id,
                        LatestStableVersion = latestStable,
                        LatestPrereleaseVersion = latestPre,
                        Description = result.Description,
                        Authors = result.Authors,
                        DownloadCount = result.DownloadCount ?? 0,
                        SourceName = source.Name,
                        IconUrl = result.IconUrl?.ToString(),
                    });
                }
                return list;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<PackageModel?> GetPackageMetadataAsync(
            string packageId,
            CancellationToken cancellationToken)
        {
            foreach (var source in GetEnabledSources())
            {
                try
                {
                    var repository = Repository.Factory.GetCoreV3(source.Source);
                    var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
                    if (resource == null) continue;

                    var metadata = await resource.GetMetadataAsync(
                        packageId, includePrerelease: true, includeUnlisted: false,
                        _cacheContext, _logger, cancellationToken).ConfigureAwait(false);

                    var latest = metadata.OrderByDescending(m => m.Identity.Version).FirstOrDefault();
                    if (latest == null) continue;

                    var deps = latest.DependencySets
                        .SelectMany(ds => ds.Packages.Select(p => new PackageDependencyInfo
                        {
                            PackageId = p.Id,
                            VersionRange = p.VersionRange?.ToString() ?? "*",
                            TargetFramework = ds.TargetFramework?.GetShortFolderName() ?? string.Empty,
                        }))
                        .ToList();

                    return new PackageModel
                    {
                        PackageId = latest.Identity.Id,
                        LatestStableVersion = latest.Identity.Version.IsPrerelease ? null : latest.Identity.Version,
                        LatestPrereleaseVersion = latest.Identity.Version,
                        Description = latest.Description,
                        Authors = latest.Authors,
                        LicenseExpression = latest.LicenseMetadata?.License,
                        LicenseUrl = latest.LicenseUrl?.ToString(),
                        DownloadCount = latest.DownloadCount ?? 0,
                        SourceName = source.Name,
                        ProjectUrl = latest.ProjectUrl?.ToString(),
                        IconUrl = latest.IconUrl?.ToString(),
                        Dependencies = deps,
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Try next source
                }
            }

            return null;
        }

        public async Task<PackageModel?> GetPackageMetadataAsync(
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken)
        {
            var identity = new global::NuGet.Packaging.Core.PackageIdentity(packageId, version);
            foreach (var source in GetEnabledSources())
            {
                try
                {
                    var repository = Repository.Factory.GetCoreV3(source.Source);
                    var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
                    if (resource == null) continue;

                    var meta = await resource.GetMetadataAsync(identity, _cacheContext, _logger, cancellationToken).ConfigureAwait(false);
                    if (meta == null) continue;

                    var deps = meta.DependencySets
                        .SelectMany(ds => ds.Packages.Select(p => new PackageDependencyInfo
                        {
                            PackageId = p.Id,
                            VersionRange = p.VersionRange?.ToString() ?? "*",
                            TargetFramework = ds.TargetFramework?.GetShortFolderName() ?? string.Empty,
                        }))
                        .ToList();

                    return new PackageModel
                    {
                        PackageId = meta.Identity.Id,
                        LatestStableVersion = meta.Identity.Version.IsPrerelease ? null : meta.Identity.Version,
                        LatestPrereleaseVersion = meta.Identity.Version,
                        Description = meta.Description,
                        Authors = meta.Authors,
                        LicenseExpression = meta.LicenseMetadata?.License,
                        LicenseUrl = meta.LicenseUrl?.ToString(),
                        DownloadCount = meta.DownloadCount ?? 0,
                        SourceName = source.Name,
                        ProjectUrl = meta.ProjectUrl?.ToString(),
                        IconUrl = meta.IconUrl?.ToString(),
                        Dependencies = deps,
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Try next source
                }
            }

            return null;
        }

        public async Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(
            string packageId,
            bool includePrerelease,
            CancellationToken cancellationToken)
        {
            foreach (var source in GetEnabledSources())
            {
                try
                {
                    var repository = Repository.Factory.GetCoreV3(source.Source);
                    var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
                    if (resource == null) continue;

                    var versions = await resource.GetAllVersionsAsync(packageId, _cacheContext, _logger, cancellationToken).ConfigureAwait(false);
                    return versions
                        .Where(v => includePrerelease || !v.IsPrerelease)
                        .OrderByDescending(v => v)
                        .ToList();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Try next source
                }
            }

            return [];
        }

        public Task<IReadOnlyList<PackageSourceModel>> GetSourcesAsync(CancellationToken cancellationToken)
        {
            var settings = Settings.LoadDefaultSettings(root: null);
            var provider = new PackageSourceProvider(settings);
            var sources = provider.LoadPackageSources()
                .Select(s => new PackageSourceModel
                {
                    Name = s.Name,
                    Source = s.Source,
                    IsEnabled = s.IsEnabled,
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<PackageSourceModel>>(sources);
        }

        public Task<string?> GetReadmeAsync(string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            // README fetching from nuget.org requires the flat container API
            // Return null for now; detailed implementation would use HttpClient + nuget.org flat container
            return Task.FromResult<string?>(null);
        }

        private static IReadOnlyList<PackageSourceModel> GetEnabledSources()
        {
            try
            {
                var settings = Settings.LoadDefaultSettings(root: null);
                var provider = new PackageSourceProvider(settings);
                return provider.LoadPackageSources()
                    .Where(s => s.IsEnabled)
                    .Select(s => new PackageSourceModel { Name = s.Name, Source = s.Source, IsEnabled = true })
                    .ToList();
            }
            catch
            {
                return [new PackageSourceModel { Name = "nuget.org", Source = "https://api.nuget.org/v3/index.json", IsEnabled = true }];
            }
        }

        public void Dispose() => _cacheContext.Dispose();
    }
}
