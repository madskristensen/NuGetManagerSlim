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
            DataContext = viewModel;
        }

        private void PackageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PackageListBox.SelectedItem != null)
                PackageListBox.ScrollIntoView(PackageListBox.SelectedItem);
        }

        private void PackageIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (sender is Image image)
            {
                image.Visibility = Visibility.Collapsed;
            }
        }
    }
}
