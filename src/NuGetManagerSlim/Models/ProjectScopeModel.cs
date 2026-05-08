namespace NuGetManagerSlim.Models
{
    public class ProjectScopeModel
    {
        public string DisplayName { get; init; } = string.Empty;

        // Full path to the .csproj/.vbproj/.fsproj that this scope represents.
        public string ProjectFullPath { get; init; } = string.Empty;

        public override string ToString() => DisplayName;
    }
}
