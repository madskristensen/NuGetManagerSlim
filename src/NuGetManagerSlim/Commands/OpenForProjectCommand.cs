using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.OpenForProjectCommand)]
    internal sealed class OpenForProjectCommand : BaseCommand<OpenForProjectCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            var visible = false;
            try
            {
                var item = ThreadHelper.JoinableTaskFactory.Run(async () =>
                    await VS.Solutions.GetActiveItemAsync());
                if (item is Project project && IsManagedDotNetProject(project.FullPath))
                {
                    visible = true;
                }
            }
            catch (Exception ex)
            {
                _ = ex.LogAsync();
            }

            Command.Visible = visible;
            Command.Enabled = visible;
        }

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

        private static bool IsManagedDotNetProject(string? fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            var ext = Path.GetExtension(fullPath);
            return string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".vbproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase);
        }
    }
}
