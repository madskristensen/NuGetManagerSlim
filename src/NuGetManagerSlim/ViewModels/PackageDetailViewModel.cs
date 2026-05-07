using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NuGet.Versioning;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.Services;

namespace NuGetManagerSlim.ViewModels
{
    public partial class PackageDetailViewModel : ObservableObject
    {
        private readonly INuGetFeedService _feedService;
        private readonly IProjectService _projectService;
        private readonly Action<string> _reportStatus;
        private readonly Func<Task>? _onChanged;
        private string? _projectFullPath;

        [ObservableProperty] private string _packageId = string.Empty;
        [ObservableProperty] private string _description = string.Empty;
        [ObservableProperty] private string _authors = string.Empty;
        [ObservableProperty] private string _license = string.Empty;
        [ObservableProperty] private string _downloadCountDisplay = string.Empty;
        [ObservableProperty] private string _readmePreview = string.Empty;
        [ObservableProperty] private string? _licenseUrl;
        [ObservableProperty] private string? _projectUrl;
        [ObservableProperty] private NuGetVersion? _selectedVersion;
        [ObservableProperty] private bool _canInstall;
        [ObservableProperty] private bool _canUpdate;
        [ObservableProperty] private bool _canUninstall;
        [ObservableProperty] private bool _canUpdateAllProjects;

        public ObservableCollection<NuGetVersion> AvailableVersions { get; } = [];
        public ObservableCollection<ProjectMembershipViewModel> ProjectMemberships { get; } = [];
        public ObservableCollection<PackageDependencyInfo> Dependencies { get; } = [];

        private CancellationTokenSource? _versionMetadataCts;
        private bool _suppressVersionReload;

        public PackageDetailViewModel(
            INuGetFeedService feedService,
            IProjectService projectService,
            Action<string> reportStatus,
            Func<Task>? onChanged = null)
        {
            _feedService = feedService;
            _projectService = projectService;
            _reportStatus = reportStatus;
            _onChanged = onChanged;
        }

        public async Task LoadAsync(PackageRowViewModel row, ProjectScopeModel scope, bool includePrerelease, CancellationToken cancellationToken)
        {
            PackageId = row.PackageId;
            _projectFullPath = scope?.ProjectFullPath;
            AvailableVersions.Clear();
            Dependencies.Clear();
            ProjectMemberships.Clear();

            // Load versions
            var versions = await _feedService.GetVersionsAsync(row.PackageId, includePrerelease, cancellationToken);
            _suppressVersionReload = true;
            try
            {
                foreach (var v in versions)
                    AvailableVersions.Add(v);

                SelectedVersion = row.InstalledVersion ?? (AvailableVersions.Count > 0 ? AvailableVersions[0] : null);
            }
            finally
            {
                _suppressVersionReload = false;
            }

            await LoadVersionMetadataAsync(SelectedVersion, cancellationToken);

            CanInstall = !row.IsInstalled;
            CanUpdate = row.HasUpdate;
            CanUninstall = row.IsInstalled && !row.IsTransitive;
            CanUpdateAllProjects = false;
        }

        partial void OnSelectedVersionChanged(NuGetVersion? value)
        {
            if (_suppressVersionReload || value == null || string.IsNullOrEmpty(PackageId)) return;
            _versionMetadataCts?.Cancel();
            _versionMetadataCts = new CancellationTokenSource();
            var ct = _versionMetadataCts.Token;
            _ = LoadVersionMetadataAsync(value, ct);
        }

        private async Task LoadVersionMetadataAsync(NuGetVersion? version, CancellationToken cancellationToken)
        {
            if (version == null) return;
            try
            {
                var metadata = await _feedService.GetPackageMetadataAsync(PackageId, version, cancellationToken);
                if (metadata == null || cancellationToken.IsCancellationRequested) return;

                Description = metadata.Description ?? string.Empty;
                Authors = metadata.Authors ?? string.Empty;
                License = metadata.LicenseExpression ?? (metadata.LicenseUrl != null ? "View license" : "Unknown");
                LicenseUrl = metadata.LicenseUrl ?? metadata.ProjectUrl;
                ProjectUrl = metadata.ProjectUrl;
                DownloadCountDisplay = metadata.DownloadCount > 0
                    ? FormatDownloadCount(metadata.DownloadCount)
                    : "N/A";

                Dependencies.Clear();
                foreach (var dep in metadata.Dependencies)
                    Dependencies.Add(dep);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        [RelayCommand]
        private async Task InstallAsync(CancellationToken cancellationToken)
        {
            if (SelectedVersion == null || string.IsNullOrEmpty(_projectFullPath)) return;
            try
            {
                _reportStatus($"Installing {PackageId} {SelectedVersion}\u2026");
                await _projectService.InstallPackageAsync(_projectFullPath!, PackageId, SelectedVersion, cancellationToken);
                _reportStatus($"\u2713 Installed {PackageId} {SelectedVersion}");
                if (_onChanged != null) await _onChanged();
            }
            catch (Exception ex)
            {
                _reportStatus($"\u2717 Failed to install {PackageId}: {ex.Message}");
                await ex.LogAsync();
            }
        }

        [RelayCommand]
        private async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (SelectedVersion == null || string.IsNullOrEmpty(_projectFullPath)) return;
            try
            {
                _reportStatus($"Updating {PackageId} to {SelectedVersion}\u2026");
                await _projectService.UpdatePackageAsync(_projectFullPath!, PackageId, SelectedVersion, cancellationToken);
                _reportStatus($"\u2713 Updated {PackageId} \u2192 {SelectedVersion}");
                if (_onChanged != null) await _onChanged();
            }
            catch (Exception ex)
            {
                _reportStatus($"\u2717 Failed to update {PackageId}: {ex.Message}");
                await ex.LogAsync();
            }
        }

        [RelayCommand]
        private async Task UninstallAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_projectFullPath)) return;
            try
            {
                _reportStatus($"Uninstalling {PackageId}\u2026");
                await _projectService.UninstallPackageAsync(_projectFullPath!, PackageId, cancellationToken);
                _reportStatus($"\u2713 Uninstalled {PackageId}");
                if (_onChanged != null) await _onChanged();
            }
            catch (Exception ex)
            {
                _reportStatus($"\u2717 Failed to uninstall {PackageId}: {ex.Message}");
                await ex.LogAsync();
            }
        }

        [RelayCommand]
        private async Task UpdateAllProjectsAsync(CancellationToken cancellationToken)
        {
            if (SelectedVersion == null) return;
            await Task.CompletedTask;
        }

        [RelayCommand]
        private void OpenLicense()
        {
            if (!string.IsNullOrEmpty(LicenseUrl))
                System.Diagnostics.Process.Start(LicenseUrl);
        }

        [RelayCommand]
        private void OpenNuGetOrg()
        {
            var url = $"https://www.nuget.org/packages/{PackageId}";
            if (SelectedVersion != null) url += $"/{SelectedVersion}";
            System.Diagnostics.Process.Start(url);
        }

        private static string FormatDownloadCount(long count)
        {
            if (count >= 1_000_000_000) return $"{count / 1_000_000_000.0:F1}B";
            if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
            if (count >= 1_000) return $"{count / 1_000.0:F1}K";
            return count.ToString();
        }
    }

    public class ProjectMembershipViewModel : ObservableObject
    {
        private bool _isSelected;

        public string DisplayText { get; init; } = string.Empty;
        public string? ProjectFullPath { get; init; }
        public string? InstalledVersion { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
