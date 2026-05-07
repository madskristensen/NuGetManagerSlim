namespace NuGetManagerSlim.Models
{
    public class ProjectScopeModel
    {
        public string DisplayName { get; init; } = string.Empty;
        public string? ProjectFullPath { get; init; }

        public override string ToString() => DisplayName;
    }
}
