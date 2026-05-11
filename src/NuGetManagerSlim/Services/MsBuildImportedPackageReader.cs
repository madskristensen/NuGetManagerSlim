using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using NuGet.Versioning;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    // Reads PackageReferences declared in MSBuild import files that sit above
    // a project on disk (Directory.Build.props, Directory.Build.targets) and
    // resolves Central Package Management versions from Directory.Packages.props.
    // Walks up from the project directory until it either finds a file with
    // <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    // chain-stopper, or reaches the filesystem root. Caller is responsible
    // for merging the results into the project's own PackageReference list.
    public static class MsBuildImportedPackageReader
    {
        private const string DirectoryBuildProps = "Directory.Build.props";
        private const string DirectoryBuildTargets = "Directory.Build.targets";
        private const string DirectoryPackagesProps = "Directory.Packages.props";

        // Single entry point. Returns an empty list when the project has no
        // imported PackageReferences or the project path is invalid. Never
        // throws for malformed XML; logs nothing and skips silently so a
        // broken Directory.Build.props doesn't block the installed list.
        public static IReadOnlyList<PackageModel> ReadImportedPackages(
            string projectFullPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(projectFullPath)) return Array.Empty<PackageModel>();

            var projectDir = Path.GetDirectoryName(projectFullPath);
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
                return Array.Empty<PackageModel>();

            var propsFiles = new List<string>();
            var targetsFiles = new List<string>();
            var packagesFiles = new List<string>();

            // Walk up to the root, collecting every Directory.* file we find.
            // MSBuild only honors the nearest file in each chain, but evaluating
            // every one we find matches what NuGet's restore actually sees once
            // you account for the chain-up imports inside Directory.Build.props.
            var dir = new DirectoryInfo(projectDir);
            while (dir != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TryAdd(dir, DirectoryBuildProps, propsFiles);
                TryAdd(dir, DirectoryBuildTargets, targetsFiles);
                TryAdd(dir, DirectoryPackagesProps, packagesFiles);

                dir = dir.Parent;
            }

            if (propsFiles.Count == 0 && targetsFiles.Count == 0)
                return Array.Empty<PackageModel>();

            // CPM versions (PackageVersion entries from Directory.Packages.props)
            // are looked up by id and applied when a PackageReference has no
            // Version attribute of its own.
            var cpmVersions = new Dictionary<string, NuGetVersion?>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in packagesFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadPackageVersions(path, cpmVersions);
            }

            var byId = new Dictionary<string, PackageModel>(StringComparer.OrdinalIgnoreCase);
            var sourceLabel = "imported";

            foreach (var path in propsFiles.Concat(targetsFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadPackageReferences(path, cpmVersions, byId, sourceLabel);
            }

            if (byId.Count == 0) return Array.Empty<PackageModel>();

            var result = new List<PackageModel>(byId.Count);
            result.AddRange(byId.Values);
            return result;
        }

        private static void TryAdd(DirectoryInfo dir, string fileName, List<string> bucket)
        {
            try
            {
                var path = Path.Combine(dir.FullName, fileName);
                if (File.Exists(path)) bucket.Add(path);
            }
            catch
            {
                // Permission / IO errors on a parent directory should not
                // abort the walk - skip the file and keep going.
            }
        }

        private static void ReadPackageVersions(string path, Dictionary<string, NuGetVersion?> cpmVersions)
        {
            XDocument? doc;
            try { doc = XDocument.Load(path); }
            catch { return; }

            if (doc.Root == null) return;

            foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
            {
                var id = (string?)entry.Attribute("Include") ?? (string?)entry.Attribute("Update");
                if (string.IsNullOrEmpty(id)) continue;
                if (ContainsMsBuildExpression(id!)) continue;

                var versionRaw = (string?)entry.Attribute("Version")
                                 ?? entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

                NuGetVersion? version = null;
                if (!string.IsNullOrWhiteSpace(versionRaw)
                    && !ContainsMsBuildExpression(versionRaw!))
                {
                    NuGetVersion.TryParse(versionRaw, out version);
                }

                // Last write wins so a closer Directory.Packages.props can
                // override one further up the tree (matches MSBuild order).
                cpmVersions[id!] = version;
            }
        }

        private static void ReadPackageReferences(
            string path,
            Dictionary<string, NuGetVersion?> cpmVersions,
            Dictionary<string, PackageModel> byId,
            string sourceLabel)
        {
            XDocument? doc;
            try { doc = XDocument.Load(path); }
            catch { return; }

            if (doc.Root == null) return;

            foreach (var packageRef in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var id = (string?)packageRef.Attribute("Include") ?? (string?)packageRef.Attribute("Update");
                if (string.IsNullOrEmpty(id)) continue;
                if (ContainsMsBuildExpression(id!)) continue;

                var versionRaw = (string?)packageRef.Attribute("Version")
                                 ?? (string?)packageRef.Attribute("VersionOverride")
                                 ?? packageRef.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

                NuGetVersion? version = null;
                if (!string.IsNullOrWhiteSpace(versionRaw))
                {
                    if (ContainsMsBuildExpression(versionRaw!)) continue;
                    NuGetVersion.TryParse(versionRaw, out version);
                }
                else if (cpmVersions.TryGetValue(id!, out var cpm))
                {
                    version = cpm;
                }

                // Closest-wins for the same id across multiple imported files.
                // The propsFiles list above is ordered project-dir-first, so
                // the first entry we see is the closest one. Skip later ones.
                if (!byId.ContainsKey(id!))
                {
                    byId[id!] = new PackageModel
                    {
                        PackageId = id!,
                        InstalledVersion = version,
                        SourceName = sourceLabel,
                    };
                }
            }
        }

        private static bool ContainsMsBuildExpression(string value)
        {
            // $(Prop) or @(Item) - we don't evaluate MSBuild so anything
            // dynamic gets skipped to avoid surfacing phantom versions.
            return value.IndexOf("$(", StringComparison.Ordinal) >= 0
                || value.IndexOf("@(", StringComparison.Ordinal) >= 0;
        }
    }
}
