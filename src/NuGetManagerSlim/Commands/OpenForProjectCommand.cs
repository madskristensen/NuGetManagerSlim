using System;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.OpenForProjectCommand)]
    internal sealed class OpenForProjectCommand : BaseCommand<OpenForProjectCommand>
    {
        // Visibility is controlled declaratively via <VisibilityConstraints> in
        // VSCommandTable.vsct combined with a ProvideUIContextRule attribute on
        // NuGetManagerSlimPackage that activates when the selected project has
        // the .NET project capability. No BeforeQueryStatus override is needed.

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                var project = (await VS.Solutions.GetActiveItemAsync()) as Project;
                var projectPath = project?.FullPath;
                if (string.IsNullOrEmpty(projectPath)) return;

                var displayName = project!.Name;
                if (string.IsNullOrEmpty(displayName) || displayName!.IndexOfAny(new[] { '\\', '/' }) >= 0)
                    displayName = Path.GetFileNameWithoutExtension(projectPath);

                await NuGetQuickManagerToolWindow.ShowAsync();

                var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
                if (vm != null)
                {
                    await vm.SetCurrentProjectAsync(projectPath!, displayName);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}
