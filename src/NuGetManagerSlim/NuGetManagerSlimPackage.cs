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
    // Drives the visibility of OpenForProjectCommand. The rule fires when the
    // hierarchy item the user right-clicked has a .csproj / .vbproj / .fsproj
    // extension. Using HierSingleSelectionName (rather than
    // ActiveProjectCapability) matters because right-clicking a project in
    // Solution Explorer doesn't necessarily change the "active project" that
    // capability terms evaluate against - so the capability-based rule only
    // activated after the package was loaded for unrelated reasons. Selection
    // name matching is evaluated by the shell directly against the
    // right-clicked item and works before the package ever loads.
    [ProvideUIContextRule(
        contextGuid: UIContextGuids.DotNetProjectContextString,
        name: "DotNetProjectContextRule",
        expression: "DotNetProj",
        termNames: new[] { "DotNetProj" },
        termValues: new[] { "HierSingleSelectionName:\\.(cs|vb|fs)proj$" })]
    [Guid(PackageGuids.NuGetManagerSlimString)]
    public sealed class NuGetManagerSlimPackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Default per-host connection limit on .NET FW 4.8 is 2, which
            // serializes parallel HTTP traffic against the same host (icons,
            // metadata round-trips, search). Lift it once for the whole VS
            // process so our HttpClient and NuGet's protocol stack can
            // actually run requests in parallel.
            try
            {
                if (System.Net.ServicePointManager.DefaultConnectionLimit < 24)
                    System.Net.ServicePointManager.DefaultConnectionLimit = 24;
            }
            catch { }

            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
        }
    }
}
