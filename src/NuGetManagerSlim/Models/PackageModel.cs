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

        // Highest known stable / prerelease version per major version number,
        // derived from the full version list during enrichment. Drives the
        // target-framework update cap (issue #27): when a project targets a
        // specific .NET major, the recommended update for runtime-coupled
        // packages is the highest version whose major is within the cap, which
        // can't be answered from LatestStableVersion alone. The map is
        // framework-independent so it stays compatible with the global metadata
        // cache; the cap itself is applied per project at display time.
        public IReadOnlyDictionary<int, NuGetVersion> MaxStableByMajor { get; init; }
            = new Dictionary<int, NuGetVersion>();
        public IReadOnlyDictionary<int, NuGetVersion> MaxPrereleaseByMajor { get; init; }
            = new Dictionary<int, NuGetVersion>();
        public string? Description { get; init; }
        public string? Authors { get; init; }
        public string? LicenseExpression { get; init; }
        public string? LicenseUrl { get; init; }
        public long DownloadCount { get; init; }
        public string? SourceName { get; init; }
        public bool IsTransitive { get; init; }
        public string? RequiredByPackageId { get; init; }

        // Every direct (top-level) package that pulls in this transitive
        // dependency, mirroring the "required by" tooltip in the built-in NuGet
        // Package Manager (issue #19). RequiredByPackageId remains the first of
        // these for callers that only need a single ancestor.
        public IReadOnlyList<string> RequiredByPackageIds { get; init; } = [];
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
            MaxStableByMajor = MaxStableByMajor,
            MaxPrereleaseByMajor = MaxPrereleaseByMajor,
            Description = Description,
            Authors = Authors,
            LicenseExpression = LicenseExpression,
            LicenseUrl = LicenseUrl,
            DownloadCount = DownloadCount,
            SourceName = SourceName,
            IsTransitive = IsTransitive,
            RequiredByPackageId = RequiredByPackageId,
            RequiredByPackageIds = RequiredByPackageIds,
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

        // Returns a copy carrying the given set of direct ancestors. Used when
        // the same transitive package is aggregated across projects and the
        // union of "required by" packages must be preserved.
        internal PackageModel WithRequiredBy(IReadOnlyList<string> requiredByPackageIds) => new PackageModel
        {
            PackageId = PackageId,
            InstalledVersion = InstalledVersion,
            LatestStableVersion = LatestStableVersion,
            LatestPrereleaseVersion = LatestPrereleaseVersion,
            MaxStableByMajor = MaxStableByMajor,
            MaxPrereleaseByMajor = MaxPrereleaseByMajor,
            Description = Description,
            Authors = Authors,
            LicenseExpression = LicenseExpression,
            LicenseUrl = LicenseUrl,
            DownloadCount = DownloadCount,
            SourceName = SourceName,
            IsTransitive = IsTransitive,
            RequiredByPackageId = requiredByPackageIds.Count > 0 ? requiredByPackageIds[0] : RequiredByPackageId,
            RequiredByPackageIds = requiredByPackageIds,
            ReadmeUrl = ReadmeUrl,
            ProjectUrl = ProjectUrl,
            IconUrl = IconUrl,
            Published = Published,
            PerFrameworkVersions = PerFrameworkVersions,
            Dependencies = Dependencies,
            Vulnerabilities = Vulnerabilities,
            IsDeprecated = IsDeprecated,
            DeprecationReason = DeprecationReason,
            AllowedVersionRange = AllowedVersionRange,
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

    // A single advisory from the feed's bulk vulnerability index. Unlike
    // <see cref="PackageVulnerabilityInfo"/> (which describes an advisory already
    // matched to a specific installed version), this carries the affected
    // <see cref="VersionRange"/> so the Vulnerable view can match every installed
    // and transitive package locally against the once-downloaded index.
    public class PackageVulnerabilityAdvisory
    {
        // Severity ordinal as reported by the feed: 0 = Low, 1 = Moderate,
        // 2 = High, 3 = Critical.
        public int Severity { get; init; }
        public string? AdvisoryUrl { get; init; }
        public VersionRange? AffectedVersions { get; init; }

        // True when the supplied installed version falls in this advisory's
        // affected range. A null range is treated as "applies to all versions"
        // so a malformed feed entry never silently hides a real advisory.
        public bool Affects(NuGetVersion version)
            => AffectedVersions == null || AffectedVersions.Satisfies(version);

        public PackageVulnerabilityInfo ToInfo()
            => new() { Severity = Severity, AdvisoryUrl = AdvisoryUrl };
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
