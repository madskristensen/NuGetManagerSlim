namespace NuGetManagerSlim.Models
{
    public class PackageSourceModel
    {
        public string Name { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public bool IsEnabled { get; set; }
        public bool HasError { get; set; }
        public string? ErrorMessage { get; set; }
        public bool RequiresAuthentication { get; set; }
    }
}
