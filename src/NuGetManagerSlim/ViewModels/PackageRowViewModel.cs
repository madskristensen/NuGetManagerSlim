using System.Collections.Generic;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NuGet.Versioning;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.Services;

namespace NuGetManagerSlim.ViewModels
{
    public partial class PackageRowViewModel : ObservableObject
    {
        private PackageModel _model;
        private string? _iconUrlOverride;
        private System.Collections.Generic.IReadOnlyList<PackageVulnerabilityInfo>? _vulnerabilitiesOverride;

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
            if (metadata == null)
                return;
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
            // Always merge the fetched metadata into the model. The latest-version
            // backfill is the whole point of enrichment (it drives the update badge
            // and the Updates view), so it must not be gated on display-only fields:
            // private/Azure Artifacts/GitHub feeds routinely return no authors and a
            // zero download count, and previously that combination skipped the merge
            // entirely, leaving LatestStableVersion unset (issue #15). Existing values
            // win so user-facing data already on the row is never overwritten.
            _model = new PackageModel
            {
                PackageId = _model.PackageId,
                InstalledVersion = _model.InstalledVersion,
                LatestStableVersion = _model.LatestStableVersion ?? metadata.LatestStableVersion,
                LatestPrereleaseVersion = _model.LatestPrereleaseVersion ?? metadata.LatestPrereleaseVersion,
                Description = _model.Description ?? metadata.Description,
                Authors = string.IsNullOrEmpty(_model.Authors) ? metadata.Authors : _model.Authors,
                LicenseExpression = _model.LicenseExpression ?? metadata.LicenseExpression,
                LicenseUrl = _model.LicenseUrl ?? metadata.LicenseUrl,
                DownloadCount = _model.DownloadCount > 0 ? _model.DownloadCount : metadata.DownloadCount,
                SourceName = _model.SourceName,
                IsTransitive = _model.IsTransitive,
                RequiredByPackageId = _model.RequiredByPackageId,
                RequiredByPackageIds = _model.RequiredByPackageIds,
                ReadmeUrl = _model.ReadmeUrl ?? metadata.ReadmeUrl,
                ProjectUrl = _model.ProjectUrl ?? metadata.ProjectUrl,
                IconUrl = _iconUrlOverride ?? metadata.IconUrl,
                PerFrameworkVersions = _model.PerFrameworkVersions,
                Dependencies = _model.Dependencies,
                Vulnerabilities = _model.Vulnerabilities,
                IsDeprecated = _model.IsDeprecated || metadata.IsDeprecated,
                DeprecationReason = _model.DeprecationReason ?? metadata.DeprecationReason,
                AllowedVersionRange = _model.AllowedVersionRange,
            };
            OnPropertyChanged(nameof(AuthorDisplay));
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateCandidateVersion));
            OnPropertyChanged(nameof(VersionInformation));
            OnPropertyChanged(nameof(UpdateBadge));
            OnPropertyChanged(nameof(DownloadCountDisplay));
            OnPropertyChanged(nameof(IsDeprecated));
            OnPropertyChanged(nameof(DeprecationTooltip));
        }

        public string PackageId => _model.PackageId;

        public NuGetVersion? InstalledVersion => _model.InstalledVersion;

        public NuGetVersion? LatestStableVersion => _model.LatestStableVersion;

        public NuGetVersion? LatestPrereleaseVersion => _model.LatestPrereleaseVersion;

        public bool IsInstalled => _model.InstalledVersion != null;

        public bool IsTransitive => _model.IsTransitive;

        public bool IsPrerelease => _model.LatestPrereleaseVersion?.IsPrerelease == true
                                    && _model.LatestStableVersion == null;

        // When set, an "update" tracks prereleases too (mirrors the host's
        // "Include prerelease" toggle). Update detection, the badge, and the
        // update target all key off this so prerelease-tracking users aren't
        // silently told a package is up to date when only a newer prerelease
        // exists. Defaults to false so the common stable-only path is unchanged.
        public bool IncludePrerelease
        {
            get => _includePrerelease;
            set
            {
                if (_includePrerelease == value) return;
                _includePrerelease = value;
                OnPropertyChanged(nameof(IncludePrerelease));
                OnPropertyChanged(nameof(UpdateCandidateVersion));
                OnPropertyChanged(nameof(HasUpdate));
                OnPropertyChanged(nameof(VersionInformation));
                OnPropertyChanged(nameof(UpdateBadge));
            }
        }
        private bool _includePrerelease;

        // The version an update would move the installed package to: the latest
        // stable, or - when prereleases are included - the highest of the stable
        // and prerelease candidates. Null when no newer version is known.
        public NuGetVersion? UpdateCandidateVersion
        {
            get
            {
                var stable = _model.LatestStableVersion;
                if (!_includePrerelease) return stable;
                var pre = _model.LatestPrereleaseVersion;
                if (stable == null) return pre;
                if (pre == null) return stable;
                return pre > stable ? pre : stable;
            }
        }

        public bool HasUpdate => IsInstalled
            && !IsTransitive
            && UpdateCandidateVersion != null
            && UpdateCandidateVersion > _model.InstalledVersion;

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

        public string VersionInformation
        {
            get
            {
                if (IsInstalled)
                {
                    return HasUpdate
                        ? $"v{_model.InstalledVersion} → v{UpdateCandidateVersion}"
                        : $"v{_model.InstalledVersion}";
                }
                else
                {
                    var latest = _model.LatestStableVersion ?? _model.LatestPrereleaseVersion;
                    return latest != null ? $"v{latest}" : string.Empty;
                }
            }
        }

        public string UpdateBadge => HasUpdate ? $"→ {UpdateCandidateVersion}" : string.Empty;

        public string AuthorDisplay => string.IsNullOrEmpty(_model.Authors) ? string.Empty : $"by {_model.Authors}";

        public string DownloadCountDisplay => _model.DownloadCount > 0
            ? $"{FormatDownloadCount(_model.DownloadCount)} downloads"
            : string.Empty;

        private static string FormatDownloadCount(long count)
        {
            if (count >= 1_000_000_000)
                return $"{count / 1_000_000_000.0:F1}B";
            if (count >= 1_000_000)
                return $"{count / 1_000_000.0:F1}M";
            if (count >= 1_000)
                return $"{count / 1_000.0:F1}K";
            return count.ToString();
        }

        public string SourceBadge => string.IsNullOrEmpty(_model.SourceName) ? string.Empty : $"⊕ {_model.SourceName}";

        // Every direct package that pulls in this transitive dependency. Empty
        // for direct/non-transitive packages.
        public IReadOnlyList<string> RequiredByPackageIds => _model.RequiredByPackageIds;

        public string RequiredByDisplay => _model.IsTransitive && _model.RequiredByPackageIds.Count > 0
            ? $"required by: {string.Join(", ", _model.RequiredByPackageIds)}"
            : (_model.IsTransitive && !string.IsNullOrEmpty(_model.RequiredByPackageId)
                ? $"required by: {_model.RequiredByPackageId}"
                : string.Empty);

        // Vulnerability metadata is fetched lazily for the installed version, so
        // it may arrive after the row is created. ApplyVulnerabilities overrides
        // whatever the model carried.
        public System.Collections.Generic.IReadOnlyList<PackageVulnerabilityInfo> Vulnerabilities
            => _vulnerabilitiesOverride ?? _model.Vulnerabilities;

        public bool HasVulnerabilities => Vulnerabilities.Count > 0;

        private int MaxSeverity
        {
            get
            {
                var max = -1;
                foreach (var v in Vulnerabilities)
                {
                    if (v.Severity > max) max = v.Severity;
                }
                return max;
            }
        }

        public string VulnerabilityBadge
        {
            get
            {
                if (!HasVulnerabilities) return string.Empty;
                var severity = MaxSeverity switch
                {
                    0 => "Low",
                    1 => "Moderate",
                    2 => "High",
                    3 => "Critical",
                    _ => "Unknown",
                };
                var count = Vulnerabilities.Count;
                return count == 1
                    ? $"\u26A0 {severity} vulnerability"
                    : $"\u26A0 {severity} vulnerability ({count})";
            }
        }

        public string VulnerabilityTooltip
        {
            get
            {
                if (!HasVulnerabilities) return string.Empty;
                return string.Join("\n", System.Linq.Enumerable.Select(Vulnerabilities, v => v.DisplayText));
            }
        }

        public void ApplyVulnerabilities(System.Collections.Generic.IReadOnlyList<PackageVulnerabilityInfo> vulnerabilities)
        {
            _vulnerabilitiesOverride = vulnerabilities ?? [];
            OnPropertyChanged(nameof(Vulnerabilities));
            OnPropertyChanged(nameof(HasVulnerabilities));
            OnPropertyChanged(nameof(VulnerabilityBadge));
            OnPropertyChanged(nameof(VulnerabilityTooltip));
        }

        // The deprecated state the feed reports for the displayed version. Drives
        // a strikethrough on the package name in every list view, matching the
        // built-in NuGet Package Manager (issue #20).
        public bool IsDeprecated => _model.IsDeprecated;

        public string DeprecationTooltip => string.IsNullOrEmpty(_model.DeprecationReason)
            ? "This package is deprecated"
            : $"This package is deprecated: {_model.DeprecationReason}";

        public string UpdateButtonAccessibleName => $"Update {PackageId} to {LatestStableVersion}";

        public string GroupKey
        {
            get
            {
                if (_model.IsTransitive)
                    return "Transitive packages";
                if (IsInstalled)
                    return "Installed";
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
            if (string.IsNullOrEmpty(url))
                return;
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
            if (_iconFailed)
                return;
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
