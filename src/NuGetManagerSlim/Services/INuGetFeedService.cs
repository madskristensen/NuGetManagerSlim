using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    public interface INuGetFeedService
    {
        Task<IReadOnlyList<PackageModel>> SearchAsync(
            string query,
            bool includePrerelease,
            int skip,
            int take,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? sourceNameFilter = null);

        /// <summary>
        /// Returns cached search results synchronously when available. Used by the
        /// view model to render the package list without a transient skeleton flash
        /// when the same search has been issued recently.
        /// </summary>
        bool TryGetCachedSearch(
            string query,
            bool includePrerelease,
            int skip,
            int take,
            IReadOnlyCollection<string>? sourceNameFilter,
            out IReadOnlyList<PackageModel> results);

        Task<PackageModel?> GetPackageMetadataAsync(
            string packageId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Returns the cached "latest" metadata for <paramref name="packageId"/> when
        /// available so the Updates view can render rows that were already enriched by
        /// a prior Browse / Installed pass without a fresh round trip.
        /// </summary>
        bool TryGetCachedLatestMetadata(string packageId, out PackageModel? metadata);

        Task<PackageModel?> GetPackageMetadataAsync(
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(
            string packageId,
            bool includePrerelease,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<PackageSourceModel>> GetSourcesAsync(
            CancellationToken cancellationToken);

        Task<string?> GetReadmeAsync(
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken);

        /// <summary>
        /// Drops all in-memory feed caches and forces the next search to bypass
        /// the NuGet HTTP cache so the user-initiated Refresh action always
        /// reaches the live feed.
        /// </summary>
        void InvalidateCache();

        /// <summary>
        /// Resolves and caches the per-source NuGet protocol resources
        /// (PackageSearchResource / PackageMetadataResource) so the first user
        /// search isn't paying for service-index download, resource lookup, TLS
        /// handshake, and credential prompts on the critical path. Safe to call
        /// multiple times; subsequent calls are no-ops once warm.
        /// </summary>
        Task PrewarmSourcesAsync(CancellationToken cancellationToken);
    }
}
