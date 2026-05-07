using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using NuGet.Versioning;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    public sealed class ProjectService : IProjectService
    {
        public async Task<IReadOnlyList<ProjectScopeModel>> GetProjectsAsync(CancellationToken cancellationToken)
        {
            var scopes = new List<ProjectScopeModel> { ProjectScopeModel.EntireSolution };

            try
            {
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution == null) return scopes;

                var projects = await VS.Solutions.GetAllProjectsAsync();
                foreach (var project in projects)
                {
                    if (project.FullPath == null) continue;
                    scopes.Add(new ProjectScopeModel
                    {
                        DisplayName = project.Name ?? System.IO.Path.GetFileNameWithoutExtension(project.FullPath),
                        ProjectFullPath = project.FullPath,
                        IsEntireSolution = false,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Return empty list if solution not loaded
            }

            return scopes;
        }

        public Task<IReadOnlyList<PackageModel>> GetInstalledPackagesAsync(
            ProjectScopeModel scope,
            CancellationToken cancellationToken)
        {
            // Real implementation would parse .csproj / packages.config for the given scope.
            // Stub returns empty list for now — integration tests drive real behavior.
            return Task.FromResult<IReadOnlyList<PackageModel>>([]);
        }

        public Task InstallPackageAsync(string projectPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            // Delegate to NuGet package management API
            // Real implementation uses IVsPackageInstaller from NuGet.VisualStudio
            return Task.CompletedTask;
        }

        public Task UpdatePackageAsync(string projectPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UninstallPackageAsync(string projectPath, string packageId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
