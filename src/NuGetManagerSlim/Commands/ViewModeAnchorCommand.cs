using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;

namespace NuGetManagerSlim.Commands
{
    // Permanent, neutral anchor for the view-mode menu controller. It carries the
    // funnel icon shown on the toolbar (pinned via the FixMenuController flag in
    // the VSCT) so Visual Studio never swaps the anchor to the last-invoked child
    // command. That keeps the toolbar from contradicting the dropdown check mark,
    // which is driven by MainViewModel.ViewMode (issues #23 / #28). Clicking the
    // anchor body is a no-op; the dropdown arrow opens the list of modes.
    [Command(PackageIds.ViewModeAnchorCommand)]
    internal sealed class ViewModeAnchorCommand : BaseCommand<ViewModeAnchorCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            var vm = NuGetQuickManagerToolWindow.Pane.CurrentViewModel;
            Command.Visible = true;
            Command.Enabled = vm?.HasProject == true;
        }

        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => Task.CompletedTask;
    }
}
