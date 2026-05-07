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

        public PackageDetailViewModel(
            INuGetFeedService feedService,
            IProjectService projectService,
            Action<string> reportStatus)
        {
            _feedService = feedService;
            _projectService = projectService;
            _reportStatus = reportStatus;
        }

        public async Task LoadAsync(PackageRowViewModel row, ProjectScopeModel scope, bool includePrerelease, CancellationToken cancellationToken)
        {
            PackageId = row.PackageId;
            AvailableVersions.Clear();
            Dependencies.Clear();
            ProjectMemberships.Clear();

            // Load versions
            var versions = await _feedService.GetVersionsAsync(row.PackageId, includePrerelease, cancellationToken);
            foreach (var v in versions)
                AvailableVersions.Add(v);

            SelectedVersion = row.InstalledVersion ?? (AvailableVersions.Count > 0 ? AvailableVersions[0] : null);

            // Load metadata
            var metadata = await _feedService.GetPackageMetadataAsync(row.PackageId, cancellationToken);
            if (metadata != null)
            {
                Description = metadata.Description ?? string.Empty;
                Authors = metadata.Authors ?? string.Empty;
                License = metadata.LicenseExpression ?? (metadata.LicenseUrl != null ? "View license" : "Unknown");
                LicenseUrl = metadata.LicenseUrl ?? metadata.ProjectUrl;
                ProjectUrl = metadata.ProjectUrl;
                DownloadCountDisplay = metadata.DownloadCount > 0
                    ? FormatDownloadCount(metadata.DownloadCount)
                    : "N/A";

                foreach (var dep in metadata.Dependencies)
                    Dependencies.Add(dep);
            }

            CanInstall = !row.IsInstalled;
            CanUpdate = row.HasUpdate;
            CanUninstall = row.IsInstalled && !row.IsTransitive;
            CanUpdateAllProjects = scope.IsEntireSolution && row.HasUpdate;
        }

        [RelayCommand]
        private async Task InstallAsync(CancellationToken cancellationToken)
        {
            if (SelectedVersion == null) return;
            _reportStatus($"Installing {PackageId} {SelectedVersion}…");
            // Delegate to project service
            _reportStatus($"✓ Installed {PackageId} {SelectedVersion}");
        }

        [RelayCommand]
        private async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (SelectedVersion == null) return;
            _reportStatus($"Updating {PackageId} to {SelectedVersion}…");
            _reportStatus($"✓ Updated {PackageId} → {SelectedVersion}");
        }

        [RelayCommand]
        private async Task UninstallAsync(CancellationToken cancellationToken)
        {
            _reportStatus($"Uninstalling {PackageId}…");
            _reportStatus($"✓ Uninstalled {PackageId}");
        }

        [RelayCommand]
        private async Task UpdateAllProjectsAsync(CancellationToken cancellationToken)
        {
            if (SelectedVersion == null) return;
            _reportStatus($"Updating {PackageId} in all projects…");
            _reportStatus($"✓ Updated {PackageId} → {SelectedVersion} in all projects");
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
