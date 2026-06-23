using NuGet.Versioning;

namespace NuGetManagerSlim.Models
{
    // A single selectable version in the detail-pane version dropdown, carrying
    // the deprecated / vulnerable status the feed reports for that exact version
    // so the picker can flag unsuitable versions the way the built-in NuGet
    // Package Manager does (issue #21).
    public class PackageVersionInfo
    {
        public PackageVersionInfo(
            NuGetVersion version,
            bool isDeprecated = false,
            bool isVulnerable = false,
            string? deprecationReason = null)
        {
            Version = version;
            IsDeprecated = isDeprecated;
            IsVulnerable = isVulnerable;
            DeprecationReason = deprecationReason;
        }

        public NuGetVersion Version { get; }
        public bool IsDeprecated { get; }
        public bool IsVulnerable { get; }
        public string? DeprecationReason { get; }
    }
}
