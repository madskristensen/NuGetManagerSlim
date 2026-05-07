using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    public interface IMruPackageService
    {
        Task<IReadOnlyList<PackageModel>> GetRecentAsync(CancellationToken cancellationToken);

        Task RecordAsync(PackageModel package, CancellationToken cancellationToken);
    }
}
