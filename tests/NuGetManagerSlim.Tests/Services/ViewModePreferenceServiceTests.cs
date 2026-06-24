using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NuGetManagerSlim.Services;
using NuGetManagerSlim.ViewModels;
using Xunit;

namespace NuGetManagerSlim.Tests.Services
{
    public class ViewModePreferenceServiceTests : IDisposable
    {
        private readonly string _tempFile;

        public ViewModePreferenceServiceTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"viewmode-{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        }

        private ViewModePreferenceService CreateService() => new(_tempFile);

        [Fact]
        public async Task GetAsync_OnFreshFile_ReturnsNull()
        {
            var svc = CreateService();
            Assert.Null(await svc.GetAsync(CancellationToken.None));
        }

        [Fact]
        public async Task SaveAsync_PersistsAcrossInstances()
        {
            var svc1 = CreateService();
            await svc1.SaveAsync(PackageViewMode.Installed, CancellationToken.None);

            var svc2 = CreateService();
            Assert.Equal(PackageViewMode.Installed, await svc2.GetAsync(CancellationToken.None));
        }

        [Fact]
        public async Task SaveAsync_OverwritesPreviousValue()
        {
            var svc = CreateService();
            await svc.SaveAsync(PackageViewMode.Updates, CancellationToken.None);
            await svc.SaveAsync(PackageViewMode.Vulnerable, CancellationToken.None);

            Assert.Equal(PackageViewMode.Vulnerable, await svc.GetAsync(CancellationToken.None));
        }

        [Fact]
        public async Task GetAsync_OnCorruptFile_ReturnsNull()
        {
            File.WriteAllText(_tempFile, "{ not valid json");
            var svc = CreateService();
            Assert.Null(await svc.GetAsync(CancellationToken.None));
        }
    }
}
