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
    public class MainViewModelTests
    {
        private static (MainViewModel vm, Mock<IProjectService> proj, Mock<INuGetFeedService> feed, Mock<IRestoreMonitorService> restore)
            CreateViewModel(
                IReadOnlyList<PackageSourceModel>? sources = null)
        {
            var projMock = new Mock<IProjectService>();
            var feedMock = new Mock<INuGetFeedService>();
            var restoreMock = new Mock<IRestoreMonitorService>();

            feedMock.Setup(f => f.GetSourcesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(sources ?? new List<PackageSourceModel>());

            projMock.Setup(p => p.GetInstalledPackagesAsync(It.IsAny<ProjectScopeModel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageModel>());

            restoreMock.Setup(r => r.StartMonitoring(It.IsAny<ProjectScopeModel>()));
            restoreMock.Setup(r => r.StopMonitoring());

            var vm = new MainViewModel(projMock.Object, feedMock.Object, restoreMock.Object);
            return (vm, projMock, feedMock, restoreMock);
        }

        [Fact]
        public async Task InitializeAsync_DoesNotSetCurrentProject()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);

            Assert.Null(vm.CurrentProject);
            Assert.False(vm.HasProject);
        }

        [Fact]
        public async Task SetCurrentProjectAsync_SetsCurrentProjectAndHasProject()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);

            await vm.SetCurrentProjectAsync(@"C:\MyApp\MyApp.csproj", "MyApp");

            Assert.NotNull(vm.CurrentProject);
            Assert.Equal("MyApp", vm.CurrentProject!.DisplayName);
            Assert.Equal(@"C:\MyApp\MyApp.csproj", vm.CurrentProject.ProjectFullPath);
            Assert.True(vm.HasProject);
        }

        [Fact]
        public async Task ClearCurrentProject_ResetsState()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);
            await vm.SetCurrentProjectAsync(@"C:\MyApp\MyApp.csproj", "MyApp");

            vm.ClearCurrentProject();

            Assert.Null(vm.CurrentProject);
            Assert.False(vm.HasProject);
            Assert.Empty(vm.Packages);
        }

        [Fact]
        public async Task InitializeAsync_PopulatesPackageSources()
        {
            var sources = new List<PackageSourceModel>
            {
                new() { Name = "nuget.org", Source = "https://api.nuget.org/v3/index.json", IsEnabled = true },
            };
            var (vm, _, _, _) = CreateViewModel(sources: sources);
            await vm.InitializeAsync(CancellationToken.None);

            Assert.Single(vm.PackageSources);
            Assert.Equal("nuget.org", vm.PackageSources[0].Name);
        }

        [Fact]
        public async Task InitializeAsync_IsLoadingFalseAfterCompletion()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);

            Assert.False(vm.IsLoading);
        }

        [Fact]
        public async Task SetCurrentProjectAsync_StartsRestoreMonitoring()
        {
            var (vm, _, _, restoreMock) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);
            await vm.SetCurrentProjectAsync(@"C:\MyApp\MyApp.csproj", "MyApp");

            restoreMock.Verify(r => r.StartMonitoring(It.IsAny<ProjectScopeModel>()), Times.Once);
        }

        [Fact]
        public void SearchText_DefaultIsEmpty()
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.Equal(string.Empty, vm.SearchText);
        }

        [Fact]
        public void FilterInstalled_DefaultIsFalse()
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.False(vm.FilterInstalled);
        }

        [Fact]
        public void FilterUpdates_DefaultIsFalse()
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.False(vm.FilterUpdates);
        }

        [Fact]
        public void FilterPrerelease_DefaultIsFalse()
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.False(vm.FilterPrerelease);
        }

        [Fact]
        public void ViewMode_DefaultIsBrowse()
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.Equal(PackageViewMode.Browse, vm.ViewMode);
        }

        [Fact]
        public void ViewMode_SetToInstalled_FlipsFilters()
        {
            var (vm, _, _, _) = CreateViewModel();
            vm.ViewMode = PackageViewMode.Installed;
            Assert.True(vm.FilterInstalled);
            Assert.False(vm.FilterUpdates);
            Assert.Equal(PackageViewMode.Installed, vm.ViewMode);
        }

        [Fact]
        public void ViewMode_SetToUpdates_FlipsBothFilters()
        {
            var (vm, _, _, _) = CreateViewModel();
            vm.ViewMode = PackageViewMode.Updates;
            Assert.True(vm.FilterInstalled);
            Assert.True(vm.FilterUpdates);
            Assert.Equal(PackageViewMode.Updates, vm.ViewMode);
        }

        [Fact]
        public async Task UpdatesView_ResolvesMetadataFromFeed_WhenNotCached()
        {
            // Regression test for issue #12: switching into the Updates view used to
            // rely on whatever "latest" metadata a prior background enrichment pass
            // happened to cache, so the list was inconsistent / frequently empty.
            // The Updates view must now resolve metadata from the feed itself.
            var installed = new List<PackageModel>
            {
                new() { PackageId = "PackageWithUpdate", InstalledVersion = NuGetVersion.Parse("1.0.0") },
                new() { PackageId = "PackageUpToDate", InstalledVersion = NuGetVersion.Parse("2.0.0") },
            };

            var projMock = new Mock<IProjectService>();
            var feedMock = new Mock<INuGetFeedService>();
            var restoreMock = new Mock<IRestoreMonitorService>();

            feedMock.Setup(f => f.GetSourcesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PackageSourceModel>());
            projMock.Setup(p => p.GetInstalledPackagesAsync(It.IsAny<ProjectScopeModel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(installed);

            // No cached "latest" metadata: the Updates view must hit the feed.
            PackageModel? cached = null;
            feedMock.Setup(f => f.TryGetCachedLatestMetadata(It.IsAny<string>(), out cached))
                .Returns(false);

            feedMock.Setup(f => f.GetPackageMetadataAsync("PackageWithUpdate", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "PackageWithUpdate", LatestStableVersion = NuGetVersion.Parse("2.0.0") });
            feedMock.Setup(f => f.GetPackageMetadataAsync("PackageUpToDate", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageModel { PackageId = "PackageUpToDate", LatestStableVersion = NuGetVersion.Parse("2.0.0") });

            var vm = new MainViewModel(projMock.Object, feedMock.Object, restoreMock.Object);
            await vm.InitializeAsync(CancellationToken.None);
            await vm.SetCurrentProjectAsync(@"C:\MyApp\MyApp.csproj", "MyApp");

            // Switching into the Updates view must resolve "latest" metadata for the
            // installed packages straight from the feed. Before the fix this only
            // happened on an explicit Refresh, so the list depended on whatever
            // background enrichment had cached and was frequently empty / wrong.
            vm.ViewMode = PackageViewMode.Updates;
            for (int i = 0; i < 200; i++)
            {
                var fetched = feedMock.Invocations.Count(x => x.Method.Name == "GetPackageMetadataAsync");
                if (!vm.IsLoading && fetched >= 2)
                    break;
                await Task.Delay(10);
            }

            feedMock.Verify(
                f => f.GetPackageMetadataAsync("PackageWithUpdate", It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
            feedMock.Verify(
                f => f.GetPackageMetadataAsync("PackageUpToDate", It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public void ViewMode_SetToBrowse_ClearsFilters()
        {
            var (vm, _, _, _) = CreateViewModel();
            vm.ViewMode = PackageViewMode.Updates;
            vm.ViewMode = PackageViewMode.Browse;
            Assert.False(vm.FilterInstalled);
            Assert.False(vm.FilterUpdates);
            Assert.Equal(PackageViewMode.Browse, vm.ViewMode);
        }

        [Fact]
        public void ViewMode_SetToVulnerable_FlipsInstalledAndVulnerableFilters()
        {
            var (vm, _, _, _) = CreateViewModel();
            vm.ViewMode = PackageViewMode.Vulnerable;
            Assert.True(vm.FilterInstalled);
            Assert.True(vm.FilterVulnerable);
            Assert.False(vm.FilterUpdates);
            Assert.Equal(PackageViewMode.Vulnerable, vm.ViewMode);
        }

        [Fact]
        public void ViewMode_SetFromVulnerableToBrowse_ClearsFilters()
        {
            var (vm, _, _, _) = CreateViewModel();
            vm.ViewMode = PackageViewMode.Vulnerable;
            vm.ViewMode = PackageViewMode.Browse;
            Assert.False(vm.FilterInstalled);
            Assert.False(vm.FilterVulnerable);
            Assert.False(vm.FilterUpdates);
            Assert.Equal(PackageViewMode.Browse, vm.ViewMode);
        }

        [Fact]
        public void ViewMode_SetToSameValue_DoesNotRaisePropertyChanged()
        {
            // Setting ViewMode to the value it already has must be a no-op so it
            // can't trigger a redundant reload or re-raise PropertyChanged.
            var (vm, _, _, _) = CreateViewModel();
            vm.ViewMode = PackageViewMode.Vulnerable;

            var raised = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ViewMode))
                    raised++;
            };

            vm.ViewMode = PackageViewMode.Vulnerable;

            Assert.Equal(0, raised);
            Assert.Equal(PackageViewMode.Vulnerable, vm.ViewMode);
        }

        [Fact]
        public async Task ClearCurrentProject_PreservesViewMode()
        {
            // Issue #18: the view-mode menu controller anchors its icon to the
            // command the user last clicked and can't be repositioned in code, so
            // clearing the project (e.g. on a solution switch) must keep the
            // selected mode. Otherwise the toolbar icon and the package list
            // disagree once the next solution loads.
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);
            await vm.SetCurrentProjectAsync(@"C:\MyApp\MyApp.csproj", "MyApp");
            vm.ViewMode = PackageViewMode.Vulnerable;

            vm.ClearCurrentProject();

            Assert.Equal(PackageViewMode.Vulnerable, vm.ViewMode);
            Assert.True(vm.FilterVulnerable);
        }

        [Fact]
        public void ExtractSourceFilter_NoToken_ReturnsNullSources()
        {
            var (clean, sources) = MainViewModel.ExtractSourceFilter("newtonsoft");
            Assert.Equal("newtonsoft", clean);
            Assert.Null(sources);
        }

        [Fact]
        public void ExtractSourceFilter_QuotedToken_StripsTokenAndReturnsSource()
        {
            var (clean, sources) = MainViewModel.ExtractSourceFilter("newtonsoft source:\"nuget.org\"");
            Assert.Equal("newtonsoft", clean);
            Assert.NotNull(sources);
            Assert.Single(sources);
            Assert.Equal("nuget.org", sources!.First());
        }

        [Fact]
        public void ExtractSourceFilter_MultipleTokens_ReturnsAllSources()
        {
            var (clean, sources) = MainViewModel.ExtractSourceFilter("source:\"nuget.org\" json source:internal");
            Assert.Equal("json", clean);
            Assert.NotNull(sources);
            Assert.Equal(2, sources!.Count);
            Assert.Contains("nuget.org", sources);
            Assert.Contains("internal", sources);
        }

        [Fact]
        public void StatusMessage_DefaultIsReady()
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.Equal("Ready", vm.StatusMessage);
        }

        [Fact]
        public void HasSelectedPackage_WhenNoSelection_ReturnsFalse()
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.False(vm.HasSelectedPackage);
        }

        [Fact]
        public async Task RefreshCommand_PreservesSearchText()
        {
            var (vm, _, _, _) = CreateViewModel();
            vm.SearchText = "something";
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("something", vm.SearchText);
        }

        [Fact]
        public void ToggleLogCommand_TogglesIsLogOpen()
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.False(vm.IsLogOpen);
            vm.ToggleLogCommand.Execute(null);
            Assert.True(vm.IsLogOpen);
            vm.ToggleLogCommand.Execute(null);
            Assert.False(vm.IsLogOpen);
        }

        [Fact]
        public async Task NavigateSearchHistory_NoHistory_DoesNothing()
        {
            var (vm, _, _, _) = CreateViewModel();
            vm.NavigateSearchHistory(-1); // Should not throw
            Assert.Equal(string.Empty, vm.SearchText);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var (vm, _, _, _) = CreateViewModel();
            var ex = Record.Exception(() => vm.Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public async Task SetSelectedPackages_Empty_ClearsSelection()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);
            vm.SetSelectedPackages(System.Array.Empty<PackageRowViewModel>());
            Assert.Null(vm.SelectedPackage);
            Assert.Null(vm.MultiSelection);
            Assert.False(vm.HasSelectedPackage);
        }

        [Fact]
        public async Task SetSelectedPackages_SingleRow_SetsSelectedPackage()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);
            var row = new PackageRowViewModel(new PackageModel { PackageId = "Pkg" });
            vm.SetSelectedPackages(new[] { row });
            Assert.Equal(row, vm.SelectedPackage);
            Assert.Null(vm.MultiSelection);
            Assert.True(vm.HasSelectedPackage);
        }

        [Fact]
        public async Task SetSelectedPackages_MultipleRows_SetsMultiSelection()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);
            var rows = new[]
            {
                new PackageRowViewModel(new PackageModel { PackageId = "Pkg1" }),
                new PackageRowViewModel(new PackageModel { PackageId = "Pkg2" }),
            };
            vm.SetSelectedPackages(rows);
            Assert.Null(vm.SelectedPackage);
            Assert.NotNull(vm.MultiSelection);
            Assert.Equal(2, vm.MultiSelection!.Count);
        }
    }
}
