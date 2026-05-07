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
    public enum PackageViewMode
    {
        Browse,
        Installed,
        Updates,
    }

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
        [ObservableProperty] private ProjectScopeModel? _currentProject;
        [ObservableProperty] private bool _filterInstalled = true;
        [ObservableProperty] private bool _filterUpdates;
        [ObservableProperty] private bool _filterPrerelease;
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _isRemoteLoading;
        [ObservableProperty] private bool _isSourcePanelOpen;
        [ObservableProperty] private bool _isLogOpen;
        [ObservableProperty] private string _statusMessage = "Ready";
        [ObservableProperty] private string _emptyStateMessage = "Open a project context menu and pick 'Manage NuGet Packages' to get started.";
        [ObservableProperty] private PackageRowViewModel? _selectedPackage;
        [ObservableProperty] private PackageDetailViewModel? _detail;

        public ObservableCollection<PackageRowViewModel> Packages { get; } = [];
        public ObservableCollection<PackageSourceModel> PackageSources { get; } = [];
        public ObservableCollection<string> OperationLog { get; } = [];

        public bool HasPackages => Packages.Count > 0;
        public bool IsEmptyState => !IsLoading && Packages.Count == 0;
        public bool HasSelectedPackage => SelectedPackage != null;
        public bool HasProject => CurrentProject != null;

        public PackageViewMode ViewMode
        {
            get
            {
                if (FilterUpdates) return PackageViewMode.Updates;
                if (FilterInstalled) return PackageViewMode.Installed;
                return PackageViewMode.Browse;
            }
            set
            {
                FilterUpdates = value == PackageViewMode.Updates;
                FilterInstalled = value == PackageViewMode.Installed || value == PackageViewMode.Updates;
                OnPropertyChanged();
            }
        }

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

        public async Task SetCurrentProjectAsync(string projectFullPath, string projectDisplayName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(projectFullPath)) return;
            CurrentProject = new ProjectScopeModel
            {
                DisplayName = projectDisplayName,
                ProjectFullPath = projectFullPath,
            };
            await ReloadPackagesAsync();
        }

        public void ClearCurrentProject()
        {
            CurrentProject = null;
            Packages.Clear();
            Detail = null;
            SelectedPackage = null;
            UpdateEmptyState();
            OnPropertyChanged(nameof(HasPackages));
            OnPropertyChanged(nameof(IsEmptyState));
        }

        partial void OnSearchTextChanged(string value)
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        partial void OnFilterInstalledChanged(bool value)
        {
            OnPropertyChanged(nameof(ViewMode));
            _ = ApplyFiltersAsync();
        }
        partial void OnFilterUpdatesChanged(bool value)
        {
            OnPropertyChanged(nameof(ViewMode));
            _ = ApplyFiltersAsync();
        }
        partial void OnFilterPrereleaseChanged(bool value) => _ = ApplyFiltersAsync();

        partial void OnCurrentProjectChanged(ProjectScopeModel? value)
        {
            _restoreMonitor.StopMonitoring();
            if (value != null)
            {
                _restoreMonitor.StartMonitoring(value);
            }
            OnPropertyChanged(nameof(HasProject));
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

            var (cleanQuery, sourceFilter) = ExtractSourceFilter(query);

            if (!string.IsNullOrWhiteSpace(cleanQuery))
                AddToSearchHistory(cleanQuery);

            try
            {
                if (!FilterInstalled && !FilterUpdates)
                {
                    // Browse / remote search mode
                    IsRemoteLoading = true;
                    var results = await _feedService.SearchAsync(cleanQuery, FilterPrerelease, 0, 50, ct, sourceFilter);
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

        // Parses `source:"<name>"` and `source:<name>" tokens that the IVsSearch
        // filter dropdown injects when the user picks one or more package sources.
        // Returns the query with the tokens stripped, and the list of source names.
        public static (string cleanQuery, IReadOnlyCollection<string>? sourceFilter) ExtractSourceFilter(string query)
        {
            if (string.IsNullOrEmpty(query))
                return (query ?? string.Empty, null);

            var sources = new List<string>();
            var pattern = new System.Text.RegularExpressions.Regex(
                "source:(?:\"(?<v>[^\"]*)\"|(?<v>\\S+))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var stripped = pattern.Replace(query, m =>
            {
                var value = m.Groups["v"].Value;
                if (!string.IsNullOrWhiteSpace(value))
                    sources.Add(value);
                return string.Empty;
            });

            stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "\\s+", " ").Trim();
            return (stripped, sources.Count == 0 ? null : sources);
        }

        private async Task ApplyFiltersAsync(CancellationToken cancellationToken = default)
        {
            if (CurrentProject == null) return;

            IsLoading = true;
            try
            {
                var installed = await _projectService.GetInstalledPackagesAsync(CurrentProject, cancellationToken);
                var rows = installed.Select(p => new PackageRowViewModel(p)).ToList();

                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    var (cleanQuery, _) = ExtractSourceFilter(SearchText);
                    if (!string.IsNullOrWhiteSpace(cleanQuery))
                        rows = rows.Where(r => r.PackageId.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (FilterUpdates)
                    rows = rows.Where(r => r.HasUpdate).ToList();

                Packages.Clear();
                foreach (var row in rows.OrderBy(r => r.IsTransitive).ThenBy(r => r.PackageId))
                    Packages.Add(row);

                EnrichInstalledMetadataInBackground(Packages.ToList());
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
                UpdateEmptyState();
            }
        }

        private CancellationTokenSource? _enrichCts;

        private void EnrichInstalledMetadataInBackground(IReadOnlyList<PackageRowViewModel> rows)
        {
            _enrichCts?.Cancel();
            _enrichCts = new CancellationTokenSource();
            var ct = _enrichCts.Token;
            var ctx = _uiContext;

            _ = Task.Run(async () =>
            {
                using var throttle = new SemaphoreSlim(4);
                var tasks = rows.Select(async row =>
                {
                    if (ct.IsCancellationRequested) return;
                    await throttle.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var meta = await _feedService.GetPackageMetadataAsync(row.PackageId, ct).ConfigureAwait(false);
                        if (meta == null || ct.IsCancellationRequested) return;
                        if (ctx != null)
                        {
#pragma warning disable VSTHRD001
                            ctx.Post(_ => row.ApplyMetadata(meta), null);
#pragma warning restore VSTHRD001
                        }
                        else
                        {
                            row.ApplyMetadata(meta);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { await ex.LogAsync(); }
                    finally { throttle.Release(); }
                });
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }, ct);
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
            }, async () => await ReloadPackagesAsync());

            try
            {
                if (CurrentProject == null) return;
                await detail.LoadAsync(row, CurrentProject, FilterPrerelease, _operationCts.Token);
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
            OperationLog.Clear();
            await ReloadPackagesAsync();
        }

        public async Task ReloadPackagesAsync()
        {
            // Re-load packages using the current scope, view-mode and search.
            if (FilterInstalled || FilterUpdates)
                await ApplyFiltersAsync();
            else
                await SearchRemoteAsync();
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
                if (CurrentProject?.ProjectFullPath != null && row.LatestStableVersion != null)
                    await _projectService.UpdatePackageAsync(CurrentProject.ProjectFullPath, row.PackageId, row.LatestStableVersion, CancellationToken.None);
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

        [RelayCommand]
        private async Task QuickInstallAsync(PackageRowViewModel? row)
        {
            if (row == null) return;
            var version = row.LatestStableVersion ?? row.LatestPrereleaseVersion;
            if (version == null) return;

            row.IsOperationInProgress = true;
            try
            {
                StatusMessage = $"Installing {row.PackageId}…";
                if (CurrentProject?.ProjectFullPath != null)
                {
                    await _projectService.InstallPackageAsync(CurrentProject.ProjectFullPath, row.PackageId, version, CancellationToken.None);
                }
                StatusMessage = $"✓ Installed {row.PackageId} {version}";
                OperationLog.Add($"[{DateTime.Now:HH:mm:ss}] ✓ Installed {row.PackageId} {version}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Failed to install {row.PackageId}: {ex.Message}";
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
            if (CurrentProject == null)
            {
                EmptyStateMessage = "Open a project context menu and pick 'Manage NuGet Packages' to manage packages for that project.";
                return;
            }
            if (!FilterInstalled && !FilterUpdates && string.IsNullOrWhiteSpace(SearchText))
            {
                EmptyStateMessage = "Search for a package to get started, or toggle Installed to see what's in your project.";
                return;
            }
            if (FilterUpdates && Packages.Count == 0)
            {
                EmptyStateMessage = $"All packages in {CurrentProject.DisplayName} are up to date. \u2713";
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
            _enrichCts?.Cancel();
            _enrichCts?.Dispose();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _restoreMonitor.RestoreStatusChanged -= OnRestoreStatusChanged;
            _restoreMonitor.StopMonitoring();
        }
    }
}
