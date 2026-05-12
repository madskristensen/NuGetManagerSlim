using NuGetManagerSlim.Models;
using Xunit;

namespace NuGetManagerSlim.Tests.Models
{
    public class PackageDependencyInfoTests
    {
        [Fact]
        public void DisplayText_WithTargetFramework_IncludesAllParts()
        {
            var dep = new PackageDependencyInfo
            {
                PackageId = "Serilog",
                VersionRange = "[3.0,)",
                TargetFramework = "net8.0",
            };
            Assert.Equal("Serilog [3.0,) [net8.0]", dep.DisplayText);
        }

        [Fact]
        public void DisplayText_WithoutTargetFramework_OmitsBracketedTfm()
        {
            var dep = new PackageDependencyInfo
            {
                PackageId = "Serilog",
                VersionRange = "[3.0,)",
                TargetFramework = string.Empty,
            };
            Assert.Equal("Serilog [3.0,)", dep.DisplayText);
        }

        [Fact]
        public void NameAndVersion_DoesNotIncludeTargetFramework()
        {
            var dep = new PackageDependencyInfo
            {
                PackageId = "Newtonsoft.Json",
                VersionRange = "[13,)",
                TargetFramework = "net48",
            };
            Assert.Equal("Newtonsoft.Json [13,)", dep.NameAndVersion);
            Assert.DoesNotContain("net48", dep.NameAndVersion);
        }
    }
}
