using System;
using System.Collections.Generic;

namespace NuGetManagerSlim.Models
{
    public class ProjectScopeModel
    {
        public string DisplayName { get; init; } = string.Empty;

        // Single-project scope: full path to the .csproj/.vbproj/.fsproj.
        // Null when the scope represents the whole solution.
        public string? ProjectFullPath { get; init; }

        // True when this scope represents the entire solution rather than a
        // single project. In that case ProjectFullPaths lists every managed
        // .NET project that participates in the aggregation, and write
        // operations (install / update / uninstall) are not supported.
        public bool IsSolutionScope { get; init; }

        // Populated when IsSolutionScope is true. Empty list otherwise.
        public IReadOnlyList<string> ProjectFullPaths { get; init; } = Array.Empty<string>();

        public override string ToString() => DisplayName;
    }
}
