using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NuGet.Versioning;
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
        private readonly IMruPackageService? _mruService;

        // Shared across all enrichment fan-outs to avoid the previous pattern of
        // allocating a fresh SemaphoreSlim per call (which let rapid filter
        // toggles spawn overlapping fan-outs that fought for sockets).
        private static readonly SemaphoreSlim s_enrichmentThrottle = new(4, 4);

        private const int MaxSearchHistory = 20;
        private const int MaxOperationLog = 500;

        private static readonly System.Text.RegularExpressions.Regex SourceFilterRegex = new(
            "source:(?:\"(?<v>[^\"]*)\"|(?<v>\\S+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex WhitespaceRegex = new(
            "\\s+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private CancellationTokenSource? _searchCts;
        private CancellationTokenSource? _operationCts;
        private System.Timers.Timer? _debounceTimer;
        private readonly List<string> _searchHistory = [];
        private int _searchHistoryIndex = -1;

        // Marshals collection mutations and PropertyChanged callbacks from
        // background threads onto the WPF UI thread. Prefer a Dispatcher
        // explicitly attached by the host UserControl (guaranteed to be the
        // right one); fall back to Application.Current.Dispatcher when hosted
        // in a WPF app, and finally to the SynchronizationContext captured at
        // construction time so unit tests still observe synchronous-ish
        // behavior. SynchronizationContext.Current alone is unreliable here:
        // the VM may be constructed off the UI thread by BaseToolWindow's
        // async pane factory, in which case it would be null.
        private System.Windows.Threading.Dispatcher? _dispatcher
            = System.Windows.Application.Current?.Dispatcher;
        private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private ProjectScopeModel? _currentProject;
        [ObservableProperty] private bool _filterInstalled;
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

        public ObservableCollection<PackageRowViewModel> Packages => _packages;
        private readonly BulkObservableCollection<PackageRowViewModel> _packages = [];
        public ObservableCollection<PackageSourceModel> PackageSources { get; } = [];
        public ObservableCollection<string> OperationLog { get; } = [];

        public bool HasPackages => Packages.Count > 0;
        public bool IsEmptyState => !IsLoading && !ShowSkeleton && Packages.Count == 0;
        public bool ShowSkeleton => IsRemoteLoading && Packages.Count == 0;
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
            : this(projectService, feedService, restoreMonitor, mruService: null)
        {
        }

        public MainViewModel(
            IProjectService projectService,
            INuGetFeedService feedService,
            IRestoreMonitorService restoreMonitor,
            IMruPackageService? mruService)
        {
            _projectService = projectService;
            _feedService = feedService;
            _restoreMonitor = restoreMonitor;
            _mruService = mruService;

            _debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
            _debounceTimer.Elapsed += OnDebounceElapsed;

            _restoreMonitor.RestoreStatusChanged += OnRestoreStatusChanged;
        }

        // Called by the hosting UserControl on the WPF UI thread to give the
        // view model a deterministic dispatcher for cross-thread marshaling.
        public void AttachDispatcher(System.Windows.Threading.Dispatcher dispatcher)
        {
            if (dispatcher == null) return;
            _dispatcher = dispatcher;
        }

        // Cancels and disposes the existing CTS, then assigns a fresh one.
        // Cancel-and-replace without dispose was leaking a small native handle
        // every keystroke / selection change.
        private static CancellationToken ReplaceCts(ref CancellationTokenSource? cts)
        {
            var old = cts;
            var fresh = new CancellationTokenSource();
            cts = fresh;
            if (old != null)
            {
                try { old.Cancel(); } catch (ObjectDisposedException) { }
                old.Dispose();
            }
            return fresh.Token;
        }

        // Marshals an action to the UI thread using the most reliable channel
        // available. Used by the debounce timer (System.Timers.Timer fires on
        // a threadpool thread) and the metadata-enrichment fan-out.
        private void RunOnUI(Action action)
        {
            if (action == null) return;

            var dispatcher = _dispatcher ?? System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    // Dispatcher.BeginInvoke is the right primitive here: we
                    // intentionally fire-and-forget onto the UI thread and the
                    // returned DispatcherOperation has nothing useful for us.
                    // VSTHRD001 prefers JTF, but this VM is unit-tested without
                    // the VS shell, so we keep the WPF primitive.
#pragma warning disable VSTHRD001, VSTHRD110
                    _ = dispatcher.BeginInvoke(action);
#pragma warning restore VSTHRD001, VSTHRD110
                }
                return;
            }

            if (_uiContext != null)
            {
#pragma warning disable VSTHRD001
                _uiContext.Post(_ => action(), null);
#pragma warning restore VSTHRD001
                return;
            }

            action();
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
            _packages.ReplaceAll(System.Linq.Enumerable.Empty<PackageRowViewModel>());
            Detail = null;
            SelectedPackage = null;
            UpdateEmptyState();
            OnPropertyChanged(nameof(HasPackages));
            OnPropertyChanged(nameof(IsEmptyState));
        }

        partial void OnSearchTextChanged(string value)
        {
            ApplyLocalFilter(value);
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        // Synchronous client-side filter that hides rows that don't match the
        // current query. Runs on every keystroke so the visible list reacts
        // immediately while the debounced remote search is still in flight.
        private void ApplyLocalFilter(string? text)
        {
            var query = text?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                foreach (var row in _packages)
                {
                    if (!row.IsLocallyVisible) row.IsLocallyVisible = true;
                }
                return;
            }

            foreach (var row in _packages)
            {
                var match = row.PackageId != null
                    && row.PackageId.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (row.IsLocallyVisible != match) row.IsLocallyVisible = match;
            }
        }

        partial void OnFilterInstalledChanged(bool value)
        {
            OnPropertyChanged(nameof(ViewMode));
            _ = ReloadPackagesAsync();
        }
        partial void OnFilterUpdatesChanged(bool value)
        {
            OnPropertyChanged(nameof(ViewMode));
            _ = ReloadPackagesAsync();
        }
        partial void OnFilterPrereleaseChanged(bool value) => _ = ReloadPackagesAsync();

        partial void OnIsRemoteLoadingChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowSkeleton));
            OnPropertyChanged(nameof(IsEmptyState));
        }

        partial void OnIsLoadingChanged(bool value)
        {
            OnPropertyChanged(nameof(IsEmptyState));
        }

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
            // System.Timers.Timer fires on a threadpool thread; marshal to the
            // WPF UI thread so collection updates inside SearchRemoteAsync run
            // where the bound ObservableCollection lives.
            RunOnUI(() => _ = SearchRemoteAsync());
        }

        private async Task SearchRemoteAsync()
        {
            var query = SearchText;

            var ct = ReplaceCts(ref _searchCts);

            var (cleanQuery, sourceFilter) = ExtractSourceFilter(query);

            if (!string.IsNullOrWhiteSpace(cleanQuery))
                AddToSearchHistory(cleanQuery);

            try
            {
                if (!FilterInstalled && !FilterUpdates)
                {
                    IsRemoteLoading = true;
                    var searchTask = _feedService.SearchAsync(cleanQuery ?? string.Empty, FilterPrerelease, 0, 50, ct, sourceFilter);
                    var installedTask = CurrentProject != null
                        ? _projectService.GetInstalledPackagesAsync(CurrentProject, ct)
                        : Task.FromResult<IReadOnlyList<PackageModel>>(Array.Empty<PackageModel>());
                    var mruTask = _mruService != null
                        ? _mruService.GetRecentAsync(ct)
                        : Task.FromResult<IReadOnlyList<PackageModel>>(Array.Empty<PackageModel>());
                    await Task.WhenAll(searchTask, installedTask, mruTask);
                    ct.ThrowIfCancellationRequested();
                    var results = await searchTask;
                    var installed = await installedTask;
                    var mru = await mruTask;
                    ApplyRemoteResults(RankByMru(results, mru), BuildInstalledMap(installed));
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
                SetStatus($"✗ Search failed: {ex.Message}");
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

            // Fast-path: ~99% of queries don't contain a `source:` token, so skip
            // both regex matches when there's nothing to do. This runs on every
            // keystroke after debounce.
            if (query.IndexOf("source:", StringComparison.OrdinalIgnoreCase) < 0)
                return (query, null);

            var sources = new List<string>();
            var stripped = SourceFilterRegex.Replace(query, m =>
            {
                var value = m.Groups["v"].Value;
                if (!string.IsNullOrWhiteSpace(value))
                    sources.Add(value);
                return string.Empty;
            });

            stripped = WhitespaceRegex.Replace(stripped, " ").Trim();
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

                _packages.ReplaceAll(rows.OrderBy(r => r.IsTransitive).ThenBy(r => r.PackageId));

                EnrichInstalledMetadataInBackground(Packages.ToList());
                SeedMruFromInstalled(installed);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetStatus($"✗ Failed to load packages: {ex.Message}");
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
            var ct = ReplaceCts(ref _enrichCts);

            _ = Task.Run(async () =>
            {
                var tasks = rows.Select(async row =>
                {
                    if (ct.IsCancellationRequested) return;
                    await s_enrichmentThrottle.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var meta = await _feedService.GetPackageMetadataAsync(row.PackageId, ct).ConfigureAwait(false);
                        if (meta == null || ct.IsCancellationRequested) return;
                        RunOnUI(() => row.ApplyMetadata(meta));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { await ex.LogAsync(); }
                    finally { s_enrichmentThrottle.Release(); }
                });
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }, ct);
        }

        private void SeedMruFromInstalled(IReadOnlyList<PackageModel> installed)
        {
            if (_mruService == null || installed == null || installed.Count == 0) return;
            _ = Task.Run(async () =>
            {
                foreach (var pkg in installed)
                {
                    if (pkg.IsTransitive) continue;
                    if (string.IsNullOrEmpty(pkg.PackageId)) continue;
                    try { await _mruService.RecordAsync(pkg, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { await ex.LogAsync(); }
                }
            });
        }

        private void RecordMru(PackageRowViewModel row, NuGetVersion? installedVersion)
        {
            if (_mruService == null || row == null) return;
            var snapshot = row.Model;
            var pkg = new PackageModel
            {
                PackageId = snapshot.PackageId,
                InstalledVersion = installedVersion ?? snapshot.InstalledVersion,
                LatestStableVersion = snapshot.LatestStableVersion,
                LatestPrereleaseVersion = snapshot.LatestPrereleaseVersion,
                Authors = snapshot.Authors,
                Description = snapshot.Description,
                IconUrl = snapshot.IconUrl,
                SourceName = snapshot.SourceName,
            };
            _ = Task.Run(async () =>
            {
                try { await _mruService.RecordAsync(pkg, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { await ex.LogAsync(); }
            });
        }

        // Stable re-ranking of search results: any package the user has previously
        // installed or updated (per the MRU) bubbles to the top in MRU order, while
        // everything else keeps the feed's original ordering.
        public static IReadOnlyList<PackageModel> RankByMru(IReadOnlyList<PackageModel> results, IReadOnlyList<PackageModel> mru)
        {
            if (results == null || results.Count == 0)
                return results ?? (IReadOnlyList<PackageModel>)Array.Empty<PackageModel>();
            if (mru == null || mru.Count == 0) return results;

            var rankById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < mru.Count; i++)
            {
                var id = mru[i].PackageId;
                if (string.IsNullOrEmpty(id)) continue;
                if (!rankById.ContainsKey(id))
                    rankById[id] = i;
            }

            var promoted = new List<PackageModel>(results.Count);
            var rest = new List<PackageModel>(results.Count);
            foreach (var r in results)
            {
                if (rankById.ContainsKey(r.PackageId))
                    promoted.Add(r);
                else
                    rest.Add(r);
            }

            promoted.Sort((a, b) => rankById[a.PackageId].CompareTo(rankById[b.PackageId]));

            var ranked = new List<PackageModel>(results.Count);
            ranked.AddRange(promoted);
            ranked.AddRange(rest);
            return ranked;
        }

        private static Dictionary<string, PackageModel> BuildInstalledMap(IReadOnlyList<PackageModel> installed)
        {
            var map = new Dictionary<string, PackageModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in installed)
            {
                if (p.IsTransitive) continue;
                if (p.InstalledVersion == null) continue;
                map[p.PackageId] = p;
            }
            return map;
        }

        private void ApplyRemoteResults(IReadOnlyList<PackageModel> results, Dictionary<string, PackageModel>? installedById = null)
        {
            var newRows = new List<PackageRowViewModel>(results.Count);
            foreach (var m in results)
            {
                var model = m;
                // Annotate remote search hits that the user already has installed
                // so the row shows the green check + the uninstall button without
                // forcing the user to switch to the Installed view.
                if (installedById != null && installedById.TryGetValue(m.PackageId, out var inst))
                {
                    model = new PackageModel
                    {
                        PackageId = m.PackageId,
                        InstalledVersion = inst.InstalledVersion,
                        LatestStableVersion = m.LatestStableVersion,
                        LatestPrereleaseVersion = m.LatestPrereleaseVersion,
                        Description = m.Description,
                        Authors = m.Authors,
                        LicenseExpression = m.LicenseExpression,
                        LicenseUrl = m.LicenseUrl,
                        DownloadCount = m.DownloadCount,
                        SourceName = m.SourceName,
                        IsTransitive = false,
                        RequiredByPackageId = m.RequiredByPackageId,
                        ReadmeUrl = m.ReadmeUrl,
                        ProjectUrl = m.ProjectUrl,
                        IconUrl = m.IconUrl,
                        PerFrameworkVersions = m.PerFrameworkVersions,
                        Dependencies = m.Dependencies,
                    };
                }
                newRows.Add(new PackageRowViewModel(model));
            }

            // Single Reset instead of Clear + N Add: collapses 51 CollectionChanged
            // events (and 51 layout passes) into one when 50 results land.
            _packages.ReplaceAll(newRows);

            OnPropertyChanged(nameof(HasPackages));
            OnPropertyChanged(nameof(IsEmptyState));
            OnPropertyChanged(nameof(ShowSkeleton));
            UpdateEmptyState();
        }

        private async Task LoadDetailAsync(PackageRowViewModel row)
        {
            // Capture the token from this load attempt so that if the user
            // selects another row mid-flight (which calls ReplaceCts on
            // _operationCts), we don't overwrite Detail with the loser's
            // result. ct.IsCancellationRequested becomes true the moment a
            // newer LoadDetailAsync replaces the CTS.
            var ct = ReplaceCts(ref _operationCts);

            var detail = new PackageDetailViewModel(_feedService, _projectService, msg =>
            {
                SetStatus(msg);
                AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] {msg}");
            }, async () => await ReloadPackagesAsync());

            try
            {
                if (CurrentProject == null) return;
                await detail.LoadAsync(row, CurrentProject, FilterPrerelease, ct);
                if (ct.IsCancellationRequested) return;
                Detail = detail;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetStatus($"✗ Failed to load package details: {ex.Message}");
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
            // User-initiated Refresh: drop cached search results so the next
            // query goes back to the feed, then reload.
            _feedService.InvalidateCache();
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
                SetStatus($"Updating {row.PackageId}…");
                if (CurrentProject?.ProjectFullPath != null && row.LatestStableVersion != null)
                    await _projectService.UpdatePackageAsync(CurrentProject.ProjectFullPath, row.PackageId, row.LatestStableVersion, CancellationToken.None);
                RecordMru(row, row.LatestStableVersion);
                var done = $"✓ Updated {row.PackageId} → {row.LatestStableVersion}";
                SetStatus(done);
                AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] {done}");
                await ReloadPackagesAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"✗ Failed to update {row.PackageId}: {ex.Message}");
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
                SetStatus($"Installing {row.PackageId}…");
                if (CurrentProject?.ProjectFullPath != null)
                {
                    await _projectService.InstallPackageAsync(CurrentProject.ProjectFullPath, row.PackageId, version, CancellationToken.None);
                }
                RecordMru(row, version);
                var done = $"✓ Installed {row.PackageId} {version}";
                SetStatus(done);
                AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] {done}");
                await ReloadPackagesAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"✗ Failed to install {row.PackageId}: {ex.Message}");
                await ex.LogAsync();
            }
            finally
            {
                row.IsOperationInProgress = false;
            }
        }

        [RelayCommand]
        private async Task QuickUninstallAsync(PackageRowViewModel? row)
        {
            if (row == null) return;
            row.IsOperationInProgress = true;
            try
            {
                SetStatus($"Uninstalling {row.PackageId}…");
                if (CurrentProject?.ProjectFullPath != null)
                {
                    await _projectService.UninstallPackageAsync(CurrentProject.ProjectFullPath, row.PackageId, CancellationToken.None);
                }
                var done = $"✓ Uninstalled {row.PackageId}";
                SetStatus(done);
                AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] {done}");
                await ReloadPackagesAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"✗ Failed to uninstall {row.PackageId}: {ex.Message}");
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
            if (_searchHistory.Count > MaxSearchHistory)
                _searchHistory.RemoveAt(0);
            _searchHistoryIndex = _searchHistory.Count;
        }

        private void AppendOperationLog(string message)
        {
            OperationLog.Add(message);
            // Cap the log so long-running sessions don't accumulate unbounded
            // strings and slow down the bound ItemsControl.
            while (OperationLog.Count > MaxOperationLog)
                OperationLog.RemoveAt(0);
        }

        // Updates the in-window StatusMessage and mirrors the message to the
        // Visual Studio main status bar so users can see progress and errors
        // without having to keep the tool window visible.
        private void SetStatus(string message)
        {
            StatusMessage = message;
            _ = WriteVsStatusAsync(message);
        }

        private static async Task WriteVsStatusAsync(string message)
        {
            try
            {
                await VS.StatusBar.ShowMessageAsync(message);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
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
                SetStatus($"⚠ Restore incomplete for {e.ProjectName} — {e.UnresolvedCount} packages unresolved.");
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
