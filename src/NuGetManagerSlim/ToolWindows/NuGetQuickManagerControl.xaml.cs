using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

            // Reset the detail pane scroll position whenever a new package is
            // loaded so the user always lands at the title - otherwise the
            // pane retains the previous package's scroll offset and may open
            // mid-dependency-list.
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Group rows by GroupKey so transitive packages render under a
            // dedicated "Transitive packages" header. The header for the
            // default "Packages" group is collapsed in XAML so the Browse
            // view (no transitives) reads as a flat list.
            var view = CollectionViewSource.GetDefaultView(viewModel.Packages);
            if (view != null && view.GroupDescriptions != null && view.GroupDescriptions.Count == 0)
            {
                view.GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(PackageRowViewModel.GroupKey)));
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.Detail))
            {
                DetailScrollViewer?.ScrollToTop();
            }
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
