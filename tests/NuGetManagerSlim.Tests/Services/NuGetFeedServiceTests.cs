using System.Linq;
using NuGet.Versioning;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.Services;
using Xunit;

namespace NuGetManagerSlim.Tests.Services
{
    public class NuGetFeedServiceTests
    {
        private static PackageModel Meta() => new()
        {
            PackageId = "Pkg",
            LatestStableVersion = NuGetVersion.Parse("2.0.0"),
        };

        [Fact]
        public void ShouldCacheMetadataResult_PositiveResult_IsCached()
        {
            // A real metadata hit is always safe to cache, even if some other
            // source in the fan-out happened to fault.
            Assert.True(NuGetFeedService.ShouldCacheMetadataResult(Meta(), anySourceFaulted: true));
        }

        [Fact]
        public void ShouldCacheMetadataResult_NotFoundWithNoFailures_IsCached()
        {
            // Every source completed and genuinely had no match: caching the
            // negative result avoids needless re-fetching.
            Assert.True(NuGetFeedService.ShouldCacheMetadataResult(null, anySourceFaulted: false));
        }

        [Fact]
        public void ShouldCacheMetadataResult_NullBecauseAllSourcesFaulted_IsNotCached()
        {
            // Issue #16: a transient all-source failure (offline, proxy hiccup,
            // credential prompt) must NOT be cached as a negative result, or the
            // package is treated as "no update available" for the full 15-minute
            // TTL even after connectivity is restored.
            Assert.False(NuGetFeedService.ShouldCacheMetadataResult(null, anySourceFaulted: true));
        }

        private static NuGetVersion[] Versions(params string[] values)
            => values.Select(NuGetVersion.Parse).ToArray();

        [Fact]
        public void SelectLatestVersions_PrereleaseAboveLatestStable_StillReportsStable()
        {
            // The regression that hid stable updates: the newest version overall is
            // a prerelease, but a lower stable exists. Stable must be the highest
            // non-prerelease, not null.
            var (stable, prerelease) = NuGetFeedService.SelectLatestVersions(
                Versions("4.7.2", "8.0.0", "9.0.0-preview.1"));

            Assert.Equal(NuGetVersion.Parse("8.0.0"), stable);
            Assert.Equal(NuGetVersion.Parse("9.0.0-preview.1"), prerelease);
        }

        [Fact]
        public void SelectLatestVersions_AllPrerelease_StableIsNull()
        {
            var (stable, prerelease) = NuGetFeedService.SelectLatestVersions(
                Versions("1.0.0-alpha", "1.0.0-beta"));

            Assert.Null(stable);
            Assert.Equal(NuGetVersion.Parse("1.0.0-beta"), prerelease);
        }

        [Fact]
        public void SelectLatestVersions_TopVersionIsStable_StableAndPrereleaseMatch()
        {
            var (stable, prerelease) = NuGetFeedService.SelectLatestVersions(
                Versions("1.0.0", "2.0.0-rc", "2.0.0"));

            Assert.Equal(NuGetVersion.Parse("2.0.0"), stable);
            Assert.Equal(NuGetVersion.Parse("2.0.0"), prerelease);
        }

        [Fact]
        public void SelectLatestVersions_Empty_ReturnsNulls()
        {
            var (stable, prerelease) = NuGetFeedService.SelectLatestVersions(Versions());

            Assert.Null(stable);
            Assert.Null(prerelease);
        }

        [Fact]
        public void SelectLatestVersions_Null_ReturnsNulls()
        {
            var (stable, prerelease) = NuGetFeedService.SelectLatestVersions(null!);

            Assert.Null(stable);
            Assert.Null(prerelease);
        }

        [Fact]
        public void SelectLatestVersions_UnorderedInput_PicksHighestOfEach()
        {
            var (stable, prerelease) = NuGetFeedService.SelectLatestVersions(
                Versions("2.0.0", "1.0.0", "3.0.0-beta", "1.5.0", "2.5.0-alpha"));

            Assert.Equal(NuGetVersion.Parse("2.0.0"), stable);
            Assert.Equal(NuGetVersion.Parse("3.0.0-beta"), prerelease);
        }
    }
}
