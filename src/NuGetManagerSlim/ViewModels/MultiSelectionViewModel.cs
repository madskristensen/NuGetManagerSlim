using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.Services;

namespace NuGetManagerSlim.ViewModels
{
    // Backs the detail pane when the user has selected more than one package.
    // Exposes Install / Update / Uninstall actions that act on the subset of
    // selected rows for which each operation is meaningful (install only the
    // not-installed rows, update only the rows with available updates, etc.).
    public partial class MultiSelectionViewModel : ObservableObject
    {
        private readonly IProjectService _projectService;
        private readonly Action<string> _reportStatus;
        private readonly Func<Task>? _onChanged;
        private readonly string? _projectFullPath;

        public IReadOnlyList<PackageRowViewModel> Packages { get; }

        public int Count => Packages.Count;
        public string Title => $"{Count} packages selected";

        public IReadOnlyList<string> PackageIds { get; }

        public int InstallCount { get; }
        public int UpdateCount { get; }
        public int UninstallCount { get; }

        public bool CanInstall => InstallCount > 0 && !string.IsNullOrEmpty(_projectFullPath);
        public bool CanUpdate => UpdateCount > 0 && !string.IsNullOrEmpty(_projectFullPath);
        public bool CanUninstall => UninstallCount > 0 && !string.IsNullOrEmpty(_projectFullPath);

        public string InstallButtonText => $"Install ({InstallCount})";
        public string UpdateButtonText => $"Update ({UpdateCount})";
        public string UninstallButtonText => $"Uninstall ({UninstallCount})";

        public MultiSelectionViewModel(
            IReadOnlyList<PackageRowViewModel> packages,
            ProjectScopeModel? scope,
            IProjectService projectService,
            Action<string> reportStatus,
            Func<Task>? onChanged)
        {
            Packages = packages ?? Array.Empty<PackageRowViewModel>();
            _projectService = projectService;
            _reportStatus = reportStatus;
            _onChanged = onChanged;
            _projectFullPath = scope?.ProjectFullPath;

            PackageIds = Packages.Select(p => p.PackageId).ToList();
            InstallCount = Packages.Count(p => !p.IsInstalled);
            UpdateCount = Packages.Count(p => p.HasUpdate);
            UninstallCount = Packages.Count(p => p.IsInstalled && !p.IsTransitive);
        }

        [RelayCommand]
        private async Task InstallAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_projectFullPath)) return;
            var targets = Packages.Where(p => !p.IsInstalled).ToList();
            if (targets.Count == 0) return;

            _reportStatus($"Installing {targets.Count} packages\u2026");
            int ok = 0, fail = 0;
            foreach (var row in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = row.LatestStableVersion ?? row.LatestPrereleaseVersion;
                if (version == null) { fail++; continue; }
                try
                {
                    await _projectService.InstallPackageAsync(_projectFullPath!, row.PackageId, version, cancellationToken);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    await ex.LogAsync();
                }
            }
            _reportStatus(fail == 0
                ? $"\u2713 Installed {ok} package(s)"
                : $"Installed {ok}, failed {fail}");
            if (_onChanged != null) await _onChanged();
        }

        [RelayCommand]
        private async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_projectFullPath)) return;
            var targets = Packages.Where(p => p.HasUpdate).ToList();
            if (targets.Count == 0) return;

            _reportStatus($"Updating {targets.Count} packages\u2026");
            int ok = 0, fail = 0;
            foreach (var row in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = row.LatestStableVersion ?? row.LatestPrereleaseVersion;
                if (version == null) { fail++; continue; }
                try
                {
                    await _projectService.UpdatePackageAsync(_projectFullPath!, row.PackageId, version, cancellationToken);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    await ex.LogAsync();
                }
            }
            _reportStatus(fail == 0
                ? $"\u2713 Updated {ok} package(s)"
                : $"Updated {ok}, failed {fail}");
            if (_onChanged != null) await _onChanged();
        }

        [RelayCommand]
        private async Task UninstallAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_projectFullPath)) return;
            var targets = Packages.Where(p => p.IsInstalled && !p.IsTransitive).ToList();
            if (targets.Count == 0) return;

            _reportStatus($"Uninstalling {targets.Count} packages\u2026");
            int ok = 0, fail = 0;
            foreach (var row in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _projectService.UninstallPackageAsync(_projectFullPath!, row.PackageId, cancellationToken);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    await ex.LogAsync();
                }
            }
            _reportStatus(fail == 0
                ? $"\u2713 Uninstalled {ok} package(s)"
                : $"Uninstalled {ok}, failed {fail}");
            if (_onChanged != null) await _onChanged();
        }
    }
}
