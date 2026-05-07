using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.ToolWindows;

namespace NuGetManagerSlim.Commands
{
    [Command(PackageIds.OpenNuGetQuickManagerCommand)]
    internal sealed class OpenToolWindowCommand : BaseCommand<OpenToolWindowCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await NuGetQuickManagerToolWindow.ShowAsync();
        }
    }
}
