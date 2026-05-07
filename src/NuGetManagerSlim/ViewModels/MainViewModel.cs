using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.Services;

namespace NuGetManagerSlim.ViewModels
{
    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly IProjectService _projectService;
        private readonly INuGetFeedService _feedService;
        private readonly IRestoreMonitorService _restoreMonitor;

        private CancellationTokenSource? _searchCts;
        private CancellationTokenSource? _operationCts;
        private System.Timers.Timer? _debounceTimer;
        private readonly List<string> _searchHistory = [];
        private int _searchHistoryIndex = -1;
        private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private ProjectScopeModel? _selectedScope;
        [ObservableProperty] private bool _filterInstalled;
        [ObservableProperty] private bool _filterUpdates;
        [ObservableProperty] private bool _filterPrerelease;
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _isRemoteLoading;
        [ObservableProperty] private bool _isSourcePanelOpen;
        [ObservableProperty] private bool _isLogOpen;
        [ObservableProperty] private string _statusMessage = "Ready";
        [ObservableProperty] private string _emptyStateMessage = "Search for a package to get started, or toggle Installed to see what's in your project.";
        [ObservableProperty] private PackageRowViewModel? _selectedPackage;
        [ObservableProperty] private PackageDetailViewModel? _detail;

        public ObservableCollection<ProjectScopeModel> ProjectScopes { get; } = [];
        public ObservableCollection<PackageRowViewModel> Packages { get; } = [];
        public ObservableCollection<PackageSourceModel> PackageSources { get; } = [];
        public ObservableCollection<string> OperationLog { get; } = [];

        public bool HasPackages => Packages.Count > 0;
        public bool IsEmptyState => !IsLoading && Packages.Count == 0;
        public bool HasSelectedPackage => SelectedPackage != null;

        public MainViewModel(
            IProjectService projectService,
            INuGetFeedService feedService,
            IRestoreMonitorService restoreMonitor)
        {
            _projectService = projectService;
            _feedService = feedService;
            _restoreMonitor = restoreMonitor;

            _debounceTimer = new System.Timers.Timer(200) { AutoReset = false };
            _debounceTimer.Elapsed += OnDebounceElapsed;

            _restoreMonitor.RestoreStatusChanged += OnRestoreStatusChanged;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            IsLoading = true;
            try
            {
                var projects = await _projectService.GetProjectsAsync(cancellationToken);
                ProjectScopes.Clear();
                foreach (var p in projects)
                    ProjectScopes.Add(p);

                SelectedScope = ProjectScopes.Count > 0 ? ProjectScopes[0] : null;

                var sources = await _feedService.GetSourcesAsync(cancellationToken);
                PackageSources.Clear();
                foreach (var s in sources)
                    PackageSources.Add(s);
            }
            finally
            {
                IsLoading = false;
                UpdateEmptyState();
            }
        }

        partial void OnSearchTextChanged(string value)
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        partial void OnFilterInstalledChanged(bool value) => _ = ApplyFiltersAsync();
        partial void OnFilterUpdatesChanged(bool value) => _ = ApplyFiltersAsync();
        partial void OnFilterPrereleaseChanged(bool value) => _ = ApplyFiltersAsync();

        partial void OnSelectedScopeChanged(ProjectScopeModel? value)
        {
            if (value != null)
            {
                _restoreMonitor.StopMonitoring();
                _restoreMonitor.StartMonitoring(value);
            }
            _ = ApplyFiltersAsync();
        }

        partial void OnSelectedPackageChanged(PackageRowViewModel? value)
        {
            OnPropertyChanged(nameof(HasSelectedPackage));
            if (value != null)
                _ = LoadDetailAsync(value);
        }

