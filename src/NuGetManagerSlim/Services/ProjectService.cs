using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using NuGet.Versioning;
using NuGet.VisualStudio;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    public sealed class ProjectService : IProjectService
    {
        private readonly object _assetsCacheGate = new();
        private readonly Dictionary<string, AssetsCacheEntry> _assetsCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool IsManagedDotNetProject(string? fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            var ext = Path.GetExtension(fullPath);
            return string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".vbproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IReadOnlyList<PackageModel>> GetInstalledPackagesAsync(
            ProjectScopeModel scope,
            CancellationToken cancellationToken)
        {
            try
            {
                if (scope == null) return [];

                var projectPaths = ResolveScopePaths(scope);
                if (projectPaths.Count == 0) return [];

                var byId = new Dictionary<string, PackageModel>(StringComparer.OrdinalIgnoreCase);
                foreach (var projectPath in projectPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // The project's target framework(s) attribute every package it
                    // references, so the per-package update cap can later be resolved
                    // from only the frameworks that actually use the package (issue
                    // #30). Read once off the dispatcher and stamped onto packages
                    // from the NuGet and imported paths (the raw-XML path stamps its
                    // own results); MergeInstalled unions them across projects.
                    var projectFrameworks = await Task
                        .Run(() => ReadTargetFrameworks(projectPath), cancellationToken)
                        .ConfigureAwait(false);

                    // Prefer NuGet's view of the project (IVsPackageInstallerServices) so
                    // we pick up SDK-injected refs (MSTest.Sdk, etc.), Central Package
                    // Management versions, and conditional PackageReferences that the raw
                    // .csproj XML doesn't expose. Falls back to XDocument parsing only
                    // when the project isn't loaded in the current solution.
                    var fromNuGet = await TryGetInstalledFromNuGetAsync(projectPath, cancellationToken).ConfigureAwait(false);
                    if (fromNuGet != null)
                    {
                        foreach (var pkg in fromNuGet)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            MergeInstalled(byId, pkg.WithReferencingFrameworks(projectFrameworks));
                        }

                        // The NuGet API gives us resolved versions but not version range
                        // constraints. Layer them in from the raw project XML so that
                        // HasUpdate can respect allowedVersions / range-syntax Version.
                        foreach (var pkg in ReadInstalledFromProject(projectPath))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            MergeInstalled(byId, pkg);
                        }
                    }
                    else
                    {
                        // The project file is parsed with synchronous XDocument.Load, which
                        // would otherwise stall the UI thread on every filter toggle / project
                        // switch. Hop to the threadpool so the I/O + parse runs off
                        // the dispatcher.
                        await TaskScheduler.Default;

                        foreach (var pkg in ReadInstalledFromProject(projectPath))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            MergeInstalled(byId, pkg);
                        }
                    }

                    // Imported PackageReferences from Directory.Build.props /
                    // Directory.Build.targets are invisible to both the
                    // IVsPackageInstallerServices view (CPM-injected) and the
                    // raw .csproj XML, so always layer them on top regardless
                    // of which branch above produced the project's own list.
                    await TaskScheduler.Default;
                    var imported = MsBuildImportedPackageReader.ReadImportedPackages(projectPath, cancellationToken);
                    foreach (var pkg in imported)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        MergeInstalled(byId, pkg.WithReferencingFrameworks(projectFrameworks));
                    }
                }

                return byId.Values
                    .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return [];
            }
        }

        // Returns null when the project is not loaded in the current solution
        // (so the caller can fall back to raw XML parsing). Returns an empty
        // list when the project is loaded but has no installed packages.
        private static async Task<IReadOnlyList<PackageModel>?> TryGetInstalledFromNuGetAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var project = await FindDteProjectAsync(projectPath);
            if (project == null) return null;

            var services = await GetServiceAsync<IVsPackageInstallerServices>();
            if (services == null) return null;

            var projectName = Path.GetFileNameWithoutExtension(projectPath);

            // GetInstalledPackages(Project) walks NuGet's package management
            // pipeline which can do MEF activation and (rarely) brief I/O.
            // Marshal to the threadpool to avoid blocking the dispatcher.
            await TaskScheduler.Default;

            cancellationToken.ThrowIfCancellationRequested();

            IEnumerable<IVsPackageMetadata> installed;
            try
            {
                installed = services.GetInstalledPackages(project);
            }
            catch (Exception ex)
            {
                // Project type that NuGet doesn't recognize - treat as "not loaded"
                // so the XDocument fallback runs.
                await ex.LogAsync();
                return null;
            }

            var result = new List<PackageModel>();
            foreach (var meta in installed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(meta?.Id)) continue;

                NuGetVersion? version = null;
                if (!string.IsNullOrWhiteSpace(meta!.VersionString))
                    NuGetVersion.TryParse(meta.VersionString, out version);

                result.Add(new PackageModel
                {
                    PackageId = meta.Id,
                    InstalledVersion = version,
                    SourceName = projectName,
                });
            }

            return result;
        }

        // Disk-only read: combines the project's own PackageReference /
        // packages.config entries with any PackageReferences declared in
        // Directory.Build.props / Directory.Build.targets /
        // Directory.Packages.props above the project. Unit-test friendly:
        // does not touch VS services, so tests can exercise the merge logic
        // without spinning up a shell host.
        public static IReadOnlyList<PackageModel> ReadInstalledFromProjectWithImports(
            string projectFullPath,
            CancellationToken cancellationToken)
        {
            var byId = new Dictionary<string, PackageModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var pkg in ReadInstalledFromProject(projectFullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MergeInstalled(byId, pkg);
            }

            // Imported packages belong to the project too, so attribute the
            // project's framework(s) to them for the per-package update cap
            // (issue #30), mirroring GetInstalledPackagesAsync.
            var projectFrameworks = ReadTargetFrameworks(projectFullPath);
            foreach (var pkg in MsBuildImportedPackageReader.ReadImportedPackages(projectFullPath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MergeInstalled(byId, pkg.WithReferencingFrameworks(projectFrameworks));
            }
            return byId.Values
                .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Reads installed packages from a project file by inspecting <PackageReference>
        // entries (SDK-style / PackageReference) and a sibling packages.config (legacy).
        // Transitive packages are not included; this is intended to mirror the
        // top-level dependencies that the user added via NuGet.
        public static IEnumerable<PackageModel> ReadInstalledFromProject(string projectFullPath)
        {
            if (string.IsNullOrEmpty(projectFullPath) || !File.Exists(projectFullPath))
                yield break;

            var projectName = Path.GetFileNameWithoutExtension(projectFullPath);

            XDocument? doc = null;
            try { doc = XDocument.Load(projectFullPath); }
            catch { doc = null; }

            // The project's own target framework(s) attribute every package it
            // declares so the per-package update cap can be resolved later from
            // only the frameworks that actually reference the package (issue #30).
            // SDK-style projects commonly centralize the framework in a
            // Directory.Build.props above the project, so fall back to it when the
            // project file itself declares none (issue #32).
            var frameworks = doc?.Root != null
                ? ExtractTargetFrameworks(doc)
                : (IReadOnlyList<string>)Array.Empty<string>();
            if (frameworks.Count == 0)
                frameworks = ReadTargetFrameworksFromImports(projectFullPath);

            if (doc?.Root != null)
            {
                foreach (var packageRef in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
                {
                    var id = PackageReferenceXml.ReadId(packageRef);
                    if (string.IsNullOrEmpty(id)) continue;

                    var versionRaw = PackageReferenceXml.ReadVersionRaw(packageRef);

                    NuGetVersion? version = null;
                    VersionRange? allowedRange = null;
                    if (!string.IsNullOrWhiteSpace(versionRaw))
                    {
                        if (!NuGetVersion.TryParse(versionRaw, out version))
                            VersionRange.TryParse(versionRaw, out allowedRange);
                    }

                    yield return new PackageModel
                    {
                        PackageId = id!,
                        InstalledVersion = version,
                        AllowedVersionRange = allowedRange,
                        SourceName = projectName,
                        ReferencingFrameworks = frameworks,
                    };
                }
            }

            var packagesConfig = Path.Combine(Path.GetDirectoryName(projectFullPath) ?? string.Empty, "packages.config");
            if (File.Exists(packagesConfig))
            {
                XDocument? legacy = null;
                try { legacy = XDocument.Load(packagesConfig); }
                catch { legacy = null; }

                if (legacy?.Root != null)
                {
                    foreach (var package in legacy.Descendants().Where(e => e.Name.LocalName == "package"))
                    {
                        var id = (string?)package.Attribute("id");
                        if (string.IsNullOrEmpty(id)) continue;

                        var versionRaw = (string?)package.Attribute("version");
                        NuGetVersion? version = null;
                        if (!string.IsNullOrWhiteSpace(versionRaw))
                            NuGetVersion.TryParse(versionRaw, out version);

                        var allowedVersionsRaw = (string?)package.Attribute("allowedVersions");
                        VersionRange? allowedRange = null;
                        if (!string.IsNullOrWhiteSpace(allowedVersionsRaw))
                            VersionRange.TryParse(allowedVersionsRaw, out allowedRange);

                        yield return new PackageModel
                        {
                            PackageId = id!,
                            InstalledVersion = version,
                            AllowedVersionRange = allowedRange,
                            SourceName = projectName,
                            ReferencingFrameworks = frameworks,
                        };
                    }
                }
            }
        }

        // Reads the resolved transitive dependency graph from the project's
        // restore output (obj/project.assets.json). Returns packages that are
        // pulled in by direct PackageReferences but not declared themselves.
        // Best-effort: returns an empty list if the project hasn't been
        // restored yet, the assets file is malformed, or the project isn't
        // SDK-style. RequiredByPackageId is set to one direct ancestor
        // (BFS up the dependency edges) so the UI can show "required by: X".
        public async Task<IReadOnlyList<PackageModel>> GetTransitivePackagesAsync(
            ProjectScopeModel scope,
            CancellationToken cancellationToken)
        {
            try
            {
                if (scope == null) return [];

                var projectPaths = ResolveScopePaths(scope);
                if (projectPaths.Count == 0) return [];

                await TaskScheduler.Default;

                var aggregated = new Dictionary<string, PackageModel>(StringComparer.OrdinalIgnoreCase);
                foreach (var projectPath in projectPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var perProject = await ReadTransitivesForProjectAsync(projectPath, cancellationToken).ConfigureAwait(false);
                    foreach (var pkg in perProject)
                    {
                        MergeInstalled(aggregated, pkg);
                    }
                }

                return aggregated.Values
                    .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return [];
            }
        }

        private static IReadOnlyList<string> ResolveScopePaths(ProjectScopeModel scope)
        {
            if (scope.IsSolutionScope)
            {
                if (scope.ProjectFullPaths == null || scope.ProjectFullPaths.Count == 0)
                    return Array.Empty<string>();

                var list = new List<string>(scope.ProjectFullPaths.Count);
                foreach (var path in scope.ProjectFullPaths)
                {
                    if (IsManagedDotNetProject(path)) list.Add(path);
                }
                return list;
            }

            if (string.IsNullOrEmpty(scope.ProjectFullPath)) return Array.Empty<string>();
            if (!IsManagedDotNetProject(scope.ProjectFullPath)) return Array.Empty<string>();
            return new[] { scope.ProjectFullPath };
        }

        public async Task<IReadOnlyDictionary<string, NuGetVersion?>> GetInstalledVersionsPerProjectAsync(
            ProjectScopeModel scope,
            string packageId,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, NuGetVersion?>(StringComparer.OrdinalIgnoreCase);
            if (scope == null || string.IsNullOrEmpty(packageId)) return result;

            var projectPaths = ResolveScopePaths(scope);
            if (projectPaths.Count == 0) return result;

            foreach (var projectPath in projectPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var subScope = new ProjectScopeModel
                {
                    ProjectFullPath = projectPath,
                    ProjectFullPaths = new[] { projectPath },
                    ScopeKind = ProjectScopeKind.Project,
                    DisplayName = Path.GetFileNameWithoutExtension(projectPath),
                };

                var installed = await GetInstalledPackagesAsync(subScope, cancellationToken).ConfigureAwait(false);
                NuGetVersion? version = null;
                foreach (var pkg in installed)
                {
                    if (string.Equals(pkg.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                    {
                        version = pkg.InstalledVersion;
                        break;
                    }
                }

                if (version == null)
                {
                    var transitive = await ReadTransitivesForProjectAsync(projectPath, cancellationToken).ConfigureAwait(false);
                    foreach (var pkg in transitive)
                    {
                        if (pkg.IsCentralTransitivePin
                            && string.Equals(pkg.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                        {
                            version = pkg.InstalledVersion;
                            break;
                        }
                    }
                }

                result[projectPath] = version;
            }

            return result;
        }

        // Reads the declared target framework moniker(s) from a project file:
        // SDK-style <TargetFramework> / <TargetFrameworks> (short monikers like
        // net8.0) and the legacy non-SDK <TargetFrameworkVersion> (e.g. v4.8).
        // Best-effort: returns an empty list when the file is missing or unreadable.
        private static IReadOnlyList<string> ReadTargetFrameworks(string projectFullPath)
        {
            if (string.IsNullOrEmpty(projectFullPath) || !File.Exists(projectFullPath))
                return Array.Empty<string>();

            XDocument? doc;
            try { doc = XDocument.Load(projectFullPath); }
            catch { return Array.Empty<string>(); }

            if (doc?.Root == null) return Array.Empty<string>();

            var fromProject = ExtractTargetFrameworks(doc);
            if (fromProject.Count > 0) return fromProject;

            // SDK-style projects frequently centralize <TargetFramework(s)> in a
            // Directory.Build.props above the project. When the project file
            // declares none, walk up and read the nearest import that does, so the
            // per-package update cap still applies to net-major targets (issue #32).
            return ReadTargetFrameworksFromImports(projectFullPath);
        }

        // Walks up from the project directory looking for the nearest
        // Directory.Build.props / Directory.Build.targets that declares a literal
        // target framework, mirroring how MSBuild imports the closest file. Only
        // literal monikers are honored; entries containing MSBuild expressions
        // ($(...) / @(...)) are skipped because we don't evaluate MSBuild here.
        // Best-effort: returns an empty list when nothing literal is found.
        private static IReadOnlyList<string> ReadTargetFrameworksFromImports(string projectFullPath)
        {
            var projectDir = Path.GetDirectoryName(projectFullPath);
            if (string.IsNullOrEmpty(projectDir)) return Array.Empty<string>();

            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(projectDir); }
            catch { return Array.Empty<string>(); }

            while (dir != null)
            {
                foreach (var fileName in ImportFileNames)
                {
                    string path;
                    try { path = Path.Combine(dir.FullName, fileName); }
                    catch { continue; }

                    if (!File.Exists(path)) continue;

                    XDocument? doc;
                    try { doc = XDocument.Load(path); }
                    catch { continue; }

                    if (doc?.Root == null) continue;

                    var tfms = ExtractTargetFrameworks(doc)
                        .Where(t => t.IndexOf("$(", StringComparison.Ordinal) < 0
                                 && t.IndexOf("@(", StringComparison.Ordinal) < 0)
                        .ToList();

                    if (tfms.Count > 0) return tfms;
                }

                try { dir = dir.Parent; }
                catch { break; }
            }

            return Array.Empty<string>();
        }

        private static readonly string[] ImportFileNames =
        {
            "Directory.Build.props",
            "Directory.Build.targets",
        };

        // Pulls the target framework moniker(s) out of an already-loaded project
        // document, so callers that parse the project file for other reasons
        // (e.g. PackageReference discovery) don't have to load it a second time.
        private static IReadOnlyList<string> ExtractTargetFrameworks(XDocument doc)
        {
            var result = new List<string>();
            if (doc?.Root == null) return result;

            foreach (var element in doc.Descendants())
            {
                var name = element.Name.LocalName;
                if (string.Equals(name, "TargetFramework", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "TargetFrameworkVersion", StringComparison.OrdinalIgnoreCase))
                {
                    var value = element.Value?.Trim();
                    if (!string.IsNullOrEmpty(value)) result.Add(value);
                }
                else if (string.Equals(name, "TargetFrameworks", StringComparison.OrdinalIgnoreCase))
                {
                    var value = element.Value?.Trim();
                    if (!string.IsNullOrEmpty(value))
                    {
                        foreach (var tfm in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var trimmed = tfm.Trim();
                            if (trimmed.Length > 0) result.Add(trimmed);
                        }
                    }
                }
            }

            return result;
        }

        // Dedupe by package id within a single project (e.g. the same id
        // appears in both PackageReference and packages.config). Keeps the
        // highest installed version observed.
        private static void MergeInstalled(Dictionary<string, PackageModel> byId, PackageModel pkg)
        {
            if (string.IsNullOrEmpty(pkg.PackageId)) return;
            if (!byId.TryGetValue(pkg.PackageId, out var existing))
            {
                byId[pkg.PackageId] = pkg;
                return;
            }

            var newer = pkg.InstalledVersion != null
                && (existing.InstalledVersion == null || pkg.InstalledVersion > existing.InstalledVersion);

            if (newer)
            {
                // Keep the higher version. Only carry AllowedVersionRange from the existing entry
                // when the newer InstalledVersion still falls within that range - this prevents a
                // range constraint from one project from leaking onto a higher version resolved by
                // a different project in the same solution.
                byId[pkg.PackageId] = existing.AllowedVersionRange != null
                    && pkg.AllowedVersionRange == null
                    && existing.AllowedVersionRange.Satisfies(pkg.InstalledVersion!)
                    ? pkg.WithAllowedVersionRange(existing.AllowedVersionRange)
                    : pkg;
            }
            else if (pkg.AllowedVersionRange != null && existing.AllowedVersionRange == null)
            {
                // Carry over the range constraint only when the existing InstalledVersion falls
                // within the incoming range. Without this guard a range constraint declared in
                // one project can contaminate a higher-versioned entry from another project.
                if (existing.InstalledVersion == null
                    || pkg.AllowedVersionRange.Satisfies(existing.InstalledVersion))
                {
                    byId[pkg.PackageId] = existing.WithAllowedVersionRange(pkg.AllowedVersionRange);
                }
            }

            // A transitive package can be pulled in by different direct packages in
            // different projects, so union the "required by" sets across projects
            // (issue #19). Applies to whichever entry was kept above.
            if ((existing.RequiredByPackageIds.Count > 0 || pkg.RequiredByPackageIds.Count > 0))
            {
                var union = new SortedSet<string>(existing.RequiredByPackageIds, StringComparer.OrdinalIgnoreCase);
                union.UnionWith(pkg.RequiredByPackageIds);
                if (union.Count != byId[pkg.PackageId].RequiredByPackageIds.Count)
                {
                    byId[pkg.PackageId] = byId[pkg.PackageId].WithRequiredBy(new List<string>(union));
                }
            }

            // The same package can be referenced from several projects targeting
            // different frameworks, so union the referencing frameworks across
            // projects (issue #30). The per-package cap is then resolved from the
            // full set, matching the existing "any non-.NET target => no cap" rule.
            if (existing.ReferencingFrameworks.Count > 0 || pkg.ReferencingFrameworks.Count > 0)
            {
                var frameworkUnion = new SortedSet<string>(existing.ReferencingFrameworks, StringComparer.OrdinalIgnoreCase);
                frameworkUnion.UnionWith(pkg.ReferencingFrameworks);
                var current = byId[pkg.PackageId];
                if (frameworkUnion.Count != current.ReferencingFrameworks.Count)
                {
                    byId[pkg.PackageId] = current.WithReferencingFrameworks(new List<string>(frameworkUnion));
                }
            }

            if ((existing.IsCentralTransitivePin || pkg.IsCentralTransitivePin)
                && !byId[pkg.PackageId].IsCentralTransitivePin)
            {
                byId[pkg.PackageId] = byId[pkg.PackageId].WithCentralTransitivePin();
            }
        }

        private async Task<IReadOnlyList<PackageModel>> ReadTransitivesForProjectAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            var snapshot = await ReadAssetsSnapshotAsync(projectPath, cancellationToken).ConfigureAwait(false);
            return snapshot?.Packages ?? [];
        }

        private async Task<AssetsSnapshot?> ReadAssetsSnapshotAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            var projectDir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(projectDir)) return null;

            var assetsPath = Path.Combine(projectDir!, "obj", "project.assets.json");
            if (!File.Exists(assetsPath)) return null;

            cancellationToken.ThrowIfCancellationRequested();

            var before = new FileInfo(assetsPath);
            var length = before.Length;
            var lastWriteTimeUtc = before.LastWriteTimeUtc;
            lock (_assetsCacheGate)
            {
                if (_assetsCache.TryGetValue(assetsPath, out var cached)
                    && cached.Length == length
                    && cached.LastWriteTimeUtc == lastWriteTimeUtc)
                {
                    return cached.Snapshot;
                }
            }

            using var fs = new FileStream(assetsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var doc = await JsonDocument.ParseAsync(fs, default, cancellationToken).ConfigureAwait(false);

            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var snapshot = ParseAssetsSnapshot(doc.RootElement, projectName, cancellationToken);

            var after = new FileInfo(assetsPath);
            if (after.Exists
                && after.Length == length
                && after.LastWriteTimeUtc == lastWriteTimeUtc)
            {
                lock (_assetsCacheGate)
                {
                    _assetsCache[assetsPath] = new AssetsCacheEntry(
                        length,
                        lastWriteTimeUtc,
                        snapshot);
                }
            }

            return snapshot;
        }

        // Builds the transitive-dependency list from a parsed project.assets.json
        // root element. Kept free of file IO and VS dependencies so the graph and
        // ancestor-resolution logic can be unit tested directly.
        public static IReadOnlyList<PackageModel> ParseTransitivesFromAssets(
            JsonElement root,
            string projectName,
            CancellationToken cancellationToken)
        {
            return ParseAssetsSnapshot(root, projectName, cancellationToken).Packages;
        }

        private static AssetsSnapshot ParseAssetsSnapshot(
            JsonElement root,
            string projectName,
            CancellationToken cancellationToken)
        {
            var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var projectFrameworks = new List<string>();
            if (root.TryGetProperty("project", out var projectEl)
                && projectEl.TryGetProperty("frameworks", out var fws)
                && fws.ValueKind == JsonValueKind.Object)
            {
                foreach (var fw in fws.EnumerateObject())
                {
                    projectFrameworks.Add(fw.Name);
                    if (fw.Value.TryGetProperty("dependencies", out var deps)
                        && deps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var d in deps.EnumerateObject())
                            direct.Add(d.Name);
                    }
                }
            }

            var centralTransitivePins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("centralTransitiveDependencyGroups", out var centralGroups)
                && centralGroups.ValueKind == JsonValueKind.Object)
            {
                foreach (var frameworkGroup in centralGroups.EnumerateObject())
                {
                    if (frameworkGroup.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var package in frameworkGroup.Value.EnumerateObject())
                        centralTransitivePins.Add(package.Name);
                }
            }

            var nodes = new Dictionary<string, TransitiveNode>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Object)
            {
                foreach (var tf in targets.EnumerateObject())
                {
                    foreach (var entry in tf.Value.EnumerateObject())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = entry.Name;
                        var slash = key.IndexOf('/');
                        if (slash <= 0) continue;
                        var id = key.Substring(0, slash);
                        var verStr = key.Substring(slash + 1);
                        if (!entry.Value.TryGetProperty("type", out var typeEl)
                            || typeEl.ValueKind != JsonValueKind.String
                            || typeEl.GetString() != "package")
                            continue;
                        NuGetVersion.TryParse(verStr, out var ver);

                        if (!nodes.TryGetValue(id, out var node))
                        {
                            node = new TransitiveNode { Id = id, Version = ver };
                            nodes[id] = node;
                        }
                        else if (node.Version == null && ver != null)
                        {
                            node.Version = ver;
                        }

                        if (entry.Value.TryGetProperty("dependencies", out var children)
                            && children.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var child in children.EnumerateObject())
                            {
                                if (!nodes.TryGetValue(child.Name, out var childNode))
                                {
                                    childNode = new TransitiveNode { Id = child.Name };
                                    nodes[child.Name] = childNode;
                                }
                                if (!childNode.Parents.Contains(id, StringComparer.OrdinalIgnoreCase))
                                    childNode.Parents.Add(id);
                            }
                        }
                    }
                }
            }

            var result = new List<PackageModel>(nodes.Count);
            foreach (var kvp in nodes)
            {
                if (direct.Contains(kvp.Key)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var ancestors = FindDirectAncestors(kvp.Key, nodes, direct);
                result.Add(new PackageModel
                {
                    PackageId = kvp.Value.Id,
                    InstalledVersion = kvp.Value.Version,
                    IsTransitive = true,
                    IsCentralTransitivePin = centralTransitivePins.Contains(kvp.Key),
                    RequiredByPackageId = ancestors.Count > 0 ? ancestors[0] : null,
                    RequiredByPackageIds = ancestors,
                    ReferencingFrameworks = projectFrameworks,
                    SourceName = projectName,
                });
            }

            return new AssetsSnapshot(result, centralTransitivePins);
        }

        private sealed class AssetsSnapshot
        {
            public AssetsSnapshot(
                IReadOnlyList<PackageModel> packages,
                HashSet<string> centralTransitivePins)
            {
                Packages = packages;
                CentralTransitivePins = centralTransitivePins;
            }

            public IReadOnlyList<PackageModel> Packages { get; }
            public HashSet<string> CentralTransitivePins { get; }
        }

        private sealed class AssetsCacheEntry
        {
            public AssetsCacheEntry(
                long length,
                DateTime lastWriteTimeUtc,
                AssetsSnapshot snapshot)
            {
                Length = length;
                LastWriteTimeUtc = lastWriteTimeUtc;
                Snapshot = snapshot;
            }

            public long Length { get; }
            public DateTime LastWriteTimeUtc { get; }
            public AssetsSnapshot Snapshot { get; }
        }

        private sealed class TransitiveNode
        {
            public string Id { get; set; } = string.Empty;
            public NuGetVersion? Version { get; set; }
            public List<string> Parents { get; } = new();
        }

        // BFS up the reverse-edge graph collecting every direct dependency that
        // (transitively) pulls in this package, so the UI can show "required by:
        // X, Y" like the built-in NuGet Package Manager (issue #19). Returns an
        // empty list when the package somehow has no direct ancestor (shouldn't
        // happen for a well-formed assets file). Ancestors are ordered
        // alphabetically for stable display.
        private static IReadOnlyList<string> FindDirectAncestors(
            string id,
            Dictionary<string, TransitiveNode> nodes,
            HashSet<string> direct)
        {
            var ancestors = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            var queue = new Queue<string>();
            queue.Enqueue(id);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!nodes.TryGetValue(cur, out var node)) continue;
                foreach (var parent in node.Parents)
                {
                    if (direct.Contains(parent))
                    {
                        ancestors.Add(parent);
                        continue;
                    }
                    if (visited.Add(parent)) queue.Enqueue(parent);
                }
            }
            return ancestors.Count == 0 ? [] : new List<string>(ancestors);
        }

        public async Task InstallPackageAsync(string projectPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var project = await FindDteProjectAsync(projectPath);
            if (project == null) throw new InvalidOperationException($"Could not find project '{projectPath}' in the current solution.");

            var installer = await GetServiceAsync<IVsPackageInstaller>();
            if (installer == null) throw new InvalidOperationException("IVsPackageInstaller is not available.");

            // IVsPackageInstaller.InstallPackage is documented as thread-safe
            // and synchronously blocks for the duration of the install
            // (network + restore). Run it on the thread pool so the UI stays
            // responsive; COM marshaling for the DTE Project handle is
            // handled internally by the wrapper.
            await Task.Run(
                () => installer.InstallPackage((string?)null, project, packageId, version.ToNormalizedString(), false),
                cancellationToken);

            await SaveProjectAsync(project);
        }

        public async Task UpdatePackageAsync(
            string projectPath,
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken)
        {
            if (await IsCentralTransitivePinAsync(projectPath, packageId, cancellationToken).ConfigureAwait(false))
            {
                await Task.Run(
                    () => UpdateCentralPackageVersion(projectPath, packageId, version, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await InstallPackageAsync(projectPath, packageId, version, cancellationToken);
        }

        private async Task<bool> IsCentralTransitivePinAsync(
            string projectPath,
            string packageId,
            CancellationToken cancellationToken)
        {
            var snapshot = await ReadAssetsSnapshotAsync(projectPath, cancellationToken).ConfigureAwait(false);
            return snapshot?.CentralTransitivePins.Contains(packageId) == true;
        }

        private static void UpdateCentralPackageVersion(
            string projectPath,
            string packageId,
            NuGetVersion version,
            CancellationToken cancellationToken)
        {
            var projectDir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(projectDir))
                throw new InvalidOperationException($"Could not locate Directory.Packages.props for '{projectPath}'.");

            var matches = new List<(string Path, XDocument Document, XElement Entry)>();
            var dir = new DirectoryInfo(projectDir);
            while (dir != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Path.Combine(dir.FullName, "Directory.Packages.props");
                if (File.Exists(candidate))
                {
                    var candidateDoc = XDocument.Load(candidate, LoadOptions.PreserveWhitespace);
                    var candidateMatches = candidateDoc.Descendants()
                        .Where(e => e.Name.LocalName == "PackageVersion")
                        .Where(e => string.Equals(
                            (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"),
                            packageId,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var candidateEntry in candidateMatches)
                    {
                        matches.Add((candidate, candidateDoc, candidateEntry));
                    }

                    if (candidateMatches.Count > 0)
                        break;

                    var importsParentPackagesFile = candidateDoc.Descendants()
                        .Where(e => e.Name.LocalName == "Import")
                        .Select(e => (string?)e.Attribute("Project"))
                        .Any(p => p?.IndexOf("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!importsParentPackagesFile)
                        break;
                }
                dir = dir.Parent;
            }

            if (matches.Count == 0)
                throw new InvalidOperationException($"Could not find a central version for '{packageId}' above '{projectPath}'.");
            if (matches.Count > 1)
                throw new InvalidOperationException($"Multiple central versions for '{packageId}' were found above '{projectPath}'. Update them explicitly.");

            var match = matches[0];
            var entry = match.Entry;
            if (entry.AncestorsAndSelf().Any(e => e.Attribute("Condition") != null))
                throw new InvalidOperationException($"The central version for '{packageId}' in '{match.Path}' is conditional. Update it explicitly.");

            var versionValue = version.ToNormalizedString();
            var versionAttribute = entry.Attribute("Version");
            if (versionAttribute != null)
            {
                if (ContainsMsBuildExpression(versionAttribute.Value))
                    throw new InvalidOperationException($"The central version for '{packageId}' in '{match.Path}' uses an MSBuild expression. Update it explicitly.");
                versionAttribute.Value = versionValue;
            }
            else
            {
                var versionElement = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Version");
                if (versionElement == null)
                    throw new InvalidOperationException($"The central version for '{packageId}' in '{match.Path}' has no Version value.");
                if (ContainsMsBuildExpression(versionElement.Value))
                    throw new InvalidOperationException($"The central version for '{packageId}' in '{match.Path}' uses an MSBuild expression. Update it explicitly.");
                versionElement.Value = versionValue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            match.Document.Save(match.Path, SaveOptions.DisableFormatting);
        }

        private static bool ContainsMsBuildExpression(string value)
        {
            return value.IndexOf("$(", StringComparison.Ordinal) >= 0
                || value.IndexOf("@(", StringComparison.Ordinal) >= 0;
        }

        public async Task UninstallPackageAsync(string projectPath, string packageId, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var project = await FindDteProjectAsync(projectPath);
            if (project == null) throw new InvalidOperationException($"Could not find project '{projectPath}' in the current solution.");

            var uninstaller = await GetServiceAsync<IVsPackageUninstaller>();
            if (uninstaller == null) throw new InvalidOperationException("IVsPackageUninstaller is not available.");

            await Task.Run(
                () => uninstaller.UninstallPackage(project, packageId, removeDependencies: false),
                cancellationToken);

            await SaveProjectAsync(project);
        }

        // SDK-style projects modified by IVsPackageInstaller / IVsPackageUninstaller
        // are dirtied in memory but not flushed to disk, so a subsequent restore-monitor
        // refresh sees stale package references. Force-save the project (and the solution)
        // so the new PackageReference list is on disk before we reload.
        private static async Task SaveProjectAsync(EnvDTE.Project project)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                project.Save();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private static async Task<T?> GetServiceAsync<T>() where T : class
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var componentModel = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            return componentModel?.GetService<T>();
        }

        private static async Task<Project?> FindDteProjectAsync(string projectPath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(DTE)) as DTE;
            if (dte?.Solution == null) return null;

            foreach (Project project in EnumerateProjects(dte.Solution.Projects))
            {
                string? fullName = null;
                try { fullName = project.FullName; } catch { }
                if (!string.IsNullOrEmpty(fullName)
                    && string.Equals(fullName, projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    return project;
                }
            }
            return null;
        }

        private static IEnumerable<Project> EnumerateProjects(Projects projects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Project project in projects)
            {
                if (project == null) continue;
                if (project.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}")
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        if (item.SubProject != null)
                        {
                            foreach (var sub in EnumerateSolutionFolderProjects(item.SubProject))
                                yield return sub;
                        }
                    }
                }
                else
                {
                    yield return project;
                }
            }
        }

        private static IEnumerable<Project> EnumerateSolutionFolderProjects(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}")
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject != null)
                    {
                        foreach (var sub in EnumerateSolutionFolderProjects(item.SubProject))
                            yield return sub;
                    }
                }
            }
            else
            {
                yield return project;
            }
        }
    }
}
