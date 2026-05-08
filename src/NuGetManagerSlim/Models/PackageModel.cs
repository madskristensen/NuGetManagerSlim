using System;
using System.Collections.Generic;
using NuGet.Versioning;

namespace NuGetManagerSlim.Models
{
    public class PackageModel
    {
        public string PackageId { get; init; } = string.Empty;
        public NuGetVersion? InstalledVersion { get; init; }
        public NuGetVersion? LatestStableVersion { get; init; }
        public NuGetVersion? LatestPrereleaseVersion { get; init; }
        public string? Description { get; init; }
        public string? Authors { get; init; }
        public string? LicenseExpression { get; init; }
        public string? LicenseUrl { get; init; }
        public long DownloadCount { get; init; }
        public string? SourceName { get; init; }
        public bool IsTransitive { get; init; }
        public string? RequiredByPackageId { get; init; }
        public string? ReadmeUrl { get; init; }
        public string? ProjectUrl { get; init; }
        public string? IconUrl { get; init; }
        public DateTimeOffset? Published { get; init; }
        public IReadOnlyList<FrameworkVersionInfo> PerFrameworkVersions { get; init; } = [];
        public IReadOnlyList<PackageDependencyInfo> Dependencies { get; init; } = [];
    }

    public class PackageDependencyInfo
    {
        public string PackageId { get; init; } = string.Empty;
        public string VersionRange { get; init; } = string.Empty;
        public string TargetFramework { get; init; } = string.Empty;

        public string DisplayText => string.IsNullOrEmpty(TargetFramework)
            ? $"{PackageId} {VersionRange}"
            : $"{PackageId} {VersionRange} [{TargetFramework}]";

        // Used by the dependency tree's per-TFM groups, where the TFM is shown
        // in the group header and would be redundant on every leaf row.
        public string NameAndVersion => $"{PackageId} {VersionRange}";
    }
}
