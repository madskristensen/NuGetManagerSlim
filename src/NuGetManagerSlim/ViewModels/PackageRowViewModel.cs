using CommunityToolkit.Mvvm.ComponentModel;
using NuGet.Versioning;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.Services;
using System.Windows.Media;

namespace NuGetManagerSlim.ViewModels
{
    public partial class PackageRowViewModel : ObservableObject
    {
        private PackageModel _model;
        private string? _iconUrlOverride;

        [ObservableProperty]
        private bool _isOperationInProgress;

        // Driven by the local-filter pass that runs synchronously on every
        // keystroke so the visible list reacts immediately, before the debounced
        // remote search comes back.
        [ObservableProperty]
        private bool _isLocallyVisible = true;

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
                _iconRequested = false;
                _iconFailed = false;
                _icon = null;
                OnPropertyChanged(nameof(IconUrl));
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(Icon));
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
                OnPropertyChanged(nameof(DownloadCountDisplay));
            }
            else if (_model.DownloadCount <= 0 && metadata.DownloadCount > 0)
            {
                _model = new PackageModel
                {
                    PackageId = _model.PackageId,
                    InstalledVersion = _model.InstalledVersion,
                    LatestStableVersion = _model.LatestStableVersion ?? metadata.LatestStableVersion,
                    LatestPrereleaseVersion = _model.LatestPrereleaseVersion ?? metadata.LatestPrereleaseVersion,
                    Description = _model.Description ?? metadata.Description,
                    Authors = _model.Authors,
                    LicenseExpression = _model.LicenseExpression ?? metadata.LicenseExpression,
                    LicenseUrl = _model.LicenseUrl ?? metadata.LicenseUrl,
                    DownloadCount = metadata.DownloadCount,
                    SourceName = _model.SourceName,
                    IsTransitive = _model.IsTransitive,
                    RequiredByPackageId = _model.RequiredByPackageId,
                    ReadmeUrl = _model.ReadmeUrl ?? metadata.ReadmeUrl,
                    ProjectUrl = _model.ProjectUrl ?? metadata.ProjectUrl,
                    IconUrl = _iconUrlOverride ?? metadata.IconUrl,
                    PerFrameworkVersions = _model.PerFrameworkVersions,
                    Dependencies = _model.Dependencies,
                };
                OnPropertyChanged(nameof(HasUpdate));
                OnPropertyChanged(nameof(UpdateBadge));
                OnPropertyChanged(nameof(DownloadCountDisplay));
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
            && !IsTransitive
            && _model.LatestStableVersion != null
            && _model.LatestStableVersion > _model.InstalledVersion;

        public string InstalledVersionDisplay
        {
            get
            {
                if (_model.InstalledVersion != null)
                {
                    return $"v{_model.InstalledVersion}";
                }
                var latest = _model.LatestStableVersion ?? _model.LatestPrereleaseVersion;
                return latest != null ? $"v{latest}" : string.Empty;
            }
        }

        public string UpdateBadge => HasUpdate ? $"→ {_model.LatestStableVersion}" : string.Empty;

        public string AuthorDisplay => string.IsNullOrEmpty(_model.Authors) ? string.Empty : $"by {_model.Authors}";

        public string DownloadCountDisplay => _model.DownloadCount > 0
            ? $"{FormatDownloadCount(_model.DownloadCount)} downloads"
            : string.Empty;

        private static string FormatDownloadCount(long count)
        {
            if (count >= 1_000_000_000) return $"{count / 1_000_000_000.0:F1}B";
            if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
            if (count >= 1_000) return $"{count / 1_000.0:F1}K";
            return count.ToString();
        }

        public string SourceBadge => string.IsNullOrEmpty(_model.SourceName) ? string.Empty : $"⊕ {_model.SourceName}";

        public string RequiredByDisplay => _model.IsTransitive && !string.IsNullOrEmpty(_model.RequiredByPackageId)
            ? $"required by: {_model.RequiredByPackageId}"
            : string.Empty;

        public string UpdateButtonAccessibleName => $"Update {PackageId} to {LatestStableVersion}";

        public string GroupKey
        {
            get
            {
                if (_model.IsTransitive) return "Transitive packages";
                if (IsInstalled) return "Installed";
                return "Packages";
            }
        }

        public string? IconUrl => _iconUrlOverride ?? _model.IconUrl;

        // True when we have an icon URL and the load hasn't yet been proven
        // to fail. Some packages publish an IconUrl that resolves to empty
        // bytes, 404s, or non-image content (HTML error pages, embedded
        // .nupkg paths). Without this flag we'd collapse the placeholder
        // and then render an empty Image element in its place.
        public bool HasIcon => !_iconFailed && !string.IsNullOrEmpty(IconUrl);

        // Decoded-once, frozen, display-sized BitmapImage. Bound directly to the
        // Image control in the row template so WPF doesn't re-decode the source
        // bytes for every recycled container.
        private ImageSource? _icon;
        private bool _iconRequested;
        private bool _iconFailed;
        public ImageSource? Icon
        {
            get
            {
                if (!_iconRequested && HasIcon)
                {
                    _iconRequested = true;
                    _ = LoadIconAsync();
                }
                return _icon;
            }
        }

        private async System.Threading.Tasks.Task LoadIconAsync()
        {
            var url = IconUrl;
            if (string.IsNullOrEmpty(url)) return;
            var image = await IconCacheService.Instance.GetIconAsync(url).ConfigureAwait(true);
            if (image != null)
            {
                _icon = image;
                OnPropertyChanged(nameof(Icon));
            }
            else
            {
                MarkIconFailed();
            }
        }

        // Called when icon resolution fails (cache returned null or the WPF
        // Image control raised ImageFailed). Flips HasIcon to false so the
        // placeholder takes over and survives ItemsControl virtualization.
        public void MarkIconFailed()
        {
            if (_iconFailed) return;
            _iconFailed = true;
            _icon = null;
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(Icon));
        }

        public bool CanQuickInstall => !IsInstalled
            && (_model.LatestStableVersion != null || _model.LatestPrereleaseVersion != null);

        public bool CanQuickUninstall => IsInstalled && !IsTransitive;

        public string InstallButtonAccessibleName => $"Install {PackageId}";

        public string UninstallButtonAccessibleName => $"Uninstall {PackageId}";

        public PackageModel Model => _model;
    }
}
