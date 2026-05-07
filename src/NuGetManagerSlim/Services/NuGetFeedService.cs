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
        private readonly SourceCacheContext _cacheContext = new();
        private readonly ILogger _logger = NullLogger.Instance;

        public async Task<IReadOnlyList<PackageModel>> SearchAsync(
            string query,
            bool includePrerelease,
            int skip,
            int take,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? sourceNameFilter = null)
        {
            var results = new List<PackageModel>();
            var sources = GetEnabledSources();
            if (sourceNameFilter != null && sourceNameFilter.Count > 0)
            {
                var allowed = new HashSet<string>(sourceNameFilter, StringComparer.OrdinalIgnoreCase);
                sources = sources.Where(s => allowed.Contains(s.Name)).ToList();
            }

            foreach (var source in sources)
            {
                try
                {
                    var repository = Repository.Factory.GetCoreV3(source.Source);
                    var resource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken);
                    if (resource == null) continue;

                    var searchFilter = new SearchFilter(includePrerelease: includePrerelease);
                    var searchResults = await resource.SearchAsync(
                        query, searchFilter, skip, take, _logger, cancellationToken);

                    foreach (var result in searchResults)
                    {
                        var versions = (await result.GetVersionsAsync()).ToList();
                        var latestStable = versions
                            .Where(v => !v.Version.IsPrerelease)
                            .OrderByDescending(v => v.Version)
                            .FirstOrDefault()?.Version;
                        var latestPre = versions
                            .OrderByDescending(v => v.Version)
                            .FirstOrDefault()?.Version;

                        results.Add(new PackageModel
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
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Source unreachable — caller can check source status separately
                }
            }

            return results
                .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
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
                    var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken);
                    if (resource == null) continue;

                    var metadata = await resource.GetMetadataAsync(
                        packageId, includePrerelease: true, includeUnlisted: false,
                        _cacheContext, _logger, cancellationToken);

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
                    var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken);
                    if (resource == null) continue;

                    var meta = await resource.GetMetadataAsync(identity, _cacheContext, _logger, cancellationToken);
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
                    var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
                    if (resource == null) continue;

                    var versions = await resource.GetAllVersionsAsync(packageId, _cacheContext, _logger, cancellationToken);
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
