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
                            MergeInstalled(byId, pkg);
                        }
                        continue;
                    }

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

            if (doc?.Root != null)
            {
                foreach (var packageRef in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
                {
                    var id = (string?)packageRef.Attribute("Include") ?? (string?)packageRef.Attribute("Update");
                    if (string.IsNullOrEmpty(id)) continue;

                    var versionRaw = (string?)packageRef.Attribute("Version")
                                     ?? (string?)packageRef.Attribute("VersionOverride")
                                     ?? packageRef.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

                    NuGetVersion? version = null;
                    if (!string.IsNullOrWhiteSpace(versionRaw))
                        NuGetVersion.TryParse(versionRaw, out version);

                    yield return new PackageModel
                    {
                        PackageId = id!,
                        InstalledVersion = version,
                        SourceName = projectName,
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

                        yield return new PackageModel
                        {
                            PackageId = id!,
                            InstalledVersion = version,
                            SourceName = projectName,
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
            if (string.IsNullOrEmpty(scope.ProjectFullPath)) return Array.Empty<string>();
            if (!IsManagedDotNetProject(scope.ProjectFullPath)) return Array.Empty<string>();
            return new[] { scope.ProjectFullPath };
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
                byId[pkg.PackageId] = pkg;
            }
        }

        private async Task<IReadOnlyList<PackageModel>> ReadTransitivesForProjectAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            var projectDir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(projectDir)) return [];

            var assetsPath = Path.Combine(projectDir!, "obj", "project.assets.json");
            if (!File.Exists(assetsPath)) return [];

            cancellationToken.ThrowIfCancellationRequested();

            using var fs = new FileStream(assetsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var doc = await JsonDocument.ParseAsync(fs, default, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("project", out var projectEl)
                && projectEl.TryGetProperty("frameworks", out var fws)
                && fws.ValueKind == JsonValueKind.Object)
            {
                foreach (var fw in fws.EnumerateObject())
                {
                    if (fw.Value.TryGetProperty("dependencies", out var deps)
                        && deps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var d in deps.EnumerateObject())
                            direct.Add(d.Name);
                    }
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
                    break;
                }
            }

            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var result = new List<PackageModel>(nodes.Count);
            foreach (var kvp in nodes)
            {
                if (direct.Contains(kvp.Key)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var ancestor = FindDirectAncestor(kvp.Key, nodes, direct);
                result.Add(new PackageModel
                {
                    PackageId = kvp.Value.Id,
                    InstalledVersion = kvp.Value.Version,
                    IsTransitive = true,
                    RequiredByPackageId = ancestor,
                    SourceName = projectName,
                });
            }

            return result;
        }

        private sealed class TransitiveNode
        {
            public string Id { get; set; } = string.Empty;
            public NuGetVersion? Version { get; set; }
            public List<string> Parents { get; } = new();
        }

        // BFS up the reverse-edge graph until we hit a node that the project
        // declared directly. Returns null when the package somehow has no
        // direct ancestor (shouldn't happen for a well-formed assets file).
        private static string? FindDirectAncestor(
            string id,
            Dictionary<string, TransitiveNode> nodes,
            HashSet<string> direct)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            var queue = new Queue<string>();
            queue.Enqueue(id);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!nodes.TryGetValue(cur, out var node)) continue;
                foreach (var parent in node.Parents)
                {
                    if (direct.Contains(parent)) return parent;
                    if (visited.Add(parent)) queue.Enqueue(parent);
                }
            }
            return null;
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

        public Task UpdatePackageAsync(string projectPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            return InstallPackageAsync(projectPath, packageId, version, cancellationToken);
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
