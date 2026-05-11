using System;
using System.Collections.Generic;

namespace NuGetManagerSlim.Models
{
    public enum ProjectScopeKind
    {
        Project,
        Solution,
    }

    public class ProjectScopeModel
    {
        public string DisplayName { get; init; } = string.Empty;

        // Full path to the .csproj/.vbproj/.fsproj that this scope represents
        // when ScopeKind is Project. Empty when ScopeKind is Solution.
        public string ProjectFullPath { get; init; } = string.Empty;

        public ProjectScopeKind ScopeKind { get; init; } = ProjectScopeKind.Project;

        // Full paths of every project this scope covers. For Project scope
        // this is a single entry (ProjectFullPath); for Solution scope it
        // enumerates every loaded managed project in the solution.
        public IReadOnlyList<string> ProjectFullPaths { get; init; } = Array.Empty<string>();

        public bool IsSolutionScope => ScopeKind == ProjectScopeKind.Solution;

        public override string ToString() => DisplayName;
    }
}
