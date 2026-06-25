using System.Linq;
using System.Xml.Linq;

namespace NuGetManagerSlim.Services
{
    // Single source of truth for the MSBuild <PackageReference> attribute schema.
    // Both the project reader (ProjectService) and the imported-file reader
    // (MsBuildImportedPackageReader) discover the package id and declared version
    // the same way, so the attribute precedence lives here to keep the two paths
    // from drifting apart.
    internal static class PackageReferenceXml
    {
        // The id comes from Include (the common case) or Update (used to set a
        // version for a package contributed by an SDK / import).
        public static string? ReadId(XElement packageReference)
            => (string?)packageReference.Attribute("Include")
               ?? (string?)packageReference.Attribute("Update");

        // Version precedence mirrors MSBuild: the Version attribute, then a
        // VersionOverride (Central Package Management), then a nested <Version>
        // element. Returns null when none is present (e.g. a CPM reference whose
        // version lives in Directory.Packages.props).
        public static string? ReadVersionRaw(XElement packageReference)
            => (string?)packageReference.Attribute("Version")
               ?? (string?)packageReference.Attribute("VersionOverride")
               ?? packageReference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
    }
}
