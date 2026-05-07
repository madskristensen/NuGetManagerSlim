using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.ViewModeInstalledCommand)]
    internal sealed class ViewModeInstalledCommand : BaseCommand<ViewModeInstalledCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
            Command.Visible = true;
            Command.Enabled = vm != null;
            Command.Checked = vm?.ViewMode == PackageViewMode.Installed;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
                if (vm != null)
                {
                    vm.ViewMode = PackageViewMode.Installed;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}
