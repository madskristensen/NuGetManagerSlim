using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                if (scope == null || string.IsNullOrEmpty(scope.ProjectFullPath))
                    return [];

                // The project file is parsed with synchronous XDocument.Load, which
                // would otherwise stall the UI thread on every filter toggle / project
                // switch. Hop to the threadpool via JTF so the I/O + parse runs off
                // the dispatcher.
                await TaskScheduler.Default;

                var projectPath = scope.ProjectFullPath!;
                cancellationToken.ThrowIfCancellationRequested();

                var byId = new Dictionary<string, PackageModel>(StringComparer.OrdinalIgnoreCase);
                foreach (var pkg in ReadInstalledFromProject(projectPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!byId.ContainsKey(pkg.PackageId))
                    {
                        byId[pkg.PackageId] = pkg;
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
