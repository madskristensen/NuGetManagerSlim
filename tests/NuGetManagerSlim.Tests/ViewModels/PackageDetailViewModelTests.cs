using System.Collections.Generic;
using System.Linq;
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
    public class PackageDetailViewModelTests
    {
        private static (PackageDetailViewModel vm, Mock<INuGetFeedService> feed, Mock<IProjectService> proj, List<string> statusLog)
            CreateViewModel()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();
            var statusLog = new List<string>();

            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo> { new(NuGetVersion.Parse("2.0.0")), new(NuGetVersion.Parse("1.0.0")) });

            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel
                {
                    PackageId = "TestPkg",
                    Description = "A test package",
                    Authors = "Test Author",
                    LicenseExpression = "MIT",
                    DownloadCount = 1_500_000,
                    Dependencies = new List<PackageDependencyInfo>
                    {
                        new() { PackageId = "Dep1", VersionRange = "[1.0,)", TargetFramework = "net8.0" },
                    },
                });

            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, msg => statusLog.Add(msg));
            return (vm, feedMock, projMock, statusLog);
        }

        private static PackageRowViewModel MakeRow(
            string id = "TestPkg",
            string? installed = "1.0.0",
            string? latestStable = "2.0.0")
        {
            return new PackageRowViewModel(new PackageModel
            {
                PackageId = id,
                InstalledVersion = installed != null ? NuGetVersion.Parse(installed) : null,
                LatestStableVersion = latestStable != null ? NuGetVersion.Parse(latestStable) : null,
            });
        }

        [Fact]
        public async Task LoadAsync_SetsPackageId()
        {
            var (vm, _, _, _) = CreateViewModel();
            var row = MakeRow("Newtonsoft.Json");
            await vm.LoadAsync(row, new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.Equal("Newtonsoft.Json", vm.PackageId);
        }

        [Fact]
        public async Task LoadAsync_PopulatesAvailableVersions()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.Equal(2, vm.AvailableVersions.Count);
            Assert.Equal(NuGetVersion.Parse("2.0.0"), vm.AvailableVersions[0].Version);
        }

        [Fact]
        public async Task LoadAsync_SetsSelectedVersionToInstalled()
        {
            var (vm, _, _, _) = CreateViewModel();
            var row = MakeRow(installed: "1.0.0");
            await vm.LoadAsync(row, new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.Equal(NuGetVersion.Parse("1.0.0"), vm.SelectedVersion);
        }

        [Fact]
        public async Task LoadAsync_FlagsDeprecatedAndVulnerableVersions()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();
            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo>
                {
                    new(NuGetVersion.Parse("2.0.0"), isVulnerable: true),
                    new(NuGetVersion.Parse("1.0.0"), isDeprecated: true, deprecationReason: "Legacy"),
                });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "TestPkg" });
            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });

            await vm.LoadAsync(MakeRow(installed: null), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            var v200 = vm.AvailableVersions.Single(i => i.Version == NuGetVersion.Parse("2.0.0"));
            var v100 = vm.AvailableVersions.Single(i => i.Version == NuGetVersion.Parse("1.0.0"));
            Assert.True(v200.IsVulnerable);
            Assert.False(v200.IsDeprecated);
            Assert.True(v100.IsDeprecated);
            Assert.False(v100.IsVulnerable);
            Assert.Equal("This version is deprecated: Legacy", v100.DeprecationTooltip);
        }

        [Fact]
        public async Task LoadAsync_SurfacesDeprecationCalloutForSelectedVersion()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();
            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo>
                {
                    new(NuGetVersion.Parse("1.0.0")),
                });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel
                {
                    PackageId = "TestPkg",
                    IsDeprecated = true,
                    DeprecationReason = "Use NewPkg instead",
                });
            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });

            await vm.LoadAsync(MakeRow(installed: null), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.True(vm.IsDeprecated);
            Assert.True(vm.HasDeprecationReason);
            Assert.Equal("Use NewPkg instead", vm.DeprecationReason);
        }

        [Fact]
        public async Task LoadAsync_NonDeprecatedVersion_HasNoDeprecationCallout()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();
            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo>
                {
                    new(NuGetVersion.Parse("1.0.0")),
                });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "TestPkg" });
            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });

            await vm.LoadAsync(MakeRow(installed: null), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.False(vm.IsDeprecated);
            Assert.False(vm.HasDeprecationReason);
            Assert.Equal(string.Empty, vm.DeprecationReason);
        }

        [Fact]
        public async Task LoadAsync_SetsDescription()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.Equal("A test package", vm.Description);
        }

        [Fact]
        public async Task LoadAsync_SetsAuthors()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.Equal("Test Author", vm.Authors);
        }

        [Fact]
        public async Task LoadAsync_SetsLicense()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.Equal("MIT", vm.License);
        }

        [Fact]
        public async Task LoadAsync_FormatsDownloadCount()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.Equal("1.5M", vm.DownloadCountDisplay);
        }

        [Fact]
        public async Task LoadAsync_PopulatesDependencies()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.Single(vm.Dependencies);
            Assert.Equal("Dep1", vm.Dependencies[0].PackageId);
        }

        [Fact]
        public async Task LoadAsync_InstalledPackage_CanUninstall()
        {
            var (vm, _, _, _) = CreateViewModel();
            var row = MakeRow(installed: "1.0.0");
            await vm.LoadAsync(row, new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.True(vm.CanUninstall);
        }

        [Fact]
        public async Task LoadAsync_NotInstalled_CanInstall()
        {
            var (vm, _, _, _) = CreateViewModel();
            var row = MakeRow(installed: null);
            await vm.LoadAsync(row, new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.True(vm.CanInstall);
            Assert.False(vm.CanUninstall);
        }

        [Fact]
        public async Task LoadAsync_HasUpdate_CanUpdate()
        {
            var (vm, _, _, _) = CreateViewModel();
            var row = MakeRow(installed: "1.0.0", latestStable: "2.0.0");
            await vm.LoadAsync(row, new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.True(vm.CanUpdate);
        }

        [Fact]
        public async Task LoadAsync_AnyScope_CannotUpdateAllProjects()
        {
            var (vm, _, _, _) = CreateViewModel();
            var row = MakeRow(installed: "1.0.0", latestStable: "2.0.0");
            await vm.LoadAsync(row, new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.False(vm.CanUpdateAllProjects);
        }

        [Fact]
        public async Task LoadAsync_SingleProjectScope_CannotUpdateAllProjects()
        {
            var (vm, _, _, _) = CreateViewModel();
            var row = MakeRow(installed: "1.0.0", latestStable: "2.0.0");
            var scope = new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" };
            await vm.LoadAsync(row, scope, false, CancellationToken.None);
            Assert.False(vm.CanUpdateAllProjects);
        }

        [Fact]
        public async Task LoadAsync_TransitivePackage_CannotUninstall()
        {
            var (vm, _, _, _) = CreateViewModel();
            var row = new PackageRowViewModel(new PackageModel
            {
                PackageId = "TestPkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
                IsTransitive = true,
            });
            await vm.LoadAsync(row, new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);
            Assert.False(vm.CanUninstall);
        }

        [Fact]
        public async Task LoadAsync_DownloadCountZero_DisplaysNA()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();

            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo> { new(NuGetVersion.Parse("1.0.0")) });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "TestPkg", DownloadCount = 0 });

            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.Equal("N/A", vm.DownloadCountDisplay);
        }

        [Fact]
        public async Task LoadAsync_DownloadCountBillions_FormatsBSuffix()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();

            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo> { new(NuGetVersion.Parse("1.0.0")) });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "TestPkg", DownloadCount = 2_000_000_000 });

            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.Equal("2.0B", vm.DownloadCountDisplay);
        }

        [Fact]
        public async Task LoadAsync_DownloadCountThousands_FormatsKSuffix()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();

            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo> { new(NuGetVersion.Parse("1.0.0")) });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "TestPkg", DownloadCount = 7_500 });

            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.Equal("7.5K", vm.DownloadCountDisplay);
        }

        [Fact]
        public async Task LoadAsync_PopulatesDependencyGroups()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();

            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo> { new(NuGetVersion.Parse("1.0.0")) });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel
                {
                    PackageId = "TestPkg",
                    DownloadCount = 1,
                    Dependencies = new List<PackageDependencyInfo>
                    {
                        new() { PackageId = "A", VersionRange = "[1,)", TargetFramework = "net8.0" },
                        new() { PackageId = "B", VersionRange = "[2,)", TargetFramework = "net48" },
                    },
                });

            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.Equal(2, vm.DependencyGroups.Count);
            Assert.Contains(vm.DependencyGroups, g => g.TargetFramework == "net48");
            Assert.Contains(vm.DependencyGroups, g => g.TargetFramework == "net8.0");
        }

        [Fact]
        public async Task LoadAsync_WithPublishedDate_SetsHasPublished()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();
            var published = new System.DateTimeOffset(2023, 6, 15, 0, 0, 0, System.TimeSpan.Zero);

            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo> { new(NuGetVersion.Parse("1.0.0")) });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "TestPkg", DownloadCount = 1, Published = published });

            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.True(vm.HasPublished);
            Assert.False(string.IsNullOrEmpty(vm.PublishedDisplay));
        }

        [Fact]
        public async Task LoadAsync_WithProjectUrl_SetsHasProjectUrl()
        {
            var feedMock = new Mock<INuGetFeedService>();
            var projMock = new Mock<IProjectService>();

            feedMock.Setup(f => f.GetVersionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageVersionInfo> { new(NuGetVersion.Parse("1.0.0")) });
            feedMock.Setup(f => f.GetPackageMetadataAsync(It.IsAny<string>(), It.IsAny<NuGetVersion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "TestPkg", DownloadCount = 1, ProjectUrl = "https://example.com" });

            var vm = new PackageDetailViewModel(feedMock.Object, projMock.Object, _ => { });
            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.True(vm.HasProjectUrl);
            Assert.Equal("https://example.com", vm.ProjectUrl);
        }

        [Fact]
        public async Task LoadAsync_PopulatesProjectMemberships_OnlyForReferencingProjects()
        {
            var (vm, _, projMock, _) = CreateViewModel();
            projMock.Setup(p => p.GetInstalledVersionsPerProjectAsync(
                    It.IsAny<ProjectScopeModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, NuGetVersion?>
                {
                    [@"C:\sln\A\A.csproj"] = NuGetVersion.Parse("1.0.0"),
                    [@"C:\sln\B\B.csproj"] = NuGetVersion.Parse("2.0.0"),
                    [@"C:\sln\C\C.csproj"] = null,
                });

            var scope = new ProjectScopeModel
            {
                DisplayName = "MySolution",
                ScopeKind = ProjectScopeKind.Solution,
                ProjectFullPaths = new[] { @"C:\sln\A\A.csproj", @"C:\sln\B\B.csproj", @"C:\sln\C\C.csproj" },
            };
            await vm.LoadAsync(MakeRow(), scope, false, CancellationToken.None);

            Assert.True(vm.HasProjectMemberships);
            Assert.Equal(2, vm.ProjectMemberships.Count);
            Assert.DoesNotContain(vm.ProjectMemberships, m => m.DisplayText == "C");

            var a = vm.ProjectMemberships.Single(m => m.DisplayText == "A");
            Assert.Equal(@"C:\sln\A\A.csproj", a.ProjectFullPath);
            Assert.Equal("1.0.0", a.InstalledVersion);
        }

        [Fact]
        public async Task LoadAsync_NoReferencingProjects_LeavesMembershipsEmpty()
        {
            var (vm, _, projMock, _) = CreateViewModel();
            projMock.Setup(p => p.GetInstalledVersionsPerProjectAsync(
                    It.IsAny<ProjectScopeModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, NuGetVersion?>
                {
                    [@"C:\sln\A\A.csproj"] = null,
                });

            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.False(vm.HasProjectMemberships);
            Assert.Empty(vm.ProjectMemberships);
        }

        [Fact]
        public async Task LoadAsync_OrdersProjectMembershipsByName()
        {
            var (vm, _, projMock, _) = CreateViewModel();
            projMock.Setup(p => p.GetInstalledVersionsPerProjectAsync(
                    It.IsAny<ProjectScopeModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, NuGetVersion?>
                {
                    [@"C:\sln\Zebra\Zebra.csproj"] = NuGetVersion.Parse("1.0.0"),
                    [@"C:\sln\Alpha\Alpha.csproj"] = NuGetVersion.Parse("1.0.0"),
                });

            await vm.LoadAsync(MakeRow(), new ProjectScopeModel { DisplayName = "MyApp", ProjectFullPath = @"C:\x\x.csproj" }, false, CancellationToken.None);

            Assert.Equal(new[] { "Alpha", "Zebra" }, vm.ProjectMemberships.Select(m => m.DisplayText).ToArray());
        }
    }
}
