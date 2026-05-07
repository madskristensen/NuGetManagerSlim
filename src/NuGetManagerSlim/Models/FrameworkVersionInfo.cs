using NuGet.Versioning;

namespace NuGetManagerSlim.Models
{
    public class FrameworkVersionInfo
    {
        public string TargetFramework { get; init; } = string.Empty;
        public NuGetVersion? InstalledVersion { get; init; }

        public string DisplayText => InstalledVersion != null
            ? $"v{InstalledVersion} [{TargetFramework}]"
            : $"[{TargetFramework}]";
    }
}
