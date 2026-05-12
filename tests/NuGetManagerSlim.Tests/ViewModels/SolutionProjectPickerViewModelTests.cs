using System.Collections.Generic;
using System.Linq;
using NuGet.Versioning;
using NuGetManagerSlim.ViewModels;
using Xunit;

namespace NuGetManagerSlim.Tests.ViewModels
{
    public class SolutionProjectPickerViewModelTests
    {
        private static IReadOnlyDictionary<string, NuGetVersion?> NoVersions(IEnumerable<string> paths)
            => paths.ToDictionary(p => p, _ => (NuGetVersion?)null);

        private static IReadOnlyDictionary<string, NuGetVersion?> WithVersion(string path, string version)
            => new Dictionary<string, NuGetVersion?> { [path] = NuGetVersion.Parse(version) };

        // ---- BuildRows ----

        [Fact]
        public void BuildRows_Install_SelectsProjectsWithoutPackage()
        {
            var paths = new[] { @"C:\A\A.csproj", @"C:\B\B.csproj" };
            var versions = new Dictionary<string, NuGetVersion?> { [@"C:\A\A.csproj"] = NuGetVersion.Parse("1.0.0") };

            var rows = SolutionProjectPickerViewModel.BuildRows(SolutionPackageAction.Install, paths, versions);

            Assert.False(rows.Single(r => r.ProjectFullPath == @"C:\A\A.csproj").IsSelected);
            Assert.True(rows.Single(r => r.ProjectFullPath == @"C:\B\B.csproj").IsSelected);
        }

        [Fact]
        public void BuildRows_Update_SelectsProjectsWithPackageInstalled()
        {
            var paths = new[] { @"C:\A\A.csproj", @"C:\B\B.csproj" };
            var versions = new Dictionary<string, NuGetVersion?> { [@"C:\A\A.csproj"] = NuGetVersion.Parse("1.0.0") };

            var rows = SolutionProjectPickerViewModel.BuildRows(SolutionPackageAction.Update, paths, versions);

            Assert.True(rows.Single(r => r.ProjectFullPath == @"C:\A\A.csproj").IsSelected);
            Assert.False(rows.Single(r => r.ProjectFullPath == @"C:\B\B.csproj").IsSelected);
        }

        [Fact]
        public void BuildRows_Uninstall_SelectsProjectsWithPackageInstalled()
        {
            var paths = new[] { @"C:\A\A.csproj", @"C:\B\B.csproj" };
            var versions = new Dictionary<string, NuGetVersion?> { [@"C:\B\B.csproj"] = NuGetVersion.Parse("2.0.0") };

            var rows = SolutionProjectPickerViewModel.BuildRows(SolutionPackageAction.Uninstall, paths, versions);

            Assert.False(rows.Single(r => r.ProjectFullPath == @"C:\A\A.csproj").IsSelected);
            Assert.True(rows.Single(r => r.ProjectFullPath == @"C:\B\B.csproj").IsSelected);
        }

        [Fact]
        public void BuildRows_DisplayName_IsFileNameWithoutExtension()
        {
            var paths = new[] { @"C:\Repos\MyApp\MyApp.csproj" };
            var rows = SolutionProjectPickerViewModel.BuildRows(SolutionPackageAction.Install, paths, NoVersions(paths));
            Assert.Equal("MyApp", rows[0].DisplayName);
        }

        // ---- Constructor / Title / ActionLabel ----

        [Fact]
        public void Constructor_Install_SetsExpectedTitleAndLabel()
        {
            var vm = new SolutionProjectPickerViewModel(
                SolutionPackageAction.Install, "Serilog", NuGetVersion.Parse("3.0.0"),
                System.Array.Empty<SolutionProjectSelection>());

            Assert.Contains("Install", vm.Title);
            Assert.Contains("Serilog", vm.Title);
            Assert.Equal("Install", vm.ActionLabel);
        }

