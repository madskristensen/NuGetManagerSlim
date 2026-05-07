using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.OpenForProjectCommand)]
    internal sealed class OpenForProjectCommand : BaseCommand<OpenForProjectCommand>
    {
        // Cached visibility, refreshed asynchronously so BeforeQueryStatus
        // never blocks the UI thread on solution/DTE access.
        private static volatile bool _cachedVisible;
        private static int _refreshing;

        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Visible = _cachedVisible;
            Command.Enabled = _cachedVisible;

            // Fire-and-forget refresh; next query cycle will see the new value.
            // Guarded so concurrent BeforeQueryStatus calls don't pile up work.
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            {
                // FileAndForget is the recommended fault-handling pattern for
                // genuinely fire-and-forget JoinableTask work; the analyzer
                // can't see that the result is consumed.
#pragma warning disable VSSDK007
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        var item = await VS.Solutions.GetActiveItemAsync();
                        _cachedVisible = item is Project project && IsManagedDotNetProject(project.FullPath);
                    }
                    catch (Exception ex)
                    {
                        _cachedVisible = false;
                        await ex.LogAsync();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshing, 0);
                    }
                }).FileAndForget("vs/nugetmanagerslim/openforprojectcommand/refresh");
#pragma warning restore VSSDK007
            }
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
