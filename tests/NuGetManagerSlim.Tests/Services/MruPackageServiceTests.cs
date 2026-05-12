using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.Services;
using Xunit;

namespace NuGetManagerSlim.Tests.Services
{
    public class MruPackageServiceTests : IDisposable
    {
        private readonly string _tempFile;

        public MruPackageServiceTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"mru-{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        }

        private MruPackageService CreateService() => new(_tempFile);

        [Fact]
        public async Task GetRecentAsync_OnFreshFile_ReturnsEmpty()
        {
            var svc = CreateService();
            var entries = await svc.GetRecentAsync(CancellationToken.None);
            Assert.Empty(entries);
        }

        [Fact]
        public async Task RecordAsync_PersistsAcrossInstances()
        {
            var svc1 = CreateService();
            await svc1.RecordAsync(new PackageModel
            {
                PackageId = "Newtonsoft.Json",
                InstalledVersion = NuGetVersion.Parse("13.0.3"),
                Authors = "James Newton-King",
            }, CancellationToken.None);

            var svc2 = CreateService();
            var entries = await svc2.GetRecentAsync(CancellationToken.None);

            Assert.Single(entries);
            Assert.Equal("Newtonsoft.Json", entries[0].PackageId);
            Assert.Equal("13.0.3", entries[0].LatestStableVersion?.ToNormalizedString());
        }

        [Fact]
        public async Task RecordAsync_RecordingSamePackage_DeduplicatesAndMovesToTop()
        {
            var svc = CreateService();
            await svc.RecordAsync(new PackageModel { PackageId = "A", InstalledVersion = NuGetVersion.Parse("1.0.0") }, CancellationToken.None);
            await svc.RecordAsync(new PackageModel { PackageId = "B", InstalledVersion = NuGetVersion.Parse("2.0.0") }, CancellationToken.None);
            await svc.RecordAsync(new PackageModel { PackageId = "A", InstalledVersion = NuGetVersion.Parse("1.0.1") }, CancellationToken.None);

            var entries = await svc.GetRecentAsync(CancellationToken.None);

            Assert.Equal(2, entries.Count);
            Assert.Equal("A", entries[0].PackageId);
            Assert.Equal("1.0.1", entries[0].LatestStableVersion?.ToNormalizedString());
            Assert.Equal("B", entries[1].PackageId);
        }

        [Fact]
        public async Task RecordAsync_CapsAtFiftyEntries()
        {
            var svc = CreateService();
            for (var i = 0; i < 60; i++)
            {
                await svc.RecordAsync(new PackageModel
                {
                    PackageId = $"Pkg{i}",
                    InstalledVersion = NuGetVersion.Parse("1.0.0"),
                }, CancellationToken.None);
            }

            var entries = await svc.GetRecentAsync(CancellationToken.None);
            Assert.Equal(50, entries.Count);
            Assert.Equal("Pkg59", entries[0].PackageId);
        }

        [Fact]
        public async Task RecordAsync_IgnoresEmptyPackageId()
        {
            var svc = CreateService();
            await svc.RecordAsync(new PackageModel { PackageId = "" }, CancellationToken.None);

            var entries = await svc.GetRecentAsync(CancellationToken.None);
            Assert.Empty(entries);
        }

        [Fact]
        public async Task RecordAsync_IgnoresNullPackageId()
        {
            var svc = CreateService();
            await svc.RecordAsync(new PackageModel { PackageId = null! }, CancellationToken.None);

            var entries = await svc.GetRecentAsync(CancellationToken.None);
            Assert.Empty(entries);
        }
    }
}
