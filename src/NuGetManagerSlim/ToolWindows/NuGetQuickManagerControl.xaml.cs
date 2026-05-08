using System.Windows;
using System.Windows.Controls;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.ToolWindows
{
    public partial class NuGetQuickManagerControl : UserControl
    {
        private readonly MainViewModel _viewModel;

        public NuGetQuickManagerControl(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            // The control's ctor runs on the WPF UI thread, so this is the
            // canonical place to give the VM a deterministic dispatcher.
            viewModel.AttachDispatcher(Dispatcher);
            DataContext = viewModel;
        }

        private void PackageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PackageListBox.SelectedItem != null)
                PackageListBox.ScrollIntoView(PackageListBox.SelectedItem);
        }

        private void PackageIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // Late-stage WPF decode failure (rare - the IconCacheService
            // already filters out null results). Flip HasIcon on the row VM
            // so the placeholder reappears and the failure survives container
            // recycling under virtualization. Manipulating the visual tree
            // directly (the previous behavior) was lost the moment the row
            // scrolled out of view.
            if (sender is Image image && image.DataContext is ViewModels.PackageRowViewModel row)
            {
                row.MarkIconFailed();
            }
        }
    }
}
