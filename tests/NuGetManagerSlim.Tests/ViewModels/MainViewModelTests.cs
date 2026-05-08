using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
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
        public async Task SetSolutionScopeAsync_SetsReadOnlyScope()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);

            await vm.SetSolutionScopeAsync("MySolution", new[] { @"C:\App\A.csproj", @"C:\App\B.csproj" });

            Assert.NotNull(vm.CurrentProject);
            Assert.True(vm.CurrentProject!.IsSolutionScope);
            Assert.Equal("MySolution", vm.CurrentProject.DisplayName);
            Assert.Null(vm.CurrentProject.ProjectFullPath);
            Assert.Equal(2, vm.CurrentProject.ProjectFullPaths.Count);
            Assert.True(vm.HasProject);
            Assert.True(vm.IsReadOnlyScope);
        }

        [Fact]
        public async Task SetSolutionScopeAsync_DoesNotStartRestoreMonitoring()
        {
            var (vm, _, _, restoreMock) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);

            await vm.SetSolutionScopeAsync("MySolution", new[] { @"C:\App\A.csproj" });

            restoreMock.Verify(r => r.StartMonitoring(It.IsAny<ProjectScopeModel>()), Times.Never);
        }

        [Fact]
        public async Task SetSolutionScopeAsync_WithNoProjects_ClearsScope()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);
            await vm.SetCurrentProjectAsync(@"C:\MyApp\MyApp.csproj", "MyApp");

            await vm.SetSolutionScopeAsync("MySolution", System.Array.Empty<string>());

            Assert.Null(vm.CurrentProject);
            Assert.False(vm.IsReadOnlyScope);
        }

        [Fact]
        public async Task SetCurrentProjectAsync_AfterSetSolutionScopeAsync_ExitsReadOnly()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);
            await vm.SetSolutionScopeAsync("MySolution", new[] { @"C:\App\A.csproj" });
            Assert.True(vm.IsReadOnlyScope);

            await vm.SetCurrentProjectAsync(@"C:\MyApp\MyApp.csproj", "MyApp");

            Assert.False(vm.IsReadOnlyScope);
            Assert.Equal(@"C:\MyApp\MyApp.csproj", vm.CurrentProject!.ProjectFullPath);
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
    }
}
