using System;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;
using Task = System.Threading.Tasks.Task;

namespace NuGetManagerSlim
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(
        typeof(NuGetQuickManagerToolWindow.Pane),
        Style = VsDockStyle.Tabbed,
        Window = WindowGuids.SolutionExplorer,
        Orientation = ToolWindowOrientation.Right)]
    // Drives the visibility of OpenForProjectCommand. The rule is true when the
    // active hierarchy selection (the project being right-clicked in Solution
    // Explorer) reports the "DotNet" project capability, which covers C#, VB,
    // F#, and any other managed project flavor we want to support.
    [ProvideUIContextRule(
        contextGuid: UIContextGuids.DotNetProjectContextString,
        name: "DotNetProjectContextRule",
        expression: "DotNet",
        termNames: new[] { "DotNet" },
        termValues: new[] { "ActiveProjectCapability:DotNet" })]
    [Guid(PackageGuids.NuGetManagerSlimString)]
    public sealed class NuGetManagerSlimPackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
        }
    }
}
