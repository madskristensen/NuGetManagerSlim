using System.Collections.Generic;
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
                IReadOnlyList<ProjectScopeModel>? projects = null,
                IReadOnlyList<PackageSourceModel>? sources = null)
        {
            var projMock = new Mock<IProjectService>();
            var feedMock = new Mock<INuGetFeedService>();
            var restoreMock = new Mock<IRestoreMonitorService>();

            projMock.Setup(p => p.GetProjectsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(projects ?? new List<ProjectScopeModel> { ProjectScopeModel.EntireSolution });

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
        public async Task InitializeAsync_PopulatesProjectScopes()
        {
            var projects = new List<ProjectScopeModel>
            {
                ProjectScopeModel.EntireSolution,
                new() { DisplayName = "MyApp", ProjectFullPath = @"C:\MyApp\MyApp.csproj" },
            };
            var (vm, _, _, _) = CreateViewModel(projects: projects);

            await vm.InitializeAsync(CancellationToken.None);

            Assert.Equal(2, vm.ProjectScopes.Count);
            Assert.Equal("Entire Solution", vm.ProjectScopes[0].DisplayName);
            Assert.Equal("MyApp", vm.ProjectScopes[1].DisplayName);
        }

        [Fact]
        public async Task InitializeAsync_SetsSelectedScopeToFirstProject()
        {
            var (vm, _, _, _) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);

            Assert.NotNull(vm.SelectedScope);
            Assert.Equal("Entire Solution", vm.SelectedScope.DisplayName);
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
        public async Task InitializeAsync_StartsRestoreMonitoring()
        {
            var (vm, _, _, restoreMock) = CreateViewModel();
            await vm.InitializeAsync(CancellationToken.None);

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
        public async Task RefreshCommand_ResetsSearchText()
        {
            var (vm, _, _, _) = CreateViewModel();
            vm.SearchText = "something";
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(string.Empty, vm.SearchText);
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
        public async Task InitializeAsync_NoProjects_HasEntireSolutionScope()
        {
            var (vm, projMock, _, _) = CreateViewModel();
            projMock.Setup(p => p.GetProjectsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ProjectScopeModel> { ProjectScopeModel.EntireSolution });
            await vm.InitializeAsync(CancellationToken.None);

            Assert.Single(vm.ProjectScopes);
            Assert.True(vm.ProjectScopes[0].IsEntireSolution);
        }
    }
}
