using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    public interface IProjectService
    {
        Task<IReadOnlyList<PackageModel>> GetInstalledPackagesAsync(
            ProjectScopeModel scope,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<PackageModel>> GetTransitivePackagesAsync(
            ProjectScopeModel scope,
            CancellationToken cancellationToken);

        // Returns the installed version of `packageId` in each of the
        // projects covered by `scope`. Projects without the package are
        // included with a null version so the caller can render an
        // "(not installed)" row in a per-project picker.
        Task<IReadOnlyDictionary<string, NuGetVersion?>> GetInstalledVersionsPerProjectAsync(
            ProjectScopeModel scope,
            string packageId,
            CancellationToken cancellationToken);

        Task InstallPackageAsync(
            string projectPath,
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken);

        Task UpdatePackageAsync(
            string projectPath,
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken);

        Task UninstallPackageAsync(
            string projectPath,
            string packageId,
            CancellationToken cancellationToken);
    }
}
