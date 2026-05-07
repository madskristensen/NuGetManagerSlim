using System;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.Commands;
using NuGetManagerSlim.ToolWindows;
using Task = System.Threading.Tasks.Task;

namespace NuGetManagerSlim
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(
        typeof(NuGetQuickManagerToolWindow.Pane),
        Style = VsDockStyle.Tabbed,
        Window = WindowGuids.SolutionExplorer,
        Orientation = ToolWindowOrientation.Right)]
    [Guid(PackageGuids.PackageGuidString)]
    public sealed class NuGetManagerSlimPackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
        }
    }
}
