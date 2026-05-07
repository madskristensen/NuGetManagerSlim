using CommunityToolkit.Mvvm.ComponentModel;
using NuGet.Versioning;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.ViewModels
{
    public partial class PackageRowViewModel : ObservableObject
    {
        private readonly PackageModel _model;

        [ObservableProperty]
        private bool _isOperationInProgress;

        public PackageRowViewModel(PackageModel model)
        {
            _model = model;
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

        public PackageModel Model => _model;
    }
}
