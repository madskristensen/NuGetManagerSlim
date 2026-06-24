using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
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
        Vulnerable,
        Deprecated,
    }

    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly IProjectService _projectService;
        private readonly INuGetFeedService _feedService;
        private readonly IRestoreMonitorService _restoreMonitor;
        private readonly IMruPackageService? _mruService;
        private readonly IViewModePreferenceService? _viewModePreferences;

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
        // Cancels an in-flight ApplyFiltersAsync (project switch / view-mode
        // change) so a fast switch between projects doesn't let a stale
        // installed-list overwrite the newer one when it eventually completes.
        private CancellationTokenSource? _filterCts;
        // Set by the ViewMode setter while it adjusts the two backing filter
        // flags so we issue exactly one ReloadPackagesAsync afterwards instead
        // of one per flag change (which would cancel the first mid-flight).
        private bool _suppressFilterReload;
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
        [ObservableProperty] private bool _filterVulnerable;
        [ObservableProperty] private bool _filterDeprecated;
        [ObservableProperty] private bool _filterPrerelease;
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _isRemoteLoading;
        [ObservableProperty] private bool _isSourcePanelOpen;
        [ObservableProperty] private bool _isLogOpen;
        [ObservableProperty] private string _statusMessage = "Ready";
        [ObservableProperty] private string _emptyStateMessage = "Open a project context menu and pick 'Manage NuGet Packages' to get started.";
        [ObservableProperty] private PackageRowViewModel? _selectedPackage;
        [ObservableProperty] private PackageDetailViewModel? _detail;
        [ObservableProperty] private MultiSelectionViewModel? _multiSelection;

        public ObservableCollection<PackageRowViewModel> Packages => _packages;
        private readonly BulkObservableCollection<PackageRowViewModel> _packages = [];
        public ObservableCollection<PackageSourceModel> PackageSources { get; } = [];
        public ObservableCollection<string> OperationLog => _operationLog;
        private readonly BulkObservableCollection<string> _operationLog = [];

        public bool HasPackages => Packages.Count > 0;
        public bool IsEmptyState => !IsLoading && !ShowSkeleton && Packages.Count == 0;
        public bool ShowSkeleton => IsRemoteLoading && Packages.Count == 0;
        public bool HasSelectedPackage => SelectedPackage != null;
        public bool HasMultiSelection => MultiSelection != null;
        public bool HasDetailPane => HasSelectedPackage || HasMultiSelection;
        public bool HasProject => CurrentProject != null;

        public PackageViewMode ViewMode
        {
            get
            {
                if (FilterDeprecated) return PackageViewMode.Deprecated;
                if (FilterVulnerable) return PackageViewMode.Vulnerable;
                if (FilterUpdates) return PackageViewMode.Updates;
                if (FilterInstalled) return PackageViewMode.Installed;
                return PackageViewMode.Browse;
            }
            set
            {
                // No-op when the mode isn't actually changing. This keeps a
                // redundant set (e.g. the toolbar re-executing the command that
                // syncs the menu-controller anchor icon) from kicking off an
                // extra reload or re-raising PropertyChanged, which would
                // otherwise loop with the anchor-sync handler.
                if (value == ViewMode) return;

                SetViewModeFlags(value);
                OnPropertyChanged();

                // Persist the user's choice so the next session starts under the
                // same mode. Visual Studio independently remembers the menu
                // controller's anchor icon across restarts; restoring the mode
                // keeps the icon, the dropdown check mark, and the list in sync
                // (issue #23).
                _ = _viewModePreferences?.SaveAsync(value, CancellationToken.None);

                _ = ReloadPackagesAsync();
            }
        }

        // Sets the backing filter flags so ViewMode resolves to the requested
        // mode. _suppressFilterReload keeps the per-flag change handlers from
        // each kicking off their own reload, leaving the caller to issue a
        // single one afterwards.
        private void SetViewModeFlags(PackageViewMode value)
        {
            _suppressFilterReload = true;
            try
            {
                FilterVulnerable = value == PackageViewMode.Vulnerable;
                FilterDeprecated = value == PackageViewMode.Deprecated;
                FilterUpdates = value == PackageViewMode.Updates;
                FilterInstalled = value == PackageViewMode.Installed
                    || value == PackageViewMode.Updates
                    || value == PackageViewMode.Vulnerable
                    || value == PackageViewMode.Deprecated;
            }
            finally
            {
                _suppressFilterReload = false;
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
            IMruPackageService? mruService,
            IViewModePreferenceService? viewModePreferences = null)
        {
            _projectService = projectService;
            _feedService = feedService;
            _restoreMonitor = restoreMonitor;
            _mruService = mruService;
            _viewModePreferences = viewModePreferences;

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

        // Cancels and disposes the existing CTS without allocating a replacement.
        // Used when we want to abort an in-flight operation but aren't starting
        // a sibling operation on the same CTS slot.
        private static void CancelCts(ref CancellationTokenSource? cts)
        {
            var old = cts;
            cts = null;
            if (old == null) return;
            try { old.Cancel(); } catch (ObjectDisposedException) { }
            old.Dispose();
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
                // Restore the last selected view mode before any packages load so
                // the toolbar icon (which Visual Studio persists separately), the
                // dropdown check mark, and the package list all agree on startup
                // (issue #23). Done silently here because the project scope isn't
                // set yet - the first ReloadPackagesAsync runs once a scope is
                // selected and honors the restored mode.
                if (_viewModePreferences != null)
                {
                    var saved = await _viewModePreferences.GetAsync(cancellationToken);
                    if (saved.HasValue && saved.Value != ViewMode)
                    {
                        SetViewModeFlags(saved.Value);
                        OnPropertyChanged(nameof(ViewMode));
                    }
                }

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

        // Prefetches project-independent data so the very first user
        // interaction is served from cache. Intended to be awaited from the
        // tool window's CreateAsync alongside InitializeAsync; the tool
        // window pane construction happens before the window is shown,
        // which gives us a free window to land warm caches.
        public async Task PrewarmAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Prime the Browse "empty query" search for the prerelease
                // mode the user actually has on (settings have already been
                // loaded by this point). Most users land on Browse with an
                // empty search, so this turns their first view into a cache
                // hit (no skeleton).
                var emptySearch = _feedService.SearchAsync(string.Empty, FilterPrerelease, 0, 50, cancellationToken);

                // MRU package list (recently installed in this VS session) - needed
                // for ranking remote results on the very first Browse render.
                var mru = _mruService != null
                    ? _mruService.GetRecentAsync(cancellationToken)
                    : Task.FromResult<IReadOnlyList<PackageModel>>(Array.Empty<PackageModel>());

                // Resolve and cache per-source NuGet protocol resources so the
                // first user search isn't paying for service-index download,
                // resource lookup, TLS handshake, and credential prompts on
                // the critical path. Runs in parallel with the empty search
                // and MRU prime so total wall time stays the same.
                var sourceWarm = _feedService.PrewarmSourcesAsync(cancellationToken);

                await Task.WhenAll(emptySearch, mru, sourceWarm).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await ex.LogAsync(); }
        }

        public async Task SetCurrentProjectAsync(string projectFullPath, string projectDisplayName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(projectFullPath)) return;
            CurrentProject = new ProjectScopeModel
            {
                DisplayName = projectDisplayName,
                ProjectFullPath = projectFullPath,
                ProjectFullPaths = new[] { projectFullPath },
                ScopeKind = ProjectScopeKind.Project,
            };
            await ReloadPackagesAsync();
        }

        // Switches the tool window to solution scope: every loaded managed
        // project in the open solution is queried in aggregate. Install /
        // update / uninstall actions in this mode fan out via a per-project
        // picker dialog instead of targeting a single project.
        public async Task SetSolutionScopeAsync(CancellationToken cancellationToken = default)
        {
            var (displayName, projectPaths) = await EnumerateSolutionProjectsAsync();
            if (projectPaths.Count == 0)
            {
                // No solution / no managed projects loaded - drop back to the
                // unset state so the empty-state copy explains the situation.
                ClearCurrentProject();
                return;
            }

            CurrentProject = new ProjectScopeModel
            {
                DisplayName = displayName,
                ProjectFullPath = string.Empty,
                ProjectFullPaths = projectPaths,
                ScopeKind = ProjectScopeKind.Solution,
            };
            await ReloadPackagesAsync();
        }

        // Returns ("Solution: MySln", [csproj1, csproj2, ...]) for the open
        // solution. Empty list when no solution is loaded.
        private static async Task<(string DisplayName, IReadOnlyList<string> ProjectPaths)> EnumerateSolutionProjectsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var solution = await VS.Solutions.GetCurrentSolutionAsync();
            if (solution == null) return (string.Empty, Array.Empty<string>());

            var projects = await VS.Solutions.GetAllProjectsAsync();
            var paths = new List<string>();
            foreach (var p in projects)
            {
                var path = p?.FullPath;
                if (string.IsNullOrEmpty(path)) continue;
                var ext = System.IO.Path.GetExtension(path);
                if (string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".vbproj", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(path!);
                }
            }

            var slnName = !string.IsNullOrEmpty(solution.Name)
                ? System.IO.Path.GetFileNameWithoutExtension(solution.Name)
                : "Solution";
            return ($"Solution: {slnName}", paths);
        }

        public void ClearCurrentProject()
        {
            CurrentProject = null;
            _packages.ReplaceAll(System.Linq.Enumerable.Empty<PackageRowViewModel>());
            Detail = null;
            MultiSelection = null;
            SelectedPackage = null;
            // Deliberately keep the current ViewMode. The view-mode menu
            // controller anchors its icon to the command the user last clicked,
            // and that anchor can't be moved programmatically. Resetting the
            // mode here (e.g. to Browse on solution switch) left the dropdown
            // showing the new mode while the toolbar icon stayed on the old one.
            // Preserving the selection keeps the icon, the dropdown check mark,
            // and the package list in agreement, and the next solution simply
            // loads under the same filter the user already had selected.
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
            if (_suppressFilterReload) return;
            _ = ReloadPackagesAsync();
        }
        partial void OnFilterUpdatesChanged(bool value)
        {
            OnPropertyChanged(nameof(ViewMode));
            if (_suppressFilterReload) return;
            _ = ReloadPackagesAsync();
        }
        partial void OnFilterVulnerableChanged(bool value)
        {
            OnPropertyChanged(nameof(ViewMode));
            if (_suppressFilterReload) return;
            _ = ReloadPackagesAsync();
        }
        partial void OnFilterDeprecatedChanged(bool value)
        {
            OnPropertyChanged(nameof(ViewMode));
            if (_suppressFilterReload) return;
            _ = ReloadPackagesAsync();
        }
        partial void OnFilterPrereleaseChanged(bool value) => _ = ReloadPackagesAsync();

        // Builds a row pre-configured with the current prerelease preference so
        // update detection, the update badge, and the update target track
        // prereleases whenever the user has opted into them. Rows are rebuilt on
        // every reload (including when the prerelease toggle changes), so setting
        // the flag at construction is sufficient.
        private PackageRowViewModel CreateRow(PackageModel model) =>
            new(model)
            {
                IncludePrerelease = FilterPrerelease,
                TargetFrameworkMajorCap = _targetFrameworkMajorCap,
            };

        // Cached target-framework update cap for the current scope (issue #27).
        // Resolved from the project file(s) and refreshed when the scope changes
        // or on a user-initiated Refresh, so capping reflects TFM edits without
        // re-reading the csproj on every keystroke.
        private int? _targetFrameworkMajorCap;
        private string? _capResolvedForKey;

        private async Task RefreshTargetFrameworkCapAsync(bool force, CancellationToken cancellationToken)
        {
            var key = CurrentProject == null
                ? null
                : CurrentProject.IsSolutionScope
                    ? "solution:" + string.Join(";", CurrentProject.ProjectFullPaths)
                    : "project:" + CurrentProject.ProjectFullPath;

            if (!force && key == _capResolvedForKey) return;

            int? cap = null;
            if (CurrentProject != null)
            {
                try
                {
                    cap = await _projectService
                        .ResolveTargetFrameworkMajorCapAsync(CurrentProject, cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    cap = null;
                }
            }

            _targetFrameworkMajorCap = cap;
            _capResolvedForKey = key;
        }


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
            OnPropertyChanged(nameof(HasDetailPane));
            if (value != null)
                _ = LoadDetailAsync(value);
        }

        partial void OnMultiSelectionChanged(MultiSelectionViewModel? value)
        {
            OnPropertyChanged(nameof(HasMultiSelection));
            OnPropertyChanged(nameof(HasDetailPane));
        }

        // Called by the view (ListBox SelectionChanged) whenever the set of
        // selected rows changes. Drives single- vs multi-select detail panes.
        public void SetSelectedPackages(IReadOnlyList<PackageRowViewModel> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                SelectedPackage = null;
                MultiSelection = null;
                Detail = null;
                return;
            }

            if (rows.Count == 1)
            {
                MultiSelection = null;
                SelectedPackage = rows[0];
                return;
            }

            // Multi-select: clear single-package detail and surface the
            // bulk-action view-model instead.
            Detail = null;
            SelectedPackage = null;
            MultiSelection = new MultiSelectionViewModel(
                rows,
                CurrentProject,
                _projectService,
                msg =>
                {
                    SetStatus(msg);
                    AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] {msg}");
                },
                async () => await ReloadPackagesAsync());
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
                await RefreshTargetFrameworkCapAsync(force: false, ct).ConfigureAwait(true);

                if (!FilterInstalled && !FilterUpdates)
                {
                    // Cache hit: skip the skeleton/loading flash and render the
                    // results synchronously alongside the installed list. The
                    // installed lookup is local (no network) so it's effectively
                    // free at this point.
                    if (_feedService.TryGetCachedSearch(cleanQuery ?? string.Empty, FilterPrerelease, 0, 50, sourceFilter, out var cachedResults))
                    {
                        var installedFromCache = CurrentProject != null
                            ? await _projectService.GetInstalledPackagesAsync(CurrentProject, ct).ConfigureAwait(true)
                            : Array.Empty<PackageModel>();
                        var mruFromCache = _mruService != null
                            ? await _mruService.GetRecentAsync(ct).ConfigureAwait(true)
                            : Array.Empty<PackageModel>();
                        ct.ThrowIfCancellationRequested();
                        ApplyRemoteResults(
                            MergeInstalledOnTop(installedFromCache, RankByMru(cachedResults, mruFromCache), cleanQuery),
                            BuildInstalledMap(installedFromCache));

                        var needsEnrichCache = _packages
                            .Where(r => r.IsInstalled && r.LatestStableVersion == null)
                            .ToList();
                        if (needsEnrichCache.Count > 0)
                            EnrichInstalledInBackground(needsEnrichCache, ReplaceCts(ref _enrichCts));
                        return;
                    }

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
                    ApplyRemoteResults(
                        MergeInstalledOnTop(installed, RankByMru(results, mru), cleanQuery),
                        BuildInstalledMap(installed));

                    // Pinned installed rows that the feed search didn't return
                    // (e.g. when the query doesn't surface them in the top 50)
                    // arrive without LatestStableVersion, so the update badge
                    // can't appear. Backfill metadata for those rows in the
                    // background.
                    var needsEnrich = _packages
                        .Where(r => r.IsInstalled && r.LatestStableVersion == null)
                        .ToList();
                    if (needsEnrich.Count > 0)
                        EnrichInstalledInBackground(needsEnrich, ReplaceCts(ref _enrichCts));
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

            await RefreshTargetFrameworkCapAsync(force: false, cancellationToken).ConfigureAwait(true);

            // Abort any transitive load still running from a previous filter
            // state. The Installed path below starts a fresh one; the Updates and
            // Vulnerable paths intentionally don't, so without this an in-flight
            // load started in the Installed view could append transitive rows
            // into the Updates/Vulnerable list once it completes (issue #14).
            CancelCts(ref _transitiveCts);

            IsLoading = true;
            try
            {
                var installed = await _projectService.GetInstalledPackagesAsync(CurrentProject, cancellationToken);
                var rows = installed.Select(p => CreateRow(p)).ToList();

                string? searchQuery = null;
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    var (cleanQuery, _) = ExtractSourceFilter(SearchText);
                    searchQuery = cleanQuery;
                    if (!string.IsNullOrWhiteSpace(cleanQuery))
                        rows = rows.Where(r => r.PackageId.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (FilterVulnerable)
                {
                    // Vulnerabilities frequently hide in transitive dependencies,
                    // so scan those too (mirrors the stock manager's behavior).
                    IReadOnlyList<PackageModel> transitive;
                    try
                    {
                        transitive = await _projectService.GetTransitivePackagesAsync(CurrentProject, cancellationToken).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                        transitive = [];
                    }

                    var seen = new HashSet<string>(rows.Select(r => r.PackageId), StringComparer.OrdinalIgnoreCase);
                    foreach (var pkg in transitive)
                    {
                        if (!string.IsNullOrWhiteSpace(searchQuery)
                            && pkg.PackageId.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (seen.Add(pkg.PackageId))
                            rows.Add(CreateRow(pkg));
                    }

                    await EnrichVulnerabilitiesAsync(rows, cancellationToken).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();

                    rows = rows.Where(r => r.HasVulnerabilities).ToList();

                    // Apply any cached latest-version metadata up front so icons,
                    // descriptions, and the update badge render immediately for
                    // packages already seen in another view, instead of staying
                    // blank until the background pass lands (issue #24).
                    foreach (var row in rows)
                    {
                        if (_feedService.TryGetCachedLatestMetadata(row.PackageId, out var cachedMeta) && cachedMeta != null)
                            row.ApplyMetadata(cachedMeta);
                    }

                    _packages.ReplaceAll(rows.OrderBy(r => r.IsTransitive).ThenBy(r => r.PackageId));
                    SeedMruFromInstalled(installed);

                    // The Vulnerable view previously enriched only vulnerability
                    // advisories, so rows showed no icon, description, or
                    // latest-version/update badge (issue #24). Run the same
                    // background metadata pass the Installed/Browse views use so the
                    // rows get the full set of details. Cached metadata makes this a
                    // cheap set of cache hits on repeat visits.
                    EnrichInstalledInBackground(Packages.ToList(), ReplaceCts(ref _enrichCts));
                    return;
                }

                if (FilterDeprecated)
                {
                    // Deprecations, like vulnerabilities, can surface from
                    // transitive dependencies, so scan those too (mirrors the
                    // Vulnerable view and the stock manager's behavior).
                    IReadOnlyList<PackageModel> transitive;
                    try
                    {
                        transitive = await _projectService.GetTransitivePackagesAsync(CurrentProject, cancellationToken).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                        transitive = [];
                    }

                    var seen = new HashSet<string>(rows.Select(r => r.PackageId), StringComparer.OrdinalIgnoreCase);
                    foreach (var pkg in transitive)
                    {
                        if (!string.IsNullOrWhiteSpace(searchQuery)
                            && pkg.PackageId.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (seen.Add(pkg.PackageId))
                            rows.Add(CreateRow(pkg));
                    }

                    await EnrichDeprecationAsync(rows, cancellationToken).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();

                    rows = rows.Where(r => r.IsDeprecated).ToList();

                    // Apply any cached latest-version metadata up front so icons,
                    // descriptions, and the update badge render immediately for
                    // packages already seen in another view (issue #24).
                    foreach (var row in rows)
                    {
                        if (_feedService.TryGetCachedLatestMetadata(row.PackageId, out var cachedMeta) && cachedMeta != null)
                            row.ApplyMetadata(cachedMeta);
                    }

                    _packages.ReplaceAll(rows.OrderBy(r => r.IsTransitive).ThenBy(r => r.PackageId));
                    SeedMruFromInstalled(installed);

                    // Run the same background metadata pass the Installed/Browse
                    // views use so the rows get the full set of details (icon,
                    // description, update badge). Cached metadata makes this a cheap
                    // set of cache hits on repeat visits.
                    EnrichInstalledInBackground(Packages.ToList(), ReplaceCts(ref _enrichCts));
                    return;
                }

                if (FilterUpdates)
                {
                    // HasUpdate depends on LatestStableVersion. Apply any cached
                    // "latest" metadata first (free), then resolve the remaining
                    // rows from the feed. Previously the remote fetch only ran when
                    // the user hit Refresh, so the Updates list reflected whatever
                    // background enrichment from the Browse/Installed view happened
                    // to finish - which made the list inconsistent and frequently
                    // incomplete (issue #12). Always resolving the missing rows here
                    // makes the Updates view authoritative; the feed service caches
                    // metadata so repeat visits stay fast, and Refresh still forces
                    // fresh data because it invalidates the cache before reloading.
                    foreach (var row in rows)
                    {
                        if (_feedService.TryGetCachedLatestMetadata(row.PackageId, out var cachedMeta) && cachedMeta != null)
                            row.ApplyMetadata(cachedMeta);
                    }

                    var rowsNeedingFetch = rows.Where(r => r.LatestStableVersion == null).ToList();
                    if (rowsNeedingFetch.Count > 0)
                        await EnrichRowsAsync(rowsNeedingFetch, cancellationToken).ConfigureAwait(true);

                    rows = rows.Where(r => r.HasUpdate).ToList();
                }

                _packages.ReplaceAll(rows.OrderBy(r => r.IsTransitive).ThenBy(r => r.PackageId));

                // Don't kick off background follow-on work (metadata enrich,
                // transitive parse) if the user already moved to another
                // project - those would race against the next ApplyFilters.
                cancellationToken.ThrowIfCancellationRequested();

                SeedMruFromInstalled(installed);

                // Background-enrich the displayed rows in every list view. The
                // Updates view runs this too so the vulnerability badge shows there
                // as well (issue #24); its latest-version data is already cached
                // from the synchronous pass above, so the extra fetch is just a
                // cache hit.
                {
                    // One enrichment scope covers both the direct rows enriched
                    // here and any transitive rows appended later by
                    // LoadTransitivesInBackground. Sharing a single token stops the
                    // transitive enrichment from cancelling the still-in-flight
                    // direct enrichment, which previously left rows with
                    // half-populated metadata when switching view modes (issue #13).
                    var enrichToken = ReplaceCts(ref _enrichCts);

                    // One consolidated pass resolves both the latest-version
                    // metadata and the installed version's vulnerability badges
                    // (issue #20) from a single registration fetch per package.
                    EnrichInstalledInBackground(Packages.ToList(), enrichToken);

                    // Transitive packages are read from project.assets.json which
                    // can be slow on first cold restore. Kick the load off in the
                    // background so the direct-package list lands immediately;
                    // transitives append to the list once parsed. Browse and Updates
                    // never list transitive rows, so only the plain Installed view
                    // loads them.
                    if (FilterInstalled && !FilterUpdates)
                    {
                        LoadTransitivesInBackground(CurrentProject, enrichToken);
                    }
                }
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
        private CancellationTokenSource? _transitiveCts;

        // Awaitable variant of metadata enrichment used by the Updates view,
        // which has to know each installed package's latest version before it
        // can decide whether to include the row.
        private async Task EnrichRowsAsync(IReadOnlyList<PackageRowViewModel> rows, CancellationToken ct)
        {
            if (rows == null || rows.Count == 0) return;
            await TaskScheduler.Default;
            var tasks = rows.Select(async row =>
            {
                if (ct.IsCancellationRequested) return;
                await s_enrichmentThrottle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var meta = await _feedService.GetPackageMetadataAsync(row.PackageId, ct).ConfigureAwait(false);
                    if (meta == null || ct.IsCancellationRequested) return;
                    row.ApplyMetadata(meta);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { await ex.LogAsync(); }
                finally { s_enrichmentThrottle.Release(); }
            });
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Resolves each installed package's vulnerability advisories for its
        // installed version. Used by the Vulnerable view, which must know the
        // advisory state of the *installed* version (not the latest) before it
        // can decide whether to keep the row.
        private async Task EnrichVulnerabilitiesAsync(IReadOnlyList<PackageRowViewModel> rows, CancellationToken ct)
        {
            if (rows == null || rows.Count == 0) return;
            await TaskScheduler.Default;
            var tasks = rows.Select(async row =>
            {
                if (ct.IsCancellationRequested) return;
                var version = row.InstalledVersion;
                if (version == null) return;
                await s_enrichmentThrottle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var meta = await _feedService.GetPackageMetadataAsync(row.PackageId, version, ct).ConfigureAwait(false);
                    if (meta == null || ct.IsCancellationRequested) return;
                    row.ApplyVulnerabilities(meta.Vulnerabilities);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { await ex.LogAsync(); }
                finally { s_enrichmentThrottle.Release(); }
            });
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Resolves each installed package's deprecation status for its installed
        // version. Used by the Deprecated view, which must know whether the
        // *installed* version (not just the latest) is deprecated before it can
        // decide whether to keep the row.
        private async Task EnrichDeprecationAsync(IReadOnlyList<PackageRowViewModel> rows, CancellationToken ct)
        {
            if (rows == null || rows.Count == 0) return;
            await TaskScheduler.Default;
            var tasks = rows.Select(async row =>
            {
                if (ct.IsCancellationRequested) return;
                var version = row.InstalledVersion;
                if (version == null) return;
                await s_enrichmentThrottle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var meta = await _feedService.GetPackageMetadataAsync(row.PackageId, version, ct).ConfigureAwait(false);
                    if (meta == null || ct.IsCancellationRequested) return;
                    row.ApplyMetadata(meta);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { await ex.LogAsync(); }
                finally { s_enrichmentThrottle.Release(); }
            });
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Resolves the latest-version metadata (update badge, deprecation, display
        // fields) and the *installed* version's vulnerability advisories for rows in
        // the Installed/Browse views in a single pass. Folding both concerns into
        // one fetch means each package costs one registration round trip instead of
        // two. Surfacing the installed-version advisories here keeps the badge
        // visible in every list view, not just the dedicated Vulnerable view
        // (issue #20).
        private void EnrichInstalledInBackground(IReadOnlyList<PackageRowViewModel> rows, CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                var tasks = rows.Select(async row =>
                {
                    if (ct.IsCancellationRequested) return;
                    await s_enrichmentThrottle.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Skip the download-count search fallback when the row
                        // already carries a count (e.g. from a Browse search); the
                        // ApplyMetadata merge keeps the existing count anyway, so the
                        // extra search would be pure waste.
                        var needsDownloadCount = row.Model.DownloadCount <= 0;
                        var enrichment = await _feedService.GetInstalledEnrichmentAsync(
                            row.PackageId, row.InstalledVersion, needsDownloadCount, ct).ConfigureAwait(false);
                        if (enrichment == null || ct.IsCancellationRequested) return;
                        RunOnUI(() =>
                        {
                            if (enrichment.Latest != null)
                                row.ApplyMetadata(enrichment.Latest);
                            if (enrichment.InstalledVulnerabilities.Count > 0)
                                row.ApplyVulnerabilities(enrichment.InstalledVulnerabilities);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { await ex.LogAsync(); }
                    finally { s_enrichmentThrottle.Release(); }
                });
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }, ct);
        }

        // Reads project.assets.json off the UI thread and appends transitive
        // rows to the package list. Cancelled if the user toggles scope, picks
        // a different project, or refreshes before we finish parsing. Browse-
        // mode results never include transitives, so this only runs in the
        // Installed view.
        private void LoadTransitivesInBackground(ProjectScopeModel scope, CancellationToken enrichToken)
        {
            var ct = ReplaceCts(ref _transitiveCts);

            _ = Task.Run(async () =>
            {
                IReadOnlyList<PackageModel> transitive;
                try
                {
                    transitive = await _projectService.GetTransitivePackagesAsync(scope, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { await ex.LogAsync(); return; }

                if (ct.IsCancellationRequested || transitive == null || transitive.Count == 0) return;

                var query = SearchText?.Trim();

                RunOnUI(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    // Only append transitives while the Installed view is active.
                    // Vulnerable also sets FilterInstalled = true, so a plain
                    // FilterInstalled check would let a stale load leak rows into
                    // that view (issue #14); compare the resolved view mode instead.
                    if (ViewMode != PackageViewMode.Installed) return;

                    var existing = new HashSet<string>(
                        _packages.Select(p => p.PackageId),
                        StringComparer.OrdinalIgnoreCase);

                    var newRows = new List<PackageRowViewModel>(transitive.Count);
                    foreach (var pkg in transitive)
                    {
                        if (existing.Contains(pkg.PackageId)) continue;
                        var row = CreateRow(pkg);
                        if (!string.IsNullOrEmpty(query)
                            && row.PackageId.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            row.IsLocallyVisible = false;
                        }
                        _packages.Add(row);
                        newRows.Add(row);
                    }

                    if (newRows.Count > 0)
                    {
                        OnPropertyChanged(nameof(HasPackages));
                        OnPropertyChanged(nameof(IsEmptyState));
                        EnrichInstalledInBackground(newRows, enrichToken);
                    }
                });
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

        // Pins installed packages to the top of the Browse list. When the user
        // is searching, only installed packages whose id matches the query are
        // pinned (so the list still narrows). The remote model is preferred
        // when both sides know about the package (it carries LatestStableVersion
        // for the update badge); otherwise we fall back to the installed
        // PackageModel and let metadata enrichment fill in the latest version
        // asynchronously.
        public static IReadOnlyList<PackageModel> MergeInstalledOnTop(
            IReadOnlyList<PackageModel> installed,
            IReadOnlyList<PackageModel> remote,
            string? query)
        {
            if (installed == null || installed.Count == 0)
                return remote ?? Array.Empty<PackageModel>();

            var trimmed = query?.Trim();
            var hasQuery = !string.IsNullOrEmpty(trimmed);

            var remoteById = new Dictionary<string, PackageModel>(StringComparer.OrdinalIgnoreCase);
            if (remote != null)
            {
                foreach (var r in remote)
                {
                    if (string.IsNullOrEmpty(r.PackageId)) continue;
                    if (!remoteById.ContainsKey(r.PackageId))
                        remoteById[r.PackageId] = r;
                }
            }

            var pinnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pinned = new List<PackageModel>();
            foreach (var p in installed)
            {
                if (p.IsTransitive) continue;
                if (string.IsNullOrEmpty(p.PackageId)) continue;
                if (p.InstalledVersion == null) continue;
                if (hasQuery && p.PackageId.IndexOf(trimmed!, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!pinnedIds.Add(p.PackageId)) continue;

                pinned.Add(remoteById.TryGetValue(p.PackageId, out var rich) ? rich : p);
            }

            var rest = new List<PackageModel>(remote?.Count ?? 0);
            if (remote != null)
            {
                foreach (var r in remote)
                {
                    if (!pinnedIds.Contains(r.PackageId))
                        rest.Add(r);
                }
            }

            var combined = new List<PackageModel>(pinned.Count + rest.Count);
            combined.AddRange(pinned);
            combined.AddRange(rest);
            return combined;
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
                        RequiredByPackageIds = m.RequiredByPackageIds,
                        ReadmeUrl = m.ReadmeUrl,
                        ProjectUrl = m.ProjectUrl,
                        IconUrl = m.IconUrl,
                        PerFrameworkVersions = m.PerFrameworkVersions,
                        Dependencies = m.Dependencies,
                    };
                }
                newRows.Add(CreateRow(model));
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
            },
            async () => await ReloadPackagesAsync(),
            async (action, version) =>
            {
                // Solution-scope fan-out from the detail pane. The detail VM
                // doesn't manage operation-in-progress state on a row, so we
                // pass null recordMru and let the picker dialog itself drive
                // the per-project UX.
                await FanOutSolutionActionCoreAsync(row.PackageId, action, version, null);
            });

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
            // User-initiated Refresh: drop cached search results and metadata so
            // the next query (and the Updates view's metadata fetch) goes back to
            // the live feed, then reload.
            _feedService.InvalidateCache();
            OperationLog.Clear();
            _capResolvedForKey = null;
            await ReloadPackagesAsync();
        }

        public async Task ReloadPackagesAsync()
        {
            // Re-load packages using the current scope, view-mode and search.
            try
            {
                if (FilterInstalled || FilterUpdates)
                {
                    // Cancel any in-flight remote search so a stale browse load
                    // can't land on top of the filtered list we're about to build.
                    CancelCts(ref _searchCts);
                    var ct = ReplaceCts(ref _filterCts);
                    await ApplyFiltersAsync(ct);
                }
                else
                {
                    // Cancel any in-flight installed/updates load so its
                    // ReplaceAll can't overwrite the browse results we're
                    // about to render. This matters when the user flips from
                    // Updates to All packages: the ViewMode setter clears
                    // FilterUpdates first (which kicks off ApplyFiltersAsync
                    // for the still-true FilterInstalled state) and then
                    // clears FilterInstalled (which lands us here). Without
                    // cancelling, the filter task can finish after the search
                    // and wipe out the online results.
                    CancelCts(ref _filterCts);
                    await SearchRemoteAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // A newer reload superseded this one - drop quietly.
            }
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
            var target = row.UpdateCandidateVersion;
            if (target == null) return;

            row.IsOperationInProgress = true;
            try
            {
                if (CurrentProject?.IsSolutionScope == true)
                {
                    await FanOutSolutionActionAsync(row, SolutionPackageAction.Update, target);
                }
                else if (!string.IsNullOrEmpty(CurrentProject?.ProjectFullPath))
                {
                    SetStatus($"Updating {row.PackageId}…");
                    await _projectService.UpdatePackageAsync(CurrentProject!.ProjectFullPath, row.PackageId, target, CancellationToken.None);
                    RecordMru(row, target);
                    var done = $"✓ Updated {row.PackageId} → {target}";
                    SetStatus(done);
                    AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] {done}");
                    await ReloadPackagesAsync();
                }
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
                if (CurrentProject?.IsSolutionScope == true)
                {
                    await FanOutSolutionActionAsync(row, SolutionPackageAction.Install, version);
                }
                else if (!string.IsNullOrEmpty(CurrentProject?.ProjectFullPath))
                {
                    SetStatus($"Installing {row.PackageId}…");
                    await _projectService.InstallPackageAsync(CurrentProject!.ProjectFullPath, row.PackageId, version, CancellationToken.None);
                    RecordMru(row, version);
                    var done = $"✓ Installed {row.PackageId} {version}";
                    SetStatus(done);
                    AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] {done}");
                    await ReloadPackagesAsync();
                }
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
                if (CurrentProject?.IsSolutionScope == true)
                {
                    await FanOutSolutionActionAsync(row, SolutionPackageAction.Uninstall, targetVersion: null);
                }
                else if (!string.IsNullOrEmpty(CurrentProject?.ProjectFullPath))
                {
                    SetStatus($"Uninstalling {row.PackageId}…");
                    await _projectService.UninstallPackageAsync(CurrentProject!.ProjectFullPath, row.PackageId, CancellationToken.None);
                    var done = $"✓ Uninstalled {row.PackageId}";
                    SetStatus(done);
                    AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] {done}");
                    await ReloadPackagesAsync();
                }
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

        // Solution-scope fan-out: show the per-project picker dialog,
        // then call the same install / update / uninstall service methods
        // we use in project scope for each project the user keeps checked.
        // Failures on individual projects are logged but don't abort the
        // remaining projects so partial successes are still visible.
        private async Task FanOutSolutionActionAsync(
            PackageRowViewModel row,
            SolutionPackageAction action,
            NuGetVersion? targetVersion)
        {
            await FanOutSolutionActionCoreAsync(row.PackageId, action, targetVersion, v =>
            {
                if (v != null) RecordMru(row, v);
            });
        }

        // Row-less entry point so the package detail pane (which doesn't own a
        // PackageRowViewModel for the operation-in-progress UI flag) can reuse
        // the same fan-out path.
        internal async Task FanOutSolutionActionCoreAsync(
            string packageId,
            SolutionPackageAction action,
            NuGetVersion? targetVersion,
            Action<NuGetVersion?>? recordMru)
        {
            var scope = CurrentProject;
            if (scope == null || !scope.IsSolutionScope) return;
            if (string.IsNullOrEmpty(packageId)) return;

            var perProject = await _projectService.GetInstalledVersionsPerProjectAsync(scope, packageId, CancellationToken.None);
            var rows = SolutionProjectPickerViewModel.BuildRows(action, scope.ProjectFullPaths, perProject);
            if (rows.Count == 0)
            {
                SetStatus("No projects in the current solution scope.");
                return;
            }

            var pickerVm = new SolutionProjectPickerViewModel(action, packageId, targetVersion, rows);
            var dialog = new ToolWindows.SolutionProjectPickerDialog(pickerVm)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            if (dialog.ShowDialog() != true) return;

            var selected = pickerVm.SelectedProjects;
            if (selected.Count == 0) return;

            var verb = action switch
            {
                SolutionPackageAction.Install => "Installing",
                SolutionPackageAction.Update => "Updating",
                SolutionPackageAction.Uninstall => "Uninstalling",
                _ => "Updating",
            };
            SetStatus($"{verb} {packageId} in {selected.Count} project(s)…");

            int successCount = 0;
            foreach (var sel in selected)
            {
                try
                {
                    switch (action)
                    {
                        case SolutionPackageAction.Install:
                            if (targetVersion != null)
                                await _projectService.InstallPackageAsync(sel.ProjectFullPath, packageId, targetVersion, CancellationToken.None);
                            break;
                        case SolutionPackageAction.Update:
                            if (targetVersion != null)
                                await _projectService.UpdatePackageAsync(sel.ProjectFullPath, packageId, targetVersion, CancellationToken.None);
                            break;
                        case SolutionPackageAction.Uninstall:
                            await _projectService.UninstallPackageAsync(sel.ProjectFullPath, packageId, CancellationToken.None);
                            break;
                    }
                    successCount++;
                    AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] ✓ {action} {packageId} in {sel.DisplayName}");
                }
                catch (Exception ex)
                {
                    AppendOperationLog($"[{DateTime.Now:HH:mm:ss}] ✗ {action} {packageId} in {sel.DisplayName}: {ex.Message}");
                    await ex.LogAsync();
                }
            }

            recordMru?.Invoke(targetVersion);

            var summary = action switch
            {
                SolutionPackageAction.Install => $"✓ Installed {packageId} in {successCount}/{selected.Count} project(s)",
                SolutionPackageAction.Update => $"✓ Updated {packageId} in {successCount}/{selected.Count} project(s)",
                SolutionPackageAction.Uninstall => $"✓ Uninstalled {packageId} from {successCount}/{selected.Count} project(s)",
                _ => string.Empty,
            };
            SetStatus(summary);
            await ReloadPackagesAsync();
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
            // strings and slow down the bound list. Trim in a single Reset
            // event rather than firing N CollectionChanged(Remove, 0) shifts.
            if (_operationLog.Count > MaxOperationLog)
            {
                var keep = new List<string>(MaxOperationLog);
                for (int i = _operationLog.Count - MaxOperationLog; i < _operationLog.Count; i++)
                    keep.Add(_operationLog[i]);
                _operationLog.ReplaceAll(keep);
            }
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
            if (FilterVulnerable && Packages.Count == 0)
            {
                EmptyStateMessage = $"No known vulnerabilities in {CurrentProject.DisplayName}. \u2713";
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
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _enrichCts?.Cancel();
            _enrichCts?.Dispose();
            _transitiveCts?.Cancel();
            _transitiveCts?.Dispose();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _restoreMonitor.RestoreStatusChanged -= OnRestoreStatusChanged;
            _restoreMonitor.StopMonitoring();
        }
    }
}
