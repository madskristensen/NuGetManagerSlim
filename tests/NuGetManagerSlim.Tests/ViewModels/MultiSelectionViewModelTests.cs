using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NuGet.Versioning;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.Services;
using NuGetManagerSlim.ViewModels;
using Xunit;

namespace NuGetManagerSlim.Tests.ViewModels
{
    public class MultiSelectionViewModelTests
    {
        private static PackageRowViewModel MakeRow(
            string id,
            string? installed = null,
            string? latestStable = null,
            bool isTransitive = false)
        {
            return new PackageRowViewModel(new PackageModel
            {
                PackageId = id,
                InstalledVersion = installed != null ? NuGetVersion.Parse(installed) : null,
                LatestStableVersion = latestStable != null ? NuGetVersion.Parse(latestStable) : null,
                IsTransitive = isTransitive,
            });
        }

        private static MultiSelectionViewModel Create(
            IReadOnlyList<PackageRowViewModel> packages,
            string? projectPath = @"C:\App\App.csproj")
        {
            var proj = new Mock<IProjectService>();
            var scope = projectPath != null
                ? new ProjectScopeModel { DisplayName = "App", ProjectFullPath = projectPath }
                : null;
            return new MultiSelectionViewModel(packages, scope, proj.Object, _ => { }, null);
        }

        [Fact]
        public void Title_ReflectsCount()
        {
            var vm = Create(new[]
            {
                MakeRow("A", installed: "1.0.0"),
                MakeRow("B", installed: "2.0.0"),
            });
            Assert.Equal("2 packages selected", vm.Title);
        }

        [Fact]
        public void InstallCount_CountsNotInstalledRows()
        {
            var vm = Create(new[]
            {
                MakeRow("A"),               // not installed
                MakeRow("B", installed: "1.0.0"),
                MakeRow("C"),               // not installed
            });
            Assert.Equal(2, vm.InstallCount);
        }

        [Fact]
        public void UpdateCount_CountsRowsWithAvailableUpdate()
        {
            var vm = Create(new[]
            {
                MakeRow("A", installed: "1.0.0", latestStable: "2.0.0"),
                MakeRow("B", installed: "1.0.0", latestStable: "1.0.0"),
            });
            Assert.Equal(1, vm.UpdateCount);
        }

        [Fact]
        public void UninstallCount_ExcludesTransitivePackages()
        {
            var vm = Create(new[]
            {
                MakeRow("A", installed: "1.0.0"),
                MakeRow("B", installed: "1.0.0", isTransitive: true),
            });
            Assert.Equal(1, vm.UninstallCount);
        }

        [Fact]
        public void CanInstall_TrueWhenInstallCountGreaterThanZeroAndProjectSet()
        {
            var vm = Create(new[] { MakeRow("A") });
            Assert.True(vm.CanInstall);
        }

        [Fact]
        public void CanInstall_FalseWhenNoProjectPath()
        {
            var vm = Create(new[] { MakeRow("A") }, projectPath: null);
            Assert.False(vm.CanInstall);
        }

        [Fact]
        public void CanUpdate_TrueWhenUpdateCountGreaterThanZeroAndProjectSet()
        {
            var vm = Create(new[] { MakeRow("A", installed: "1.0.0", latestStable: "2.0.0") });
            Assert.True(vm.CanUpdate);
        }

        [Fact]
        public void CanUninstall_TrueWhenUninstallCountGreaterThanZeroAndProjectSet()
        {
            var vm = Create(new[] { MakeRow("A", installed: "1.0.0") });
            Assert.True(vm.CanUninstall);
        }

        [Fact]
        public void InstallButtonText_IncludesCount()
        {
            var vm = Create(new[] { MakeRow("A"), MakeRow("B") });
            Assert.Equal("Install (2)", vm.InstallButtonText);
        }

        [Fact]
        public void UpdateButtonText_IncludesCount()
        {
            var vm = Create(new[]
            {
                MakeRow("A", installed: "1.0.0", latestStable: "2.0.0"),
                MakeRow("B", installed: "1.0.0", latestStable: "2.0.0"),
            });
            Assert.Equal("Update (2)", vm.UpdateButtonText);
        }

        [Fact]
        public void UninstallButtonText_IncludesCount()
        {
            var vm = Create(new[] { MakeRow("A", installed: "1.0.0") });
            Assert.Equal("Uninstall (1)", vm.UninstallButtonText);
        }

        [Fact]
        public void PackageIds_ContainsAllIds()
        {
            var vm = Create(new[]
            {
                MakeRow("Pkg1"),
                MakeRow("Pkg2"),
            });
            Assert.Equal(2, vm.PackageIds.Count);
            Assert.Contains("Pkg1", vm.PackageIds);
            Assert.Contains("Pkg2", vm.PackageIds);
        }

        [Fact]
        public async Task InstallAsync_CallsProjectServiceForNotInstalledRows()
        {
            var proj = new Mock<IProjectService>();
            var rows = new[]
            {
                MakeRow("A", latestStable: "1.0.0"),
                MakeRow("B", installed: "1.0.0"),
            };
            var scope = new ProjectScopeModel { DisplayName = "App", ProjectFullPath = @"C:\App\App.csproj" };
            var vm = new MultiSelectionViewModel(rows, scope, proj.Object, _ => { }, null);

            await vm.InstallCommand.ExecuteAsync(null);

            proj.Verify(p => p.InstallPackageAsync(@"C:\App\App.csproj", "A", It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()), Times.Once);
            proj.Verify(p => p.InstallPackageAsync(It.IsAny<string>(), "B", It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task UpdateAsync_CallsProjectServiceForRowsWithUpdates()
        {
            var proj = new Mock<IProjectService>();
            var rows = new[]
            {
                MakeRow("A", installed: "1.0.0", latestStable: "2.0.0"),
                MakeRow("B", installed: "1.0.0", latestStable: "1.0.0"),
            };
            var scope = new ProjectScopeModel { DisplayName = "App", ProjectFullPath = @"C:\App\App.csproj" };
            var vm = new MultiSelectionViewModel(rows, scope, proj.Object, _ => { }, null);

            await vm.UpdateCommand.ExecuteAsync(null);

            proj.Verify(p => p.UpdatePackageAsync(@"C:\App\App.csproj", "A", NuGetVersion.Parse("2.0.0"), It.IsAny<CancellationToken>()), Times.Once);
            proj.Verify(p => p.UpdatePackageAsync(It.IsAny<string>(), "B", It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task UninstallAsync_CallsProjectServiceForInstalledNonTransitiveRows()
        {
            var proj = new Mock<IProjectService>();
            var rows = new[]
            {
                MakeRow("A", installed: "1.0.0"),
                MakeRow("B", installed: "1.0.0", isTransitive: true),
            };
            var scope = new ProjectScopeModel { DisplayName = "App", ProjectFullPath = @"C:\App\App.csproj" };
            var vm = new MultiSelectionViewModel(rows, scope, proj.Object, _ => { }, null);

            await vm.UninstallCommand.ExecuteAsync(null);

            proj.Verify(p => p.UninstallPackageAsync(@"C:\App\App.csproj", "A", It.IsAny<CancellationToken>()), Times.Once);
            proj.Verify(p => p.UninstallPackageAsync(It.IsAny<string>(), "B", It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
