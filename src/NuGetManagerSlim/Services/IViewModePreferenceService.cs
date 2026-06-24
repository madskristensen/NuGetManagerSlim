using System.Threading;
using System.Threading.Tasks;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.Services
{
    public interface IViewModePreferenceService
    {
        Task<PackageViewMode?> GetAsync(CancellationToken cancellationToken);

        Task SaveAsync(PackageViewMode viewMode, CancellationToken cancellationToken);
    }
}
