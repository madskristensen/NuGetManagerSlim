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
        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Once the package is loaded, this command's OleMenuCommand becomes the
            // authority for its own visibility, so the <VisibilityConstraints>
            // UIContext rule in the .vsct no longer governs it. Replicate that rule
            // here so the command stays scoped to .cs/.vb/.fsproj selections.
            var dotNetProjectContext = UIContext.FromUIContextGuid(new Guid(UIContextGuids.DotNetProjectContextString));
            Command.Visible = dotNetProjectContext.IsActive;

            // Match the built-in NuGet Package Manager behavior by disabling the
            // command while a solution build is in progress.
            Command.Enabled = !KnownUIContexts.SolutionBuildingContext.IsActive;
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
    }
}
