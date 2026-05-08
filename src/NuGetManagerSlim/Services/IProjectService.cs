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
