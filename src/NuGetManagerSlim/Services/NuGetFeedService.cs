using System;
using System.Collections.Concurrent;
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

        private void StoreMetadata(string key, PackageModel? value)
        {
            lock (_metadataCacheLock)
            {
                if (_metadataCache.Count >= MetadataCacheMaxEntries)
                {
                    var oldestKey = string.Empty;
                    var oldestExp = DateTime.MaxValue;
                    foreach (var kvp in _metadataCache)
                    {
                        if (kvp.Value.ExpiresUtc < oldestExp)
                        {
                            oldestExp = kvp.Value.ExpiresUtc;
                            oldestKey = kvp.Key;
                        }
                    }
                    if (oldestKey.Length > 0) _metadataCache.Remove(oldestKey);
                }
                _metadataCache[key] = new MetadataCacheEntry
                {
                    ExpiresUtc = DateTime.UtcNow.Add(MetadataCacheTtl),
                    Value = value,
                };
            }
        }

        private void StoreVersions(string key, IReadOnlyList<PackageVersionInfo> value)
        {
            lock (_versionsCacheLock)
            {
                if (_versionsCache.Count >= MetadataCacheMaxEntries)
                {
                    var oldestKey = string.Empty;
                    var oldestExp = DateTime.MaxValue;
                    foreach (var kvp in _versionsCache)
                    {
                        if (kvp.Value.ExpiresUtc < oldestExp)
                        {
                            oldestExp = kvp.Value.ExpiresUtc;
                            oldestKey = kvp.Key;
                        }
                    }
                    if (oldestKey.Length > 0) _versionsCache.Remove(oldestKey);
                }
                _versionsCache[key] = new VersionsCacheEntry
                {
                    ExpiresUtc = DateTime.UtcNow.Add(MetadataCacheTtl),
                    Value = value,
                };
            }
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

        public bool TryGetCachedSearch(
            string query,
            bool includePrerelease,
            int skip,
            int take,
            IReadOnlyCollection<string>? sourceNameFilter,
            out IReadOnlyList<PackageModel> results)
        {
            var key = BuildSearchCacheKey(query ?? string.Empty, includePrerelease, skip, take, sourceNameFilter);
            return TryGetCachedSearch(key, out results);
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

        // Per-session metadata + versions caches keyed by package id. Both layers
        // are wired to InvalidateCache() so a user-initiated Refresh punches
        // through them too.
        private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromMinutes(15);
        private const int MetadataCacheMaxEntries = 256;

        private sealed class MetadataCacheEntry
        {
            public DateTime ExpiresUtc;
            public PackageModel? Value;
        }

        private sealed class VersionsCacheEntry
        {
            public DateTime ExpiresUtc;
            public IReadOnlyList<PackageVersionInfo> Value = Array.Empty<PackageVersionInfo>();
        }

        private readonly Dictionary<string, MetadataCacheEntry> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _metadataCacheLock = new();
        private readonly Dictionary<string, VersionsCacheEntry> _versionsCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _versionsCacheLock = new();

        public void InvalidateCache()
        {
            lock (_searchCacheLock) _searchCache.Clear();
            lock (_metadataCacheLock) _metadataCache.Clear();
            lock (_versionsCacheLock) _versionsCache.Clear();
            lock (_sourcesCacheLock) _enabledSourcesCache = null;
            _repositoryCache.Clear();
            _searchResourceCache.Clear();
            _metadataResourceCache.Clear();
            _searchDiskCache.Clear();
            lock (_vulnIndexLock) { _vulnIndexCache = null; }
            System.Threading.Interlocked.Exchange(ref _bypassNextNetworkFetch, 1);
            DiagnosticsLogger.Info("Caches cleared by user-initiated refresh.");
        }

        // Per-source repository + resource caches. NuGet's Repository.Factory
        // returns a fresh SourceRepository on every call; each call also
        // re-runs GetResourceAsync, which re-downloads the v3 service index
        // when it isn't already warm in this process. Cache them ourselves so
        // PrewarmSourcesAsync actually pays off on the first user search.
        private readonly ConcurrentDictionary<string, SourceRepository> _repositoryCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<PackageSearchResource>> _searchResourceCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<PackageMetadataResource>> _metadataResourceCache = new(StringComparer.OrdinalIgnoreCase);

        // Memory cache for the merged bulk vulnerability index. The underlying
        // advisory files are also cached on disk by NuGet's HTTP layer (shared
        // with the built-in client), but merging the per-source dictionaries on
        // every Vulnerable-view visit is wasteful, so keep the merged result for
        // the session with the same TTL as the per-package metadata cache.
        private Dictionary<string, IReadOnlyList<PackageVulnerabilityAdvisory>>? _vulnIndexCache;
        private DateTime _vulnIndexExpiresUtc;
        private readonly object _vulnIndexLock = new();

        // Disk-backed second tier for the search-result cache. Lets the first
        // search of a fresh VS session be served from disk when the same query
        // was issued recently. Stored as a small DTO so NuGetVersion roundtrips
        // cleanly (System.Text.Json doesn't know how to read NuGetVersion).
        private readonly FeedMetadataDiskCache<List<SearchCacheDto>> _searchDiskCache =
            new("search", TimeSpan.FromHours(1), 50L * 1024 * 1024);

        private sealed class SearchCacheDto
        {
            public string PackageId { get; set; } = string.Empty;
            public string? LatestStable { get; set; }
            public string? LatestPre { get; set; }
            public string? Description { get; set; }
            public string? Authors { get; set; }
            public long DownloadCount { get; set; }
            public string? SourceName { get; set; }
            public string? IconUrl { get; set; }
        }

        private static SearchCacheDto ToDto(PackageModel m) => new()
        {
            PackageId = m.PackageId,
            LatestStable = m.LatestStableVersion?.ToNormalizedString(),
            LatestPre = m.LatestPrereleaseVersion?.ToNormalizedString(),
            Description = m.Description,
            Authors = m.Authors,
            DownloadCount = m.DownloadCount,
            SourceName = m.SourceName,
            IconUrl = m.IconUrl,
        };

        private static PackageModel FromDto(SearchCacheDto d)
        {
            NuGetVersion? stable = null, pre = null;
            if (!string.IsNullOrEmpty(d.LatestStable)) NuGetVersion.TryParse(d.LatestStable, out stable);
            if (!string.IsNullOrEmpty(d.LatestPre)) NuGetVersion.TryParse(d.LatestPre, out pre);
            return new PackageModel
            {
                PackageId = d.PackageId,
                LatestStableVersion = stable,
                LatestPrereleaseVersion = pre,
                Description = d.Description,
                Authors = d.Authors,
                DownloadCount = d.DownloadCount,
                SourceName = d.SourceName,
                IconUrl = d.IconUrl,
            };
        }

        private SourceRepository GetRepository(PackageSourceModel source)
        {
            return _repositoryCache.GetOrAdd(source.Source, _ => Repository.Factory.GetCoreV3(source.Source));
        }

        private Task<PackageSearchResource> GetSearchResourceAsync(PackageSourceModel source, CancellationToken cancellationToken)
        {
            // Cache the Task<>, not the resource: GetResourceAsync may be in
            // flight when the second caller arrives, and we want both to await
            // the same outstanding request rather than fire two parallel ones.
            // The cancellation token here only affects the *first* caller; that
            // is fine because pre-warm uses its own short token.
            // VSTHRD003: the cached Task is intentionally shared across sync
            // contexts; callers don't deadlock because the underlying work is
            // pure HTTP / async and never re-enters the UI thread.
#pragma warning disable VSTHRD003
            return _searchResourceCache.GetOrAdd(source.Source, _ =>
                GetRepository(source).GetResourceAsync<PackageSearchResource>(cancellationToken));
#pragma warning restore VSTHRD003
        }

        private Task<PackageMetadataResource> GetMetadataResourceAsync(PackageSourceModel source, CancellationToken cancellationToken)
        {
#pragma warning disable VSTHRD003
            return _metadataResourceCache.GetOrAdd(source.Source, _ =>
                GetRepository(source).GetResourceAsync<PackageMetadataResource>(cancellationToken));
#pragma warning restore VSTHRD003
        }

        public async Task PrewarmSourcesAsync(CancellationToken cancellationToken)
        {
            using var _ = DiagnosticsLogger.Time("PrewarmSourcesAsync");
            var sources = GetEnabledSources();
            if (sources.Count == 0) return;

            // Hard cap so a dead private feed can't keep the prewarm task alive
            // for the full HttpClient timeout (usually 100s). The next real call
            // surfaces the failure normally.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var tasks = new List<Task>(sources.Count * 2);
            foreach (var src in sources)
            {
                tasks.Add(SafeWarmAsync(GetSearchResourceAsync(src, linked.Token)));
                tasks.Add(SafeWarmAsync(GetMetadataResourceAsync(src, linked.Token)));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static async Task SafeWarmAsync<TResource>(Task<TResource> task)
        {
            // Pre-warm intentionally awaits a task started outside the caller's
            // sync context (a cached task shared across all callers). The work
            // is pure HTTP and never re-enters the UI thread, so deadlock risk
            // is nil.
#pragma warning disable VSTHRD003
            try { await task.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
            catch (Exception ex) { DiagnosticsLogger.Warn("Source pre-warm failed: " + ex.Message); }
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
            {
                DiagnosticsLogger.Verbose($"search mem-hit '{query}'");
                return cached;
            }

            // Disk-tier hit: serve immediately and promote into the in-memory
            // cache so subsequent keystrokes also fast-path. Don't fan out to
            // the network on a hit - the next user-initiated Refresh will.
            if (!bypass)
            {
                var disk = await _searchDiskCache.ReadAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                if (disk != null)
                {
                    DiagnosticsLogger.Verbose($"search disk-hit '{query}'");
                    var hydrated = disk.Select(FromDto).ToList();
                    StoreCachedSearch(cacheKey, hydrated);
                    return hydrated;
                }
            }

            var results = new List<PackageModel>();
            var sources = GetEnabledSources();
            if (sourceNameFilter != null && sourceNameFilter.Count > 0)
            {
                var allowed = new HashSet<string>(sourceNameFilter, StringComparer.OrdinalIgnoreCase);
                sources = sources.Where(s => allowed.Contains(s.Name)).ToList();
            }

            // Fan out to all enabled sources in parallel and race each against a
            // soft 2.5s deadline. A slow / dead feed no longer blocks the healthy
            // ones - we surface whatever returned in time, then keep the slow
            // ones alive so their results can backfill the cache.
            var perSourceTimeout = TimeSpan.FromMilliseconds(2500);
            var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sourceTasks = sources
                .Select(src => RunSourceSearchAsync(src, query, includePrerelease, skip, take, perCallCts.Token))
                .ToArray();

            var completed = Task.WhenAll(sourceTasks);

            // Dedicated timeout CTS so the timer is released the moment the
            // fan-out wins (instead of leaking until the outer token fires).
            using var timeoutCts = new CancellationTokenSource();
            using var linkedTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var timeout = Task.Delay(perSourceTimeout, linkedTimeoutCts.Token);
            await Task.WhenAny(completed, timeout).ConfigureAwait(false);
            timeoutCts.Cancel();

            for (var i = 0; i < sourceTasks.Length; i++)
            {
                var t = sourceTasks[i];
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                    results.AddRange(t.Result);
            }

            var deduped = results
                .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // Cancellation guard: a query that was cancelled mid-flight returns
            // a partial / empty result set. Caching that would poison the next
            // identical search until TTL expires. Drop the write entirely - the
            // next typed keystroke (or Refresh) will fill the cache cleanly.
            if (cancellationToken.IsCancellationRequested)
            {
                DiagnosticsLogger.Verbose($"search '{query}' cancelled; cache write skipped");
                return deduped;
            }

            StoreCachedSearch(cacheKey, deduped);
            _ = _searchDiskCache.WriteAsync(cacheKey, deduped.Select(ToDto).ToList(), CancellationToken.None);

            // Backfill: when a slow source eventually returns, merge its
            // results with the partial set already cached and rewrite the
            // entry so the next keystroke gets the full picture without a
            // second network round-trip. If the outer search was cancelled
            // we cancel the in-flight requests instead of letting them run.
            _ = completed.ContinueWith(t =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        perCallCts.Cancel();
                        return;
                    }

                    if (t.Status != TaskStatus.RanToCompletion) return;

                    var merged = new List<PackageModel>(deduped);
                    var have = new HashSet<string>(deduped.Select(p => p.PackageId), StringComparer.OrdinalIgnoreCase);
                    var addedLate = false;
                    foreach (var list in t.Result)
                    {
                        if (list == null) continue;
                        foreach (var p in list)
                        {
                            if (have.Add(p.PackageId))
                            {
                                merged.Add(p);
                                addedLate = true;
                            }
                        }
                    }
                    if (addedLate)
                    {
                        StoreCachedSearch(cacheKey, merged);
                        _ = _searchDiskCache.WriteAsync(cacheKey, merged.Select(ToDto).ToList(), CancellationToken.None);
                    }
                }
                finally
                {
                    perCallCts.Dispose();
                }
            }, TaskScheduler.Default);

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
                var resource = await GetSearchResourceAsync(source, cancellationToken).ConfigureAwait(false);
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
                    //
                    // A single search hit only tells us one version, so only assert
                    // what it actually represents and leave the other slot unknown
                    // (null) for the authoritative latest-metadata enrichment to
                    // fill. Fabricating a prerelease equal to a stable hit - or a
                    // stable we never saw - is exactly what made update detection
                    // inconsistent between this path and enrichment.
                    var version = result.Identity.Version;
                    NuGet.Versioning.NuGetVersion? latestStable;
                    NuGet.Versioning.NuGetVersion? latestPre;
                    if (version == null)
                    {
                        latestStable = null;
                        latestPre = null;
                    }
                    else if (version.IsPrerelease)
                    {
                        latestStable = null;
                        latestPre = version;
                    }
                    else
                    {
                        latestStable = version;
                        latestPre = null;
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
            var key = "latest:" + (packageId ?? string.Empty);
            lock (_metadataCacheLock)
            {
                if (_metadataCache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
                    return hit.Value;
            }

            var (result, anySourceFaulted) = await GetPackageMetadataCoreAsync(packageId ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && ShouldCacheMetadataResult(result, anySourceFaulted))
                StoreMetadata(key, result);
            return result;
        }

        public async Task<InstalledEnrichment?> GetInstalledEnrichmentAsync(
            string packageId,
            NuGetVersion? installedVersion,
            bool needsDownloadCount,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(packageId)) return null;

            // When the caller only needs the "latest" half (no installed version
            // to resolve advisories for), reuse the warm latest-metadata cache so
            // a repeat enrichment pass stays a no-op round trip.
            if (installedVersion == null && TryGetCachedLatestMetadata(packageId, out var cachedLatest))
                return new InstalledEnrichment { Latest = cachedLatest };

            var (result, anySourceFaulted) = await GetInstalledEnrichmentCoreAsync(
                packageId, installedVersion, needsDownloadCount, cancellationToken).ConfigureAwait(false);

            // Seed the shared latest-metadata cache so the Updates view's
            // TryGetCachedLatestMetadata fast-path and any later latest-only
            // enrichment benefit from this single fetch.
            if (!cancellationToken.IsCancellationRequested
                && ShouldCacheMetadataResult(result?.Latest, anySourceFaulted))
            {
                StoreMetadata("latest:" + packageId, result?.Latest);
            }

            return result;
        }

        public bool TryGetCachedLatestMetadata(string packageId, out PackageModel? metadata)
        {
            metadata = null;
            if (string.IsNullOrEmpty(packageId)) return false;
            var key = "latest:" + packageId;
            lock (_metadataCacheLock)
            {
                if (_metadataCache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
                {
                    metadata = hit.Value;
                    return true;
                }
            }
            return false;
        }

        // Decides whether a metadata fan-out result is safe to cache. A non-null
        // hit is always cacheable. A null (no-match) result is only cacheable when
        // every source actually completed: if any source faulted (transient
        // network/proxy/credential failure) the package may genuinely exist there,
        // so caching the null would wrongly mark it "up to date" for the full TTL
        // even after connectivity is restored (issue #16).
        public static bool ShouldCacheMetadataResult(PackageModel? value, bool anySourceFaulted)
            => value != null || !anySourceFaulted;

        // Single source of truth for deriving the "latest" versions from a set of
        // candidate versions: the stable result is the highest non-prerelease
        // version (null when none qualify) and the prerelease result is the
        // highest version overall. Centralizing this stops the various feed paths
        // from each re-deriving it inconsistently - several previously inspected
        // only the single top version and reported "no stable update" whenever
        // that top version happened to be a prerelease, which made packages whose
        // feed lists a prerelease above the latest stable (e.g. System.Text.Json)
        // vanish from the Updates view even though a stable update existed.
        public static (NuGetVersion? Stable, NuGetVersion? Prerelease) SelectLatestVersions(
            IEnumerable<NuGetVersion> versions)
        {
            if (versions == null) return (null, null);

            NuGetVersion? stable = null;
            NuGetVersion? prerelease = null;
            foreach (var v in versions)
            {
                if (v == null) continue;
                if (prerelease == null || v > prerelease) prerelease = v;
                if (!v.IsPrerelease && (stable == null || v > stable)) stable = v;
            }
            return (stable, prerelease);
        }

        // Builds the per-major "highest version" maps used by the target-framework
        // update cap (issue #27). MaxStable tracks the highest stable version for
        // each major; MaxPrerelease tracks the highest version overall (stable or
        // prerelease) for each major, mirroring how UpdateCandidateVersion treats
        // the include-prerelease toggle.
        public static (IReadOnlyDictionary<int, NuGetVersion> Stable, IReadOnlyDictionary<int, NuGetVersion> Prerelease) BuildMaxByMajor(
            IEnumerable<NuGetVersion> versions)
        {
            var stable = new Dictionary<int, NuGetVersion>();
            var prerelease = new Dictionary<int, NuGetVersion>();
            if (versions == null) return (stable, prerelease);

            foreach (var v in versions)
            {
                if (v == null) continue;
                var major = v.Major;

                if (!prerelease.TryGetValue(major, out var curPre) || v > curPre)
                    prerelease[major] = v;

                if (!v.IsPrerelease && (!stable.TryGetValue(major, out var curStable) || v > curStable))
                    stable[major] = v;
            }

            return (stable, prerelease);
        }

        private enum SourceMetadataStatus { Found, NotFound, Faulted }

        private readonly struct SourceMetadataOutcome
        {
            private SourceMetadataOutcome(PackageModel? value, SourceMetadataStatus status)
            {
                Value = value;
                Status = status;
            }

            public PackageModel? Value { get; }
            public SourceMetadataStatus Status { get; }

            public static readonly SourceMetadataOutcome NotFound = new(null, SourceMetadataStatus.NotFound);
            public static readonly SourceMetadataOutcome Faulted = new(null, SourceMetadataStatus.Faulted);
            public static SourceMetadataOutcome Hit(PackageModel value) => new(value, SourceMetadataStatus.Found);
        }

        private async Task<(PackageModel? Value, bool AnySourceFaulted)> GetPackageMetadataCoreAsync(
            string packageId,
            CancellationToken cancellationToken)
        {
            var sources = GetEnabledSources();
            if (sources.Count == 0) return (null, false);

            // Fan out across enabled sources in parallel and return the first
            // non-null result. The serial loop this replaces could block for the
            // full timeout of a slow first source before reaching the next one,
            // which made enrichment painful when an internal feed was listed
            // ahead of nuget.org.
            using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = perCallCts.Token;
            var tasks = sources
                .Select(src => FetchLatestMetadataFromSourceAsync(src, packageId, token))
                .ToList();

            var anyFaulted = false;
            try
            {
                while (tasks.Count > 0)
                {
                    var done = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(done);
                    if (done.Status != TaskStatus.RanToCompletion) continue;
                    var outcome = done.Result;
                    if (outcome.Status == SourceMetadataStatus.Found && outcome.Value != null)
                    {
                        perCallCts.Cancel();
                        return (outcome.Value, anyFaulted);
                    }
                    if (outcome.Status == SourceMetadataStatus.Faulted) anyFaulted = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (null, anyFaulted);
        }

        private async Task<SourceMetadataOutcome> FetchLatestMetadataFromSourceAsync(
            PackageSourceModel source,
            string packageId,
            CancellationToken cancellationToken)
        {
            try
            {
                var resource = await GetMetadataResourceAsync(source, cancellationToken).ConfigureAwait(false);
                if (resource == null) return SourceMetadataOutcome.NotFound;

                var metadata = await resource.GetMetadataAsync(
                    packageId, includePrerelease: true, includeUnlisted: false,
                    _cacheContext, _logger, cancellationToken).ConfigureAwait(false);

                var allMetadata = metadata?.ToList();
                if (allMetadata == null || allMetadata.Count == 0) return SourceMetadataOutcome.NotFound;

                // The display fields (description, authors, deps, download count)
                // come from the newest version overall, but the latest stable /
                // prerelease slots are derived from the full version list so a
                // prerelease sitting above the latest stable no longer hides the
                // stable update.
                var latest = allMetadata.OrderByDescending(m => m.Identity.Version).First();
                var (latestStable, latestPrerelease) = SelectLatestVersions(
                    allMetadata.Select(m => m.Identity.Version));
                var (maxStableByMajor, maxPrereleaseByMajor) = BuildMaxByMajor(
                    allMetadata.Select(m => m.Identity.Version));

                var deps = latest.DependencySets
                    .SelectMany(ds => ds.Packages.Select(p => new PackageDependencyInfo
                    {
                        PackageId = p.Id,
                        VersionRange = p.VersionRange?.ToString() ?? "*",
                        TargetFramework = ds.TargetFramework?.GetShortFolderName() ?? string.Empty,
                    }))
                    .ToList();

                long downloadCount = await ResolveDownloadCountAsync(
                    source, packageId, latest.DownloadCount ?? 0, cancellationToken).ConfigureAwait(false);

                var (isDeprecated, deprecationReason) = await MapDeprecationAsync(latest).ConfigureAwait(false);

                return SourceMetadataOutcome.Hit(new PackageModel
                {
                    PackageId = latest.Identity.Id,
                    LatestStableVersion = latestStable,
                    LatestPrereleaseVersion = latestPrerelease,
                    MaxStableByMajor = maxStableByMajor,
                    MaxPrereleaseByMajor = maxPrereleaseByMajor,
                    Description = latest.Description,
                    Authors = latest.Authors,
                    LicenseExpression = latest.LicenseMetadata?.License,
                    LicenseUrl = latest.LicenseUrl?.ToString(),
                    DownloadCount = downloadCount,
                    SourceName = source.Name,
                    ProjectUrl = latest.ProjectUrl?.ToString(),
                    IconUrl = latest.IconUrl?.ToString(),
                    Published = latest.Published,
                    Dependencies = deps,
                    Vulnerabilities = MapVulnerabilities(latest),
                    IsDeprecated = isDeprecated,
                    DeprecationReason = deprecationReason,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Source failed (transient network/proxy/credential error). Signal
                // the fault so the caller won't cache a misleading negative result.
                return SourceMetadataOutcome.Faulted;
            }
        }

        private readonly struct InstalledEnrichmentOutcome
        {
            private InstalledEnrichmentOutcome(
                PackageModel? latest,
                IReadOnlyList<PackageVulnerabilityInfo> installedVulnerabilities,
                SourceMetadataStatus status)
            {
                Latest = latest;
                InstalledVulnerabilities = installedVulnerabilities;
                Status = status;
            }

            public PackageModel? Latest { get; }
            public IReadOnlyList<PackageVulnerabilityInfo> InstalledVulnerabilities { get; }
            public SourceMetadataStatus Status { get; }

            public static readonly InstalledEnrichmentOutcome NotFound = new(null, [], SourceMetadataStatus.NotFound);
            public static readonly InstalledEnrichmentOutcome Faulted = new(null, [], SourceMetadataStatus.Faulted);
            public static InstalledEnrichmentOutcome Found(
                PackageModel? latest, IReadOnlyList<PackageVulnerabilityInfo> installedVulnerabilities)
                => new(latest, installedVulnerabilities, SourceMetadataStatus.Found);
        }

        private async Task<(InstalledEnrichment? Value, bool AnySourceFaulted)> GetInstalledEnrichmentCoreAsync(
            string packageId,
            NuGetVersion? installedVersion,
            bool needsDownloadCount,
            CancellationToken cancellationToken)
        {
            var sources = GetEnabledSources();
            if (sources.Count == 0) return (null, false);

            // Fan out across enabled sources in parallel and take the first source
            // that actually hosts the package - matching the latest-metadata path -
            // so a slow source listed first can't stall enrichment.
            using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = perCallCts.Token;
            var tasks = sources
                .Select(src => FetchInstalledEnrichmentFromSourceAsync(
                    src, packageId, installedVersion, needsDownloadCount, token))
                .ToList();

            var anyFaulted = false;
            try
            {
                while (tasks.Count > 0)
                {
                    var done = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(done);
                    if (done.Status != TaskStatus.RanToCompletion) continue;
                    var outcome = done.Result;
                    if (outcome.Status == SourceMetadataStatus.Found)
                    {
                        perCallCts.Cancel();
                        return (new InstalledEnrichment
                        {
                            Latest = outcome.Latest,
                            InstalledVulnerabilities = outcome.InstalledVulnerabilities,
                        }, anyFaulted);
                    }
                    if (outcome.Status == SourceMetadataStatus.Faulted) anyFaulted = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (null, anyFaulted);
        }

        private async Task<InstalledEnrichmentOutcome> FetchInstalledEnrichmentFromSourceAsync(
            PackageSourceModel source,
            string packageId,
            NuGetVersion? installedVersion,
            bool needsDownloadCount,
            CancellationToken cancellationToken)
        {
            try
            {
                var resource = await GetMetadataResourceAsync(source, cancellationToken).ConfigureAwait(false);
                if (resource == null) return InstalledEnrichmentOutcome.NotFound;

                // Single registration fetch covering every version. includeUnlisted
                // is true so an unlisted *installed* version can still resolve its
                // advisories; the "latest" computation below filters back to listed
                // entries so an unlisted version is never reported as the latest.
                var allMetadata = (await resource.GetMetadataAsync(
                    packageId, includePrerelease: true, includeUnlisted: true,
                    _cacheContext, _logger, cancellationToken).ConfigureAwait(false))?.ToList();

                if (allMetadata == null || allMetadata.Count == 0) return InstalledEnrichmentOutcome.NotFound;

                // Installed-version advisories come from the full (listed +
                // unlisted) set so they mirror the dedicated versioned lookup.
                // When the exact installed version isn't on this source (unlisted,
                // removed, or only available on another feed) fall back to the
                // latest available metadata's advisories - exactly what the
                // versioned GetPackageMetadataAsync path does - so the vulnerability
                // badge stays consistent between the Installed and Vulnerable views
                // (issue #26). Without the fallback a package flagged in the
                // Vulnerable view showed no badge in the plain Installed list.
                IReadOnlyList<PackageVulnerabilityInfo> installedVulnerabilities = [];
                IPackageSearchMetadata? installedMeta = null;
                if (installedVersion != null)
                {
                    installedMeta = allMetadata.FirstOrDefault(
                        m => m?.Identity?.Version != null && m.Identity.Version.Equals(installedVersion))
                        ?? allMetadata.OrderByDescending(m => m.Identity.Version).FirstOrDefault();
                    if (installedMeta != null)
                        installedVulnerabilities = MapVulnerabilities(installedMeta);
                }

                // Latest metadata is derived from listed entries only, matching the
                // includeUnlisted:false latest-metadata path exactly.
                var listed = allMetadata.Where(m => m.IsListed).ToList();
                if (listed.Count == 0)
                {
                    // Package exists here but has no listed version: report Found
                    // (so the fan-out stops) with no "latest", but still surface any
                    // installed-version advisories we resolved above.
                    return InstalledEnrichmentOutcome.Found(null, installedVulnerabilities);
                }

                var latest = listed.OrderByDescending(m => m.Identity.Version).First();
                var (latestStable, latestPrerelease) = SelectLatestVersions(
                    listed.Select(m => m.Identity.Version));
                var (maxStableByMajor, maxPrereleaseByMajor) = BuildMaxByMajor(
                    listed.Select(m => m.Identity.Version));

                var deps = latest.DependencySets
                    .SelectMany(ds => ds.Packages.Select(p => new PackageDependencyInfo
                    {
                        PackageId = p.Id,
                        VersionRange = p.VersionRange?.ToString() ?? "*",
                        TargetFramework = ds.TargetFramework?.GetShortFolderName() ?? string.Empty,
                    }))
                    .ToList();

                // Same nuget.org-only download-count fallback as the latest path,
                // but skipped entirely when the caller already has a count for the
                // row - that removes a redundant per-package search round trip.
                long downloadCount = latest.DownloadCount ?? 0;
                if (needsDownloadCount)
                {
                    downloadCount = await ResolveDownloadCountAsync(
                        source, packageId, downloadCount, cancellationToken).ConfigureAwait(false);
                }

                // Deprecation is per-version: a package can be deprecated at the
                // installed version while a newer, non-deprecated version exists.
                // Resolve the installed version's status first so the badge stays
                // consistent with the dedicated Deprecated view, then fall back to
                // the latest version's status so a package deprecated only at its
                // newest version is still flagged (issue #31).
                var (latestDeprecated, latestReason) = await MapDeprecationAsync(latest).ConfigureAwait(false);
                bool isDeprecated = latestDeprecated;
                string? deprecationReason = latestReason;
                if (installedMeta != null && !ReferenceEquals(installedMeta, latest))
                {
                    var (installedDeprecated, installedReason) = await MapDeprecationAsync(installedMeta).ConfigureAwait(false);
                    if (installedDeprecated)
                    {
                        isDeprecated = true;
                        deprecationReason = installedReason ?? latestReason;
                    }
                }

                var latestModel = new PackageModel
                {
                    PackageId = latest.Identity.Id,
                    LatestStableVersion = latestStable,
                    LatestPrereleaseVersion = latestPrerelease,
                    MaxStableByMajor = maxStableByMajor,
                    MaxPrereleaseByMajor = maxPrereleaseByMajor,
                    Description = latest.Description,
                    Authors = latest.Authors,
                    LicenseExpression = latest.LicenseMetadata?.License,
                    LicenseUrl = latest.LicenseUrl?.ToString(),
                    DownloadCount = downloadCount,
                    SourceName = source.Name,
                    ProjectUrl = latest.ProjectUrl?.ToString(),
                    IconUrl = latest.IconUrl?.ToString(),
                    Published = latest.Published,
                    Dependencies = deps,
                    Vulnerabilities = MapVulnerabilities(latest),
                    IsDeprecated = isDeprecated,
                    DeprecationReason = deprecationReason,
                };

                return InstalledEnrichmentOutcome.Found(latestModel, installedVulnerabilities);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return InstalledEnrichmentOutcome.Faulted;
            }
        }

        private static bool IsNuGetOrgSource(PackageSourceModel source)
        {
            if (string.IsNullOrEmpty(source.Source)) return false;
            return source.Source.IndexOf("nuget.org", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Single source of truth for the cumulative-download-count fallback. The
        // PackageMetadataResource exposes per-version download counts which are
        // typically null on v3 feeds, so when we have no count we fall back to the
        // search resource - but only for nuget.org, since private / Azure Artifacts /
        // GitHub feeds don't expose cumulative counts there either and the extra
        // per-package round trip would be pure overhead on every enrichment pass.
        // Returns the original count untouched when no fallback applies or the
        // best-effort lookup fails.
        private async Task<long> ResolveDownloadCountAsync(
            PackageSourceModel source,
            string packageId,
            long currentCount,
            CancellationToken cancellationToken)
        {
            if (currentCount != 0 || !IsNuGetOrgSource(source)) return currentCount;
            try
            {
                var searchResource = await GetSearchResourceAsync(source, cancellationToken).ConfigureAwait(false);
                if (searchResource != null)
                {
                    var hits = await searchResource.SearchAsync(
                        "packageid:" + packageId,
                        new SearchFilter(includePrerelease: true),
                        0, 1, _logger, cancellationToken).ConfigureAwait(false);
                    var hit = hits?.FirstOrDefault();
                    if (hit?.DownloadCount != null) return hit.DownloadCount.Value;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* search fallback is best-effort */ }
            return currentCount;
        }

        // Projects the feed's per-version vulnerability advisories onto our model.
        // Returns an empty list when the version carries no known advisories.
        private static IReadOnlyList<PackageVulnerabilityInfo> MapVulnerabilities(IPackageSearchMetadata metadata)
        {
            var vulnerabilities = metadata.Vulnerabilities;
            if (vulnerabilities == null) return [];

            var list = new List<PackageVulnerabilityInfo>();
            foreach (var v in vulnerabilities)
            {
                if (v == null) continue;
                list.Add(new PackageVulnerabilityInfo
                {
                    Severity = v.Severity,
                    AdvisoryUrl = v.AdvisoryUrl?.ToString(),
                });
            }
            return list;
        }

        // Reads the feed's deprecation metadata for the displayed version so a
        // deprecated package can be flagged in every list view (issue #20). The
        // lookup is best-effort: a failure or a feed that doesn't expose
        // deprecation data simply leaves the package unflagged.
        private static async Task<(bool IsDeprecated, string? Reason)> MapDeprecationAsync(IPackageSearchMetadata metadata)
        {
            try
            {
                var deprecation = await metadata.GetDeprecationMetadataAsync().ConfigureAwait(false);
                if (deprecation != null)
                {
                    var reason = deprecation.Reasons != null && deprecation.Reasons.Any()
                        ? string.Join(", ", deprecation.Reasons)
                        : deprecation.Message;
                    return (true, reason);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Deprecation lookup is best-effort.
            }
            return (false, null);
        }

        public async Task<PackageModel?> GetPackageMetadataAsync(
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken)
        {
            var key = $"versioned:{packageId}@{version?.ToNormalizedString()}";
            lock (_metadataCacheLock)
            {
                if (_metadataCache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
                    return hit.Value;
            }

            var (result, anySourceFaulted) = await GetPackageMetadataCoreAsync(packageId ?? string.Empty, version!, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && ShouldCacheMetadataResult(result, anySourceFaulted))
                StoreMetadata(key, result);
            return result;
        }

        private async Task<(PackageModel? Value, bool AnySourceFaulted)> GetPackageMetadataCoreAsync(
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken)
        {
            var sources = GetEnabledSources();
            if (sources.Count == 0) return (null, false);

            var identity = new global::NuGet.Packaging.Core.PackageIdentity(packageId, version);
            using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = perCallCts.Token;
            var tasks = sources
                .Select(src => FetchVersionedMetadataFromSourceAsync(src, packageId, identity, token))
                .ToList();

            var anyFaulted = false;
            try
            {
                while (tasks.Count > 0)
                {
                    var done = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(done);
                    if (done.Status != TaskStatus.RanToCompletion) continue;
                    var outcome = done.Result;
                    if (outcome.Status == SourceMetadataStatus.Found && outcome.Value != null)
                    {
                        perCallCts.Cancel();
                        return (outcome.Value, anyFaulted);
                    }
                    if (outcome.Status == SourceMetadataStatus.Faulted) anyFaulted = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (null, anyFaulted);
        }

        private async Task<SourceMetadataOutcome> FetchVersionedMetadataFromSourceAsync(
            PackageSourceModel source,
            string packageId,
            global::NuGet.Packaging.Core.PackageIdentity identity,
            CancellationToken cancellationToken)
        {
            try
            {
                var resource = await GetMetadataResourceAsync(source, cancellationToken).ConfigureAwait(false);
                if (resource == null) return SourceMetadataOutcome.NotFound;

                // Pull the full registration index for the package once and pick
                // the matching version. This is what GetMetadataAsync(identity, ...)
                // does internally for v3, but doing it explicitly lets us fall back
                // to the latest available metadata when the exact installed version
                // isn't on this source (unlisted, removed, or only on another feed) -
                // otherwise the detail pane ends up empty for installed packages.
                var allMetadata = (await resource.GetMetadataAsync(
                    packageId, includePrerelease: true, includeUnlisted: true,
                    _cacheContext, _logger, cancellationToken).ConfigureAwait(false))?.ToList();

                if (allMetadata == null || allMetadata.Count == 0) return SourceMetadataOutcome.NotFound;

                var meta = allMetadata.FirstOrDefault(m => m.Identity.Equals(identity))
                    ?? allMetadata.OrderByDescending(m => m.Identity.Version).First();

                // The display metadata (description, deps, download count) reflects
                // the requested version, but the latest stable / prerelease slots
                // must describe the newest available versions - not the requested
                // one - so they stay consistent with the latest-metadata path.
                var (latestStable, latestPrerelease) = SelectLatestVersions(
                    allMetadata.Select(m => m.Identity.Version));
                var (maxStableByMajor, maxPrereleaseByMajor) = BuildMaxByMajor(
                    allMetadata.Select(m => m.Identity.Version));

                var deps = meta.DependencySets
                    .SelectMany(ds => ds.Packages.Select(p => new PackageDependencyInfo
                    {
                        PackageId = p.Id,
                        VersionRange = p.VersionRange?.ToString() ?? "*",
                        TargetFramework = ds.TargetFramework?.GetShortFolderName() ?? string.Empty,
                    }))
                    .ToList();

                // Same nuget.org-only guard as the latest-metadata path: skip the
                // search-resource fallback on feeds that never expose download counts.
                long downloadCount = await ResolveDownloadCountAsync(
                    source, packageId, meta.DownloadCount ?? 0, cancellationToken).ConfigureAwait(false);

                var (isDeprecated, deprecationReason) = await MapDeprecationAsync(meta).ConfigureAwait(false);

                return SourceMetadataOutcome.Hit(new PackageModel
                {
                    PackageId = meta.Identity.Id,
                    LatestStableVersion = latestStable,
                    LatestPrereleaseVersion = latestPrerelease,
                    MaxStableByMajor = maxStableByMajor,
                    MaxPrereleaseByMajor = maxPrereleaseByMajor,
                    Description = meta.Description,
                    Authors = meta.Authors,
                    LicenseExpression = meta.LicenseMetadata?.License,
                    LicenseUrl = meta.LicenseUrl?.ToString(),
                    DownloadCount = downloadCount,
                    SourceName = source.Name,
                    ProjectUrl = meta.ProjectUrl?.ToString(),
                    IconUrl = meta.IconUrl?.ToString(),
                    Dependencies = deps,
                    Vulnerabilities = MapVulnerabilities(meta),
                    IsDeprecated = isDeprecated,
                    DeprecationReason = deprecationReason,
                });
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Transient source failure: signal the fault so a misleading
                // negative result isn't cached (issue #16).
                return SourceMetadataOutcome.Faulted;
            }
        }

        public async Task<IReadOnlyList<PackageVersionInfo>> GetVersionsAsync(
            string packageId,
            bool includePrerelease,
            CancellationToken cancellationToken)
        {
            var key = $"{packageId}|{includePrerelease}";
            lock (_versionsCacheLock)
            {
                if (_versionsCache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
                    return hit.Value;
            }

            var fresh = await GetVersionsCoreAsync(packageId, includePrerelease, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
                StoreVersions(key, fresh);
            return fresh;
        }

        private async Task<IReadOnlyList<PackageVersionInfo>> GetVersionsCoreAsync(
            string packageId,
            bool includePrerelease,
            CancellationToken cancellationToken)
        {
            foreach (var source in GetEnabledSources())
            {
                try
                {
                    var resource = await GetMetadataResourceAsync(source, cancellationToken).ConfigureAwait(false);
                    if (resource == null) continue;

                    // Use PackageMetadataResource (which honors includeUnlisted: false)
                    // instead of FindPackageByIdResource.GetAllVersionsAsync. The flat
                    // container returns every version that was ever pushed, including
                    // ones the author has unlisted on NuGet.org, so the dropdown ended
                    // up showing versions that don't appear on the gallery.
                    var metadata = await resource.GetMetadataAsync(
                        packageId, includePrerelease: true, includeUnlisted: false,
                        _cacheContext, _logger, cancellationToken).ConfigureAwait(false);

                    // A source that doesn't host this package returns an empty
                    // sequence rather than throwing; fall through to the next
                    // source instead of returning an empty list as the answer.
                    if (metadata == null) continue;
                    var candidates = metadata
                        .Where(m => m?.Identity?.Version != null
                            && (includePrerelease || !m.Identity.Version.IsPrerelease))
                        .ToList();
                    if (candidates.Count == 0) continue;

                    // Resolve the per-version deprecated / vulnerable status from the
                    // registration metadata we already fetched so the dropdown can flag
                    // each version (issue #21). Vulnerabilities are embedded in the
                    // registration leaf; deprecation is read via the leaf's accessor,
                    // which doesn't require an extra round trip for v3 feeds.
                    var infos = await Task.WhenAll(candidates.Select(m =>
                        BuildVersionInfoAsync(m))).ConfigureAwait(false);

                    return infos
                        .OrderByDescending(i => i.Version)
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

        private static async Task<PackageVersionInfo> BuildVersionInfoAsync(IPackageSearchMetadata metadata)
        {
            // Derive the per-version deprecated / vulnerable status through the
            // same helpers every other feed path uses so the dropdown can't drift
            // from the list and detail views (single source of truth / DRY). Both
            // read from the registration leaf we already fetched, so neither
            // issues an extra round trip for v3 feeds.
            var isVulnerable = MapVulnerabilities(metadata).Count > 0;
            var (isDeprecated, reason) = await MapDeprecationAsync(metadata).ConfigureAwait(false);

            return new PackageVersionInfo(metadata.Identity.Version, isDeprecated, isVulnerable, reason);
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

        // Downloads and merges the bulk vulnerability index across all enabled
        // sources. Each source exposes the data through IVulnerabilityInfoResource
        // (a couple of static JSON files), so this is one cacheable fetch per
        // source instead of a registration round trip per package. Sources that
        // don't expose the resource (most private feeds) are skipped.
        public async Task<IReadOnlyDictionary<string, IReadOnlyList<PackageVulnerabilityAdvisory>>> GetVulnerabilityIndexAsync(
            CancellationToken cancellationToken)
        {
            lock (_vulnIndexLock)
            {
                if (_vulnIndexCache != null && DateTime.UtcNow < _vulnIndexExpiresUtc)
                    return _vulnIndexCache;
            }

            using var _ = DiagnosticsLogger.Time("GetVulnerabilityIndexAsync");
            var merged = new Dictionary<string, List<PackageVulnerabilityAdvisory>>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in GetEnabledSources())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var resource = await GetRepository(source)
                        .GetResourceAsync<IVulnerabilityInfoResource>(cancellationToken)
                        .ConfigureAwait(false);
                    if (resource == null) continue;

                    var result = await resource
                        .GetVulnerabilityInfoAsync(_cacheContext, _logger, cancellationToken)
                        .ConfigureAwait(false);
                    if (result == null) continue;

                    if (result.Exceptions != null)
                        DiagnosticsLogger.Warn($"Vulnerability index for '{source.Name}' reported errors: {result.Exceptions.Message}");

                    if (result.KnownVulnerabilities == null) continue;

                    foreach (var file in result.KnownVulnerabilities)
                    {
                        if (file == null) continue;
                        foreach (var entry in file)
                        {
                            if (string.IsNullOrEmpty(entry.Key) || entry.Value == null) continue;
                            if (!merged.TryGetValue(entry.Key, out var list))
                            {
                                list = new List<PackageVulnerabilityAdvisory>();
                                merged[entry.Key] = list;
                            }
                            foreach (var v in entry.Value)
                            {
                                if (v == null) continue;
                                list.Add(new PackageVulnerabilityAdvisory
                                {
                                    Severity = (int)v.Severity,
                                    AdvisoryUrl = v.Url?.ToString(),
                                    AffectedVersions = v.Versions,
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Warn($"Vulnerability index fetch failed for '{source.Name}': {ex.Message}");
                }
            }

            var final = merged.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PackageVulnerabilityAdvisory>)kvp.Value,
                StringComparer.OrdinalIgnoreCase);

            lock (_vulnIndexLock)
            {
                _vulnIndexCache = final;
                _vulnIndexExpiresUtc = DateTime.UtcNow.Add(MetadataCacheTtl);
            }

            return final;
        }

        private static IReadOnlyList<PackageSourceModel> GetEnabledSources()
        {
            var cached = _enabledSourcesCache;
            if (cached != null) return cached;

            lock (_sourcesCacheLock)
            {
                if (_enabledSourcesCache != null) return _enabledSourcesCache;

                try
                {
                    var settings = Settings.LoadDefaultSettings(root: null);
                    var provider = new PackageSourceProvider(settings);
                    _enabledSourcesCache = provider.LoadPackageSources()
                        .Where(s => s.IsEnabled)
                        .Select(s => new PackageSourceModel { Name = s.Name, Source = s.Source, IsEnabled = true })
                        .ToList();
                }
                catch
                {
                    _enabledSourcesCache = [new PackageSourceModel { Name = "nuget.org", Source = "https://api.nuget.org/v3/index.json", IsEnabled = true }];
                }

                return _enabledSourcesCache;
            }
        }

        // Cached enabled-sources snapshot. NuGet.config rarely changes mid-session,
        // and this list was previously re-read from disk on every search, every
        // metadata fetch, and every per-package enrichment fan-out (so 50+ disk
        // reads per keystroke during a Browse search).
        private static IReadOnlyList<PackageSourceModel>? _enabledSourcesCache;
        private static readonly object _sourcesCacheLock = new();

        public void Dispose() => _cacheContext.Dispose();
    }
}
