using System.Collections.Generic;

namespace NuGetManagerSlim.Models
{
    /// <summary>
    /// Result of a single registration fetch that resolves everything the
    /// Installed/Browse background enrichment needs at once: the "latest"
    /// metadata (drives the update badge, deprecation and display fields) and
    /// the advisories that affect the *installed* version. Folding both into one
    /// fetch avoids issuing two separate per-package registration round trips.
    /// </summary>
    public sealed class InstalledEnrichment
    {
        /// <summary>
        /// Latest-version metadata for the package, or null when the package was
        /// not found on any enabled source.
        /// </summary>
        public PackageModel? Latest { get; init; }

        /// <summary>
        /// Advisories affecting the installed version specifically. Empty when
        /// the installed version is unknown, not present on the feed, or carries
        /// no known advisories.
        /// </summary>
        public IReadOnlyList<PackageVulnerabilityInfo> InstalledVulnerabilities { get; init; } = [];
    }
}