        [Fact]
        public void Constructor_Update_SetsExpectedTitleAndLabel()
        {
            var vm = new SolutionProjectPickerViewModel(
                SolutionPackageAction.Update, "Serilog", NuGetVersion.Parse("3.1.0"),
                System.Array.Empty<SolutionProjectSelection>());

            Assert.Contains("Update", vm.Title);
            Assert.Equal("Update", vm.ActionLabel);
        }

        [Fact]
        public void Constructor_Uninstall_SetsExpectedTitleAndLabel()
        {
            var vm = new SolutionProjectPickerViewModel(
                SolutionPackageAction.Uninstall, "Serilog", null,
                System.Array.Empty<SolutionProjectSelection>());

            Assert.Contains("Uninstall", vm.Title);
            Assert.Equal("Uninstall", vm.ActionLabel);
        }

        // ---- CanApply ----

        [Fact]
        public void CanApply_FalseWhenNothingSelected()
        {
            var project = new SolutionProjectSelection
            {
                ProjectFullPath = @"C:\App\App.csproj",
                DisplayName = "App",
                IsSelected = false,
            };
            var vm = new SolutionProjectPickerViewModel(
                SolutionPackageAction.Install, "Pkg", NuGetVersion.Parse("1.0.0"),
                new[] { project });

            Assert.False(vm.CanApply);
        }

        [Fact]
        public void CanApply_TrueWhenAtLeastOneSelected()
        {
            var project = new SolutionProjectSelection
            {
                ProjectFullPath = @"C:\App\App.csproj",
                DisplayName = "App",
                IsSelected = true,
            };
            var vm = new SolutionProjectPickerViewModel(
                SolutionPackageAction.Install, "Pkg", NuGetVersion.Parse("1.0.0"),
                new[] { project });

            Assert.True(vm.CanApply);
        }

        [Fact]
        public void CanApply_UpdatesWhenSelectionChanges()
        {
            var project = new SolutionProjectSelection
            {
                ProjectFullPath = @"C:\App\App.csproj",
                DisplayName = "App",
                IsSelected = false,
            };
            var vm = new SolutionProjectPickerViewModel(
                SolutionPackageAction.Install, "Pkg", NuGetVersion.Parse("1.0.0"),
                new[] { project });

            Assert.False(vm.CanApply);
            project.IsSelected = true;
            Assert.True(vm.CanApply);
        }

        // ---- SelectedProjects ----

        [Fact]
        public void SelectedProjects_ReturnsOnlySelectedRows()
        {
            var a = new SolutionProjectSelection { ProjectFullPath = @"C:\A\A.csproj", DisplayName = "A", IsSelected = true };
            var b = new SolutionProjectSelection { ProjectFullPath = @"C:\B\B.csproj", DisplayName = "B", IsSelected = false };
            var vm = new SolutionProjectPickerViewModel(
                SolutionPackageAction.Install, "Pkg", NuGetVersion.Parse("1.0.0"),
                new[] { a, b });

            var selected = vm.SelectedProjects;

            Assert.Single(selected);
            Assert.Equal(@"C:\A\A.csproj", selected[0].ProjectFullPath);
        }

        // ---- SolutionProjectSelection helpers ----

        [Fact]
        public void SolutionProjectSelection_IsInstalled_TrueWhenVersionSet()
        {
            var sel = new SolutionProjectSelection { CurrentVersion = NuGetVersion.Parse("1.0.0") };
            Assert.True(sel.IsInstalled);
        }

        [Fact]
        public void SolutionProjectSelection_IsInstalled_FalseWhenVersionNull()
        {
            var sel = new SolutionProjectSelection();
            Assert.False(sel.IsInstalled);
        }

        [Fact]
        public void SolutionProjectSelection_CurrentVersionDisplay_ReturnsVersionString()
        {
            var sel = new SolutionProjectSelection { CurrentVersion = NuGetVersion.Parse("2.3.4") };
            Assert.Equal("2.3.4", sel.CurrentVersionDisplay);
        }

        [Fact]
        public void SolutionProjectSelection_CurrentVersionDisplay_EmptyWhenNull()
        {
            var sel = new SolutionProjectSelection();
            Assert.Equal(string.Empty, sel.CurrentVersionDisplay);
        }
    }
}
