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

        // Known security advisories that affect this package version. Populated
        // from the feed's registration metadata. Empty when the package is not
        // known to be vulnerable.
        public IReadOnlyList<PackageVulnerabilityInfo> Vulnerabilities { get; init; } = [];

        public bool HasVulnerabilities => Vulnerabilities.Count > 0;

        // Package-level deprecation status, read from the feed's registration
        // metadata for the displayed version. Mirrors what the built-in NuGet
        // Package Manager shows so deprecated packages can be flagged in every
        // list view (issue #20).
        public bool IsDeprecated { get; init; }
        public string? DeprecationReason { get; init; }

        // Version range constraint declared in the project file (e.g. allowedVersions
        // in packages.config, or a range-syntax Version in PackageReference).
        // When set, only updates that satisfy this range should be offered.
        public VersionRange? AllowedVersionRange { get; init; }

        internal PackageModel WithAllowedVersionRange(VersionRange? range) => new PackageModel
        {
            PackageId = PackageId,
            InstalledVersion = InstalledVersion,
            LatestStableVersion = LatestStableVersion,
            LatestPrereleaseVersion = LatestPrereleaseVersion,
            Description = Description,
            Authors = Authors,
            LicenseExpression = LicenseExpression,
            LicenseUrl = LicenseUrl,
            DownloadCount = DownloadCount,
            SourceName = SourceName,
            IsTransitive = IsTransitive,
            RequiredByPackageId = RequiredByPackageId,
            ReadmeUrl = ReadmeUrl,
            ProjectUrl = ProjectUrl,
            IconUrl = IconUrl,
            Published = Published,
            PerFrameworkVersions = PerFrameworkVersions,
            Dependencies = Dependencies,
            Vulnerabilities = Vulnerabilities,
            IsDeprecated = IsDeprecated,
            DeprecationReason = DeprecationReason,
            AllowedVersionRange = range,
        };
    }

    public class PackageVulnerabilityInfo
    {
        // Severity ordinal as reported by the feed: 0 = Low, 1 = Moderate,
        // 2 = High, 3 = Critical.
        public int Severity { get; init; }
        public string? AdvisoryUrl { get; init; }

        public string SeverityText => Severity switch
        {
            0 => "Low",
            1 => "Moderate",
            2 => "High",
            3 => "Critical",
            _ => "Unknown",
        };

        public string DisplayText => string.IsNullOrEmpty(AdvisoryUrl)
            ? $"{SeverityText} severity"
            : $"{SeverityText} severity - {AdvisoryUrl}";
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
