using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.Internal.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using NuGetManagerSlim.Services;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.ToolWindows
{
    public class NuGetQuickManagerToolWindow : BaseToolWindow<NuGetQuickManagerToolWindow>
    {
        // CreateAsync runs before the Pane constructor, so we stage the session
        // here and let the Pane pick it up when it's instantiated.
        private static (MainViewModel ViewModel, NuGetFeedService FeedService, RestoreMonitorService RestoreMonitor)? _pendingSession;

        public override string GetTitle(int toolWindowId) => "NuGet Quick Manager";

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            var projectService = new ProjectService();
            var feedService = new NuGetFeedService();
            var restoreMonitor = new RestoreMonitorService();

            var viewModel = new MainViewModel(projectService, feedService, restoreMonitor);
            await viewModel.InitializeAsync(cancellationToken);

            var session = (viewModel, feedService, restoreMonitor);
            if (Pane.Instance != null)
            {
                Pane.Instance.AttachSession(viewModel, feedService, restoreMonitor);
            }
            else
            {
                _pendingSession = session;
            }

            return new NuGetQuickManagerControl(viewModel);
        }

        private static bool IsManagedDotNetProject(string? fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            var ext = System.IO.Path.GetExtension(fullPath);
            return string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".vbproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase);
        }

        [Guid("07c4db86-767a-46e3-882f-e2935e2167be")]
        internal class Pane : ToolWindowPane
        {
            internal static MainViewModel? CurrentViewModel { get; set; }
            internal static Pane? Instance { get; private set; }

            private MainViewModel? _viewModel;
            private NuGetFeedService? _feedService;
            private RestoreMonitorService? _restoreMonitor;
            private PropertyChangedEventHandler? _viewModelPropertyChanged;
            private Action? _solutionClosedHandler;
            private EventHandler<SelectionChangedEventArgs>? _selectionChangedHandler;

            public Pane()
            {
                Instance = this;
                BitmapImageMoniker = KnownMonikers.NuGet;

                ToolBar = new CommandID(
                    PackageGuids.NuGetManagerSlim,
                    PackageIds.NuGetQuickManagerToolbar);
                ToolBarLocation = (int)VSTWT_LOCATION.VSTWT_TOP;

                // CreateAsync may have completed before this constructor ran;
                // pick up the staged session if so.
                var pending = _pendingSession;
                if (pending.HasValue)
                {
                    _pendingSession = null;
                    AttachSession(pending.Value.ViewModel, pending.Value.FeedService, pending.Value.RestoreMonitor);
                }
            }

            internal void AttachSession(MainViewModel viewModel, NuGetFeedService feedService, RestoreMonitorService restoreMonitor)
            {
                _viewModel = viewModel;
                _feedService = feedService;
                _restoreMonitor = restoreMonitor;
                CurrentViewModel = viewModel;

                _viewModelPropertyChanged = (s, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.CurrentProject))
                    {
                        var project = viewModel.CurrentProject;
                        Caption = project == null
                            ? "NuGet Quick Manager"
                            : $"NuGet Quick Manager - {project.DisplayName}";
                    }
                };
                viewModel.PropertyChanged += _viewModelPropertyChanged;

                _solutionClosedHandler = () =>
                {
                    try
                    {
                        viewModel.ClearCurrentProject();
                    }
                    catch (Exception ex)
                    {
                        _ = ex.LogAsync();
                    }
                };
                VS.Events.SolutionEvents.OnAfterCloseSolution += _solutionClosedHandler;

                _selectionChangedHandler = (s, e) =>
                {
                    _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        try
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            var project = await VS.Solutions.GetActiveProjectAsync();
                            if (project == null || string.IsNullOrEmpty(project.FullPath)) return;
                            if (!IsManagedDotNetProject(project.FullPath)) return;
                            if (string.Equals(viewModel.CurrentProject?.ProjectFullPath, project.FullPath, StringComparison.OrdinalIgnoreCase))
                                return;

                            var displayName = project.Name;
                            if (string.IsNullOrEmpty(displayName) || displayName!.IndexOfAny(new[] { '\\', '/' }) >= 0)
                                displayName = System.IO.Path.GetFileNameWithoutExtension(project.FullPath);

                            await viewModel.SetCurrentProjectAsync(project.FullPath!, displayName);
                        }
                        catch (Exception ex)
                        {
                            await ex.LogAsync();
                        }
                    });
                };
                VS.Events.SelectionEvents.SelectionChanged += _selectionChangedHandler;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try
                    {
                        if (_viewModelPropertyChanged != null && _viewModel != null)
                            _viewModel.PropertyChanged -= _viewModelPropertyChanged;
                        if (_solutionClosedHandler != null)
                            VS.Events.SolutionEvents.OnAfterCloseSolution -= _solutionClosedHandler;
                        if (_selectionChangedHandler != null)
                            VS.Events.SelectionEvents.SelectionChanged -= _selectionChangedHandler;

                        _viewModel?.Dispose();
                        _feedService?.Dispose();
                        _restoreMonitor?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _ = ex.LogAsync();
                    }
                    finally
                    {
                        if (ReferenceEquals(CurrentViewModel, _viewModel))
                            CurrentViewModel = null;
                        if (ReferenceEquals(Instance, this))
                            Instance = null;

                        _viewModelPropertyChanged = null;
                        _solutionClosedHandler = null;
                        _selectionChangedHandler = null;
                        _viewModel = null;
                        _feedService = null;
                        _restoreMonitor = null;
                    }
                }

                base.Dispose(disposing);
            }

            public override bool SearchEnabled => true;

            public override IVsEnumWindowSearchFilters SearchFiltersEnum
            {
                get
                {
                    var filters = new List<IVsWindowSearchFilter>();
                    var vm = CurrentViewModel;
                    if (vm != null)
                    {
                        foreach (var source in vm.PackageSources)
                        {
                            filters.Add(new WindowSearchSimpleFilter(
                                source.Name,
                                $"Limit results to the '{source.Name}' source",
                                "source",
                                source.Name));
                        }
                    }
                    return new WindowSearchFilterEnumerator(filters);
                }
            }

            public override void ProvideSearchSettings(IVsUIDataSource pSearchSettings)
            {
                Utilities.SetValue(
                    pSearchSettings,
                    SearchSettingsDataSource.SearchStartTypeProperty.Name,
                    (uint)VSSEARCHSTARTTYPE.SST_INSTANT);

                Utilities.SetValue(
                    pSearchSettings,
                    SearchSettingsDataSource.SearchProgressTypeProperty.Name,
                    (uint)VSSEARCHPROGRESSTYPE.SPT_INDETERMINATE);

                Utilities.SetValue(
                    pSearchSettings,
                    SearchSettingsDataSource.SearchWatermarkProperty.Name,
                    "Search packages");

                // Let the search control stretch to fill the entire title-bar width
                // instead of the default 400px right-aligned chrome.
                Utilities.SetValue(
                    pSearchSettings,
                    SearchSettingsDataSource.ControlMaxWidthProperty.Name,
                    (uint)10000);
            }

            public override IVsSearchTask? CreateSearch(uint dwCookie, IVsSearchQuery pSearchQuery, IVsSearchCallback pSearchCallback)
            {
                if (pSearchQuery == null || pSearchCallback == null)
                {
                    return null;
                }

                return new SearchTask(dwCookie, pSearchQuery, pSearchCallback, this);
            }

            public override void ClearSearch()
            {
                if (CurrentViewModel != null)
                {
                    CurrentViewModel.SearchText = string.Empty;
                }
            }

            private sealed class SearchTask : VsSearchTask
            {
                private readonly Pane _pane;

                public SearchTask(uint dwCookie, IVsSearchQuery pSearchQuery, IVsSearchCallback pSearchCallback, Pane pane)
                    : base(dwCookie, pSearchQuery, pSearchCallback)
                {
                    _pane = pane;
                }

                protected override void OnStartSearch()
                {
                    try
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        var vm = CurrentViewModel;
                        if (vm != null)
                        {
                            // CreateSearch is invoked on the UI thread by the VS search
                            // infrastructure, so update the ViewModel directly without
                            // marshaling. The previous JoinableTaskFactory.Run wrapper
                            // synchronously blocked the UI thread for no benefit.
                            vm.SearchText = SearchQuery.SearchString ?? string.Empty;
                            SearchResults = (uint)vm.Packages.Count;
                        }
                        else
                        {
                            SearchResults = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                        ErrorCode = Microsoft.VisualStudio.VSConstants.E_FAIL;
                    }

                    base.OnStartSearch();
                }

                protected override void OnStopSearch()
                {
                    SearchResults = 0;
                }
            }
        }
    }
}
