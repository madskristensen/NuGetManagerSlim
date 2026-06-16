using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.OpenForSolutionCommand)]
    internal sealed class OpenForSolutionCommand : BaseCommand<OpenForSolutionCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            // Match the built-in NuGet Package Manager behavior by disabling the
            // command while a solution build is in progress.
            Command.Enabled = !KnownUIContexts.SolutionBuildingContext.IsActive;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await NuGetQuickManagerToolWindow.ShowAsync();

                var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
                if (vm != null)
                {
                    await vm.SetSolutionScopeAsync();
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}
