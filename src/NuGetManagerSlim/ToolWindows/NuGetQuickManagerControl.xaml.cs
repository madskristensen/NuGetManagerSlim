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

        // Remembers the user's last splitter-resized detail-pane height so we
        // can restore it after the pane was hidden (no selection) and shown
        // again. Falls back to the XAML-default GridLength on first show.
        private GridLength _lastDetailRowHeight;
        private GridLength _lastDetailRowMaxHeight;

        public NuGetQuickManagerControl(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _lastDetailRowHeight = DetailRow.Height;
            _lastDetailRowMaxHeight = new GridLength(DetailRow.MaxHeight);
            // The control's ctor runs on the WPF UI thread, so this is the
            // canonical place to give the VM a deterministic dispatcher.
            viewModel.AttachDispatcher(Dispatcher);
            DataContext = viewModel;

            // Reset the detail pane scroll position whenever a new package is
            // loaded so the user always lands at the title - otherwise the
            // pane retains the previous package's scroll offset and may open
            // mid-dependency-list.
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            ApplyDetailPaneVisibility(viewModel.HasDetailPane);

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
            else if (e.PropertyName == nameof(MainViewModel.CurrentProject))
            {
                // Reset the package list scroll position when the user
                // switches projects so the new project's list is read from
                // the top instead of inheriting the previous list's offset.
                ScrollPackageListToTop();
            }
            else if (e.PropertyName == nameof(MainViewModel.HasDetailPane)
                  || e.PropertyName == nameof(MainViewModel.HasSelectedPackage)
                  || e.PropertyName == nameof(MainViewModel.HasMultiSelection))
            {
                ApplyDetailPaneVisibility(_viewModel.HasDetailPane);
            }
        }

        // Toggles the detail row's grid height alongside the Border's
        // Visibility binding. Without this the row would still reserve its
        // 180px even when no package is selected.
        private void ApplyDetailPaneVisibility(bool hasDetail)
        {
            if (hasDetail)
            {
                if (DetailRow.Height.Value <= 0)
                {
                    DetailRow.MinHeight = 100;
                    DetailRow.MaxHeight = _lastDetailRowMaxHeight.Value;
                    DetailRow.Height = _lastDetailRowHeight.Value > 0
                        ? _lastDetailRowHeight
                        : new GridLength(180);
                }
            }
            else
            {
                if (DetailRow.Height.Value > 0)
                {
                    _lastDetailRowHeight = DetailRow.Height;
                }
                DetailRow.MinHeight = 0;
                DetailRow.MaxHeight = 0;
                DetailRow.Height = new GridLength(0);
            }
        }

        private void ScrollPackageListToTop()
        {
            if (PackageListBox == null) return;
            var scrollViewer = FindDescendant<ScrollViewer>(PackageListBox);
            scrollViewer?.ScrollToTop();
        }

        private static T? FindDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
        {
            if (root == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var deeper = FindDescendant<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private void PackageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PackageListBox.SelectedItem != null)
                PackageListBox.ScrollIntoView(PackageListBox.SelectedItem);

            // ListBox.SelectedItems isn't bindable; push the current set to
            // the VM so multi-select drives the bulk-action detail pane and
            // single-select keeps the per-package detail pane.
            var rows = new System.Collections.Generic.List<PackageRowViewModel>(PackageListBox.SelectedItems.Count);
            foreach (var item in PackageListBox.SelectedItems)
            {
                if (item is PackageRowViewModel row) rows.Add(row);
            }
            _viewModel.SetSelectedPackages(rows);
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