        private void OnDebounceElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_uiContext != null)
            {
                // Marshal to the WPF UI thread captured at construction so that
                // collection updates inside SearchRemoteAsync run on the UI thread.
                // We intentionally use SynchronizationContext rather than JoinableTaskFactory
                // because this VM is unit-tested without the VS shell.
#pragma warning disable VSTHRD001
                _uiContext.Post(_ => _ = SearchRemoteAsync(), null);
#pragma warning restore VSTHRD001
            }
            else
            {
                _ = SearchRemoteAsync();
            }
        }

        private async Task SearchRemoteAsync()
        {
            var query = SearchText;
            if (string.IsNullOrWhiteSpace(query) && !FilterInstalled && !FilterUpdates)
            {
                Packages.Clear();
                UpdateEmptyState();
                return;
            }

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            if (!string.IsNullOrWhiteSpace(query))
                AddToSearchHistory(query);

            try
            {
                if (!FilterInstalled && !FilterUpdates)
                {
                    // Browse / remote search mode
                    IsRemoteLoading = true;
                    var results = await _feedService.SearchAsync(query, FilterPrerelease, 0, 50, ct);
                    ct.ThrowIfCancellationRequested();
                    ApplyRemoteResults(results);
                }
                else
                {
                    await ApplyFiltersAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // User typed more — debounce will retry
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Search failed: {ex.Message}";
                await ex.LogAsync();
            }
            finally
            {
                IsRemoteLoading = false;
            }
        }

        private async Task ApplyFiltersAsync(CancellationToken cancellationToken = default)
        {
            if (SelectedScope == null) return;

            IsLoading = true;
            try
            {
                var installed = await _projectService.GetInstalledPackagesAsync(SelectedScope, cancellationToken);
                var rows = installed.Select(p => new PackageRowViewModel(p)).ToList();

                if (!string.IsNullOrWhiteSpace(SearchText))
                    rows = rows.Where(r => r.PackageId.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

                if (FilterUpdates)
                    rows = rows.Where(r => r.HasUpdate).ToList();

                Packages.Clear();
                foreach (var row in rows.OrderBy(r => r.IsTransitive).ThenBy(r => r.PackageId))
                    Packages.Add(row);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Failed to load packages: {ex.Message}";
                await ex.LogAsync();
            }
            finally
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasPackages));
                OnPropertyChanged(nameof(IsEmptyState));
            }
        }

        private void ApplyRemoteResults(IReadOnlyList<PackageModel> results)
        {
            Packages.Clear();
            foreach (var m in results)
                Packages.Add(new PackageRowViewModel(m));

            OnPropertyChanged(nameof(HasPackages));
            OnPropertyChanged(nameof(IsEmptyState));
            UpdateEmptyState();
        }

        private async Task LoadDetailAsync(PackageRowViewModel row)
        {
            _operationCts?.Cancel();
            _operationCts = new CancellationTokenSource();

            var detail = new PackageDetailViewModel(_feedService, _projectService, msg =>
            {
                StatusMessage = msg;
                OperationLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            });

            try
            {
                await detail.LoadAsync(row, SelectedScope ?? ProjectScopeModel.EntireSolution, FilterPrerelease, _operationCts.Token);
                Detail = detail;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Failed to load package details: {ex.Message}";
                await ex.LogAsync();
            }
        }

        [RelayCommand]
        private async Task SearchAsync()
        {
            _debounceTimer?.Stop();
            await SearchRemoteAsync();
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            SearchText = string.Empty;
            OperationLog.Clear();
            await InitializeAsync(CancellationToken.None);
        }

        [RelayCommand]
        private void ToggleLog()
        {
            IsLogOpen = !IsLogOpen;
        }

        [RelayCommand]
        private async Task AddSourceAsync()
        {
            // Show inline add-source form — stub for v1
            await Task.CompletedTask;
        }

        [RelayCommand]
        private async Task QuickUpdateAsync(PackageRowViewModel? row)
        {
            if (row == null) return;
            row.IsOperationInProgress = true;
            try
            {
                StatusMessage = $"Updating {row.PackageId}…";
                if (SelectedScope?.ProjectFullPath != null && row.LatestStableVersion != null)
                    await _projectService.UpdatePackageAsync(SelectedScope.ProjectFullPath, row.PackageId, row.LatestStableVersion, CancellationToken.None);
                StatusMessage = $"✓ Updated {row.PackageId} → {row.LatestStableVersion}";
                OperationLog.Add($"[{DateTime.Now:HH:mm:ss}] ✓ Updated {row.PackageId} → {row.LatestStableVersion}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Failed to update {row.PackageId}: {ex.Message}";
                await ex.LogAsync();
            }
            finally
            {
                row.IsOperationInProgress = false;
            }
        }

        public void NavigateSearchHistory(int direction)
        {
            if (_searchHistory.Count == 0) return;
            var next = _searchHistoryIndex + direction;
            _searchHistoryIndex = next < 0 ? 0 : next >= _searchHistory.Count ? _searchHistory.Count - 1 : next;
            SearchText = _searchHistory[_searchHistoryIndex];
        }

        private void AddToSearchHistory(string query)
        {
            if (_searchHistory.LastOrDefault() == query) return;
            _searchHistory.Add(query);
            if (_searchHistory.Count > 20)
                _searchHistory.RemoveAt(0);
            _searchHistoryIndex = _searchHistory.Count;
        }

        private void UpdateEmptyState()
        {
            if (SelectedScope == null)
            {
                EmptyStateMessage = "Open a solution to manage NuGet packages.";
                return;
            }
            if (!FilterInstalled && !FilterUpdates && string.IsNullOrWhiteSpace(SearchText))
            {
                EmptyStateMessage = "Search for a package to get started, or toggle Installed to see what's in your project.";
                return;
            }
            if (FilterUpdates && Packages.Count == 0)
            {
                EmptyStateMessage = $"All packages in {SelectedScope.DisplayName} are up to date. ✓";
                return;
            }
            if (!string.IsNullOrWhiteSpace(SearchText) && Packages.Count == 0)
            {
                EmptyStateMessage = $"No packages match '{SearchText}'.";
                return;
            }
            EmptyStateMessage = string.Empty;
        }

        private void OnRestoreStatusChanged(object? sender, RestoreStatusChangedEventArgs e)
        {
            if (e.IsRestoreIncomplete)
                StatusMessage = $"⚠ Restore incomplete for {e.ProjectName} — {e.UnresolvedCount} packages unresolved.";
        }

        public void Dispose()
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _restoreMonitor.RestoreStatusChanged -= OnRestoreStatusChanged;
            _restoreMonitor.StopMonitoring();
        }
    }
}
