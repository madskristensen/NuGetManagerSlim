using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.FilterInstalledCommand)]
    internal sealed class FilterInstalledCommand : BaseCommand<FilterInstalledCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
            Command.Visible = true;
            Command.Enabled = vm != null;
            Command.Checked = vm?.FilterInstalled ?? false;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
                if (vm != null)
                {
                    vm.FilterInstalled = !vm.FilterInstalled;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}
