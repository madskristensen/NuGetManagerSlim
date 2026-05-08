using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.RefreshCommand)]
    internal sealed class RefreshCommand : BaseCommand<RefreshCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
            Command.Enabled = vm?.HasProject == true;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
                if (vm?.RefreshCommand?.CanExecute(null) == true)
                {
                    vm.RefreshCommand.Execute(null);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}
