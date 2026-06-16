using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.ViewModeVulnerableCommand)]
    internal sealed class ViewModeVulnerableCommand : BaseCommand<ViewModeVulnerableCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
            Command.Visible = true;
            Command.Enabled = vm?.HasProject == true;
            Command.Checked = vm?.ViewMode == PackageViewMode.Vulnerable;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
                if (vm != null)
                {
                    vm.ViewMode = PackageViewMode.Vulnerable;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}
