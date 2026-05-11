using System.Windows;
using Microsoft.VisualStudio.PlatformUI;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.ToolWindows
{
    public partial class SolutionProjectPickerDialog : DialogWindow
    {
        public SolutionProjectPickerDialog(SolutionProjectPickerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
