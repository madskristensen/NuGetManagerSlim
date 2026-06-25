using System.Collections.Generic;
using System.Linq;
using NuGet.Versioning;
using NuGetManagerSlim.Services;
using Xunit;

namespace NuGetManagerSlim.Tests.Services
{
    public class TargetFrameworkCapTests
    {
        [Theory]
        [InlineData("net8.0", 8)]
        [InlineData("net8.0-windows", 8)]
        [InlineData("net10.0", 10)]
        [InlineData("net5.0", 5)]
        [InlineData("netcoreapp3.1", 3)]
        [InlineData("netcoreapp2.0", 2)]
        public void GetDotNetMajor_RecognizedDotNetTargets_ReturnsMajor(string tfm, int expected)
        {
            Assert.Equal(expected, TargetFrameworkCap.GetDotNetMajor(tfm));
        }

        [Theory]
        [InlineData("net48")]
        [InlineData("net472")]
        [InlineData("v4.8")]
        [InlineData("v4.7.2")]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("")]
        [InlineData(null)]
        public void GetDotNetMajor_FrameworkOrStandardOrEmpty_ReturnsNull(string? tfm)
        {
            Assert.Null(TargetFrameworkCap.GetDotNetMajor(tfm));
        }

        [Fact]
        public void ResolveCap_SingleDotNetTarget_ReturnsItsMajor()
        {
            Assert.Equal(8, TargetFrameworkCap.ResolveCap(new[] { "net8.0" }));
        }

        [Fact]
        public void ResolveCap_MultiTarget_ReturnsMinimumMajor()
        {
            Assert.Equal(6, TargetFrameworkCap.ResolveCap(new[] { "net8.0", "net6.0" }));
        }

        [Fact]
        public void ResolveCap_AnyTargetWithoutDotNetMajor_ReturnsNull()
        {
            // Multi-targeting that includes a non-.NET-major framework -> no cap, so
            // updates for that framework are never hidden.
            Assert.Null(TargetFrameworkCap.ResolveCap(new[] { "net8.0", "netstandard2.0" }));
        }

        [Fact]
        public void ResolveCap_EmptyOrNull_ReturnsNull()
        {
            Assert.Null(TargetFrameworkCap.ResolveCap(new string?[0]));
            Assert.Null(TargetFrameworkCap.ResolveCap(null));
        }

        [Theory]
        [InlineData("System.Text.Json", true)]
        [InlineData("System.Memory", true)]
        [InlineData("Microsoft.Extensions.Logging", true)]
        [InlineData("Microsoft.AspNetCore.Mvc.Core", true)]
        [InlineData("Microsoft.EntityFrameworkCore", true)]
        [InlineData("Microsoft.EntityFrameworkCore.SqlServer", true)]
        [InlineData("Microsoft.Windows.Compatibility", true)]
        [InlineData("Microsoft.Data.Sqlite", true)]
        [InlineData("Microsoft.Data.Sqlite.Core", true)]
        [InlineData("Microsoft.Bcl.AsyncInterfaces", true)]
        [InlineData("Microsoft.JSInterop", true)]
        [InlineData("Microsoft.JSInterop.WebAssembly", true)]
        [InlineData("Newtonsoft.Json", false)]
        [InlineData("Serilog", false)]
        [InlineData("Microsoft.Data.SqlClient", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsCappedFamily_MatchesRuntimeCoupledPrefixes(string? id, bool expected)
        {
            Assert.Equal(expected, TargetFrameworkCap.IsCappedFamily(id));
        }
    }

    public class BuildMaxByMajorTests
    {
        private static IReadOnlyList<NuGetVersion> Parse(params string[] versions)
            => versions.Select(NuGetVersion.Parse).ToList();

        [Fact]
        public void BuildMaxByMajor_TracksHighestStablePerMajor()
        {
            var (stable, _) = NuGetFeedService.BuildMaxByMajor(
                Parse("8.0.3", "8.0.4", "8.0.1", "9.0.0", "9.0.1"));

            Assert.Equal(NuGetVersion.Parse("8.0.4"), stable[8]);
            Assert.Equal(NuGetVersion.Parse("9.0.1"), stable[9]);
        }

        [Fact]
        public void BuildMaxByMajor_PrereleaseMapIncludesHighestOverall()
        {
            var (stable, prerelease) = NuGetFeedService.BuildMaxByMajor(
                Parse("8.0.4", "8.1.0-beta", "9.0.0"));

            // Stable map ignores the prerelease.
            Assert.Equal(NuGetVersion.Parse("8.0.4"), stable[8]);
            // Prerelease map captures the higher prerelease for the major.
            Assert.Equal(NuGetVersion.Parse("8.1.0-beta"), prerelease[8]);
            Assert.Equal(NuGetVersion.Parse("9.0.0"), prerelease[9]);
        }

        [Fact]
        public void BuildMaxByMajor_MajorWithOnlyPrerelease_AbsentFromStableMap()
        {
            var (stable, prerelease) = NuGetFeedService.BuildMaxByMajor(
                Parse("8.0.4", "9.0.0-rc.1"));

            Assert.False(stable.ContainsKey(9));
            Assert.Equal(NuGetVersion.Parse("9.0.0-rc.1"), prerelease[9]);
        }
    }
}
