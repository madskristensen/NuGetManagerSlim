namespace NuGetManagerSlim.Models
{
    public class ProjectScopeModel
    {
        public static readonly ProjectScopeModel EntireSolution = new()
        {
            DisplayName = "Entire Solution",
            ProjectFullPath = null,
            IsEntireSolution = true,
        };

        public string DisplayName { get; init; } = string.Empty;
        public string? ProjectFullPath { get; init; }
        public bool IsEntireSolution { get; init; }

        public override string ToString() => DisplayName;
    }
}
