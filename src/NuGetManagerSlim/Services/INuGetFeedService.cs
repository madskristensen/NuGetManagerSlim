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
            CancellationToken cancellationToken);

        Task<PackageModel?> GetPackageMetadataAsync(
            string packageId,
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
    }
}
