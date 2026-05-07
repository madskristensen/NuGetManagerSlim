using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.ViewModeBrowseCommand)]
    internal sealed class ViewModeBrowseCommand : BaseCommand<ViewModeBrowseCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
            Command.Visible = true;
            Command.Enabled = vm != null;
            Command.Checked = vm?.ViewMode == PackageViewMode.Browse;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
                if (vm != null)
                {
                    vm.ViewMode = PackageViewMode.Browse;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}
