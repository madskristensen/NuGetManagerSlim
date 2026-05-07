using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                _viewModel.NavigateSearchHistory(e.Key == Key.Up ? -1 : 1);
                e.Handled = true;
            }
        }

        private void PackageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PackageListBox.SelectedItem != null)
                PackageListBox.ScrollIntoView(PackageListBox.SelectedItem);
        }
    }
}
