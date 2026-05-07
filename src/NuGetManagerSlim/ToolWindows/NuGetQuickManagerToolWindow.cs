using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using NuGetManagerSlim.Services;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.ToolWindows
{
    public class NuGetQuickManagerToolWindow : BaseToolWindow<NuGetQuickManagerToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "NuGet Quick Manager";

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            // All async initialization runs here, before the WPF control is created.
            var projectService = new ProjectService();
            var feedService = new NuGetFeedService();
            var restoreMonitor = new RestoreMonitorService();

            var viewModel = new MainViewModel(projectService, feedService, restoreMonitor);
            await viewModel.InitializeAsync(cancellationToken);

            return new NuGetQuickManagerControl(viewModel);
        }

        [Guid("07c4db86-767a-46e3-882f-e2935e2167be")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.NuGet;
            }
        }
    }
}
