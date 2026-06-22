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
    }
}
