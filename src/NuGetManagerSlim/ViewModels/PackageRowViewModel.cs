using CommunityToolkit.Mvvm.ComponentModel;
using NuGet.Versioning;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.ViewModels
{
    public partial class PackageRowViewModel : ObservableObject
    {
        private PackageModel _model;
        private string? _iconUrlOverride;

        [ObservableProperty]
        private bool _isOperationInProgress;

        public PackageRowViewModel(PackageModel model)
        {
            _model = model;
        }

        public void ApplyMetadata(PackageModel metadata)
        {
            if (metadata == null) return;
            if (!string.IsNullOrEmpty(metadata.IconUrl))
            {
                _iconUrlOverride = metadata.IconUrl;
                OnPropertyChanged(nameof(IconUrl));
                OnPropertyChanged(nameof(HasIcon));
            }
            if (string.IsNullOrEmpty(_model.Authors) && !string.IsNullOrEmpty(metadata.Authors))
            {
                _model = new PackageModel
                {
                    PackageId = _model.PackageId,
                    InstalledVersion = _model.InstalledVersion,
                    LatestStableVersion = _model.LatestStableVersion ?? metadata.LatestStableVersion,
                    LatestPrereleaseVersion = _model.LatestPrereleaseVersion ?? metadata.LatestPrereleaseVersion,
                    Description = _model.Description ?? metadata.Description,
                    Authors = metadata.Authors,
                    LicenseExpression = _model.LicenseExpression ?? metadata.LicenseExpression,
                    LicenseUrl = _model.LicenseUrl ?? metadata.LicenseUrl,
                    DownloadCount = _model.DownloadCount > 0 ? _model.DownloadCount : metadata.DownloadCount,
                    SourceName = _model.SourceName,
                    IsTransitive = _model.IsTransitive,
                    RequiredByPackageId = _model.RequiredByPackageId,
                    ReadmeUrl = _model.ReadmeUrl ?? metadata.ReadmeUrl,
                    ProjectUrl = _model.ProjectUrl ?? metadata.ProjectUrl,
                    IconUrl = _iconUrlOverride ?? metadata.IconUrl,
                    PerFrameworkVersions = _model.PerFrameworkVersions,
                    Dependencies = _model.Dependencies,
                };
                OnPropertyChanged(nameof(AuthorDisplay));
                OnPropertyChanged(nameof(HasUpdate));
                OnPropertyChanged(nameof(UpdateBadge));
            }
        }

        public string PackageId => _model.PackageId;

        public NuGetVersion? InstalledVersion => _model.InstalledVersion;

        public NuGetVersion? LatestStableVersion => _model.LatestStableVersion;

        public NuGetVersion? LatestPrereleaseVersion => _model.LatestPrereleaseVersion;

        public bool IsInstalled => _model.InstalledVersion != null;

        public bool IsTransitive => _model.IsTransitive;

        public bool IsPrerelease => _model.LatestPrereleaseVersion?.IsPrerelease == true
                                    && _model.LatestStableVersion == null;

        public bool HasUpdate => IsInstalled
            && _model.LatestStableVersion != null
            && _model.LatestStableVersion > _model.InstalledVersion;

        public string InstalledVersionDisplay => _model.InstalledVersion != null
            ? $"v{_model.InstalledVersion}"
            : string.Empty;

        public string UpdateBadge => HasUpdate ? $"→ {_model.LatestStableVersion}" : string.Empty;

        public string AuthorDisplay => string.IsNullOrEmpty(_model.Authors) ? string.Empty : $"by {_model.Authors}";

        public string SourceBadge => string.IsNullOrEmpty(_model.SourceName) ? string.Empty : $"⊕ {_model.SourceName}";

        public string RequiredByDisplay => _model.IsTransitive && !string.IsNullOrEmpty(_model.RequiredByPackageId)
            ? $"required by: {_model.RequiredByPackageId}"
            : string.Empty;

        public string UpdateButtonAccessibleName => $"Update {PackageId} to {LatestStableVersion}";

        public string GroupKey => _model.IsTransitive ? "Implicitly installed" : "Packages";

        public string? IconUrl => _iconUrlOverride ?? _model.IconUrl;

        public bool HasIcon => !string.IsNullOrEmpty(IconUrl);

        public bool CanQuickInstall => !IsInstalled
            && (_model.LatestStableVersion != null || _model.LatestPrereleaseVersion != null);

        public string InstallButtonAccessibleName => $"Install {PackageId}";

        public PackageModel Model => _model;
    }
}
