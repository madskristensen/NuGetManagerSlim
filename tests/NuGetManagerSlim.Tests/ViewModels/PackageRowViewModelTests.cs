using NuGet.Versioning;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.ViewModels;
using Xunit;

namespace NuGetManagerSlim.Tests.ViewModels
{
    public class PackageRowViewModelTests
    {
        private static PackageRowViewModel MakeRow(
            string id = "TestPkg",
            string? installed = null,
            string? latestStable = null,
            string? latestPre = null,
            bool isTransitive = false,
            string? requiredBy = null,
            string? source = "nuget.org")
        {
            return new PackageRowViewModel(new PackageModel
            {
                PackageId = id,
                InstalledVersion = installed != null ? NuGetVersion.Parse(installed) : null,
                LatestStableVersion = latestStable != null ? NuGetVersion.Parse(latestStable) : null,
                LatestPrereleaseVersion = latestPre != null ? NuGetVersion.Parse(latestPre) : null,
                IsTransitive = isTransitive,
                RequiredByPackageId = requiredBy,
                SourceName = source,
            });
        }

        [Fact]
        public void IsInstalled_WhenInstalledVersionSet_ReturnsTrue()
        {
            var vm = MakeRow(installed: "1.0.0");
            Assert.True(vm.IsInstalled);
        }

        [Fact]
        public void IsInstalled_WhenNoInstalledVersion_ReturnsFalse()
        {
            var vm = MakeRow(installed: null);
            Assert.False(vm.IsInstalled);
        }

        [Fact]
        public void HasUpdate_WhenLatestIsHigher_ReturnsTrue()
        {
            var vm = MakeRow(installed: "1.0.0", latestStable: "2.0.0");
            Assert.True(vm.HasUpdate);
        }

        [Fact]
        public void HasUpdate_WhenLatestIsEqual_ReturnsFalse()
        {
            var vm = MakeRow(installed: "2.0.0", latestStable: "2.0.0");
            Assert.False(vm.HasUpdate);
        }

        [Fact]
        public void HasUpdate_WhenNotInstalled_ReturnsFalse()
        {
            var vm = MakeRow(installed: null, latestStable: "2.0.0");
            Assert.False(vm.HasUpdate);
        }

        [Fact]
        public void HasUpdate_WhenNoLatestStable_ReturnsFalse()
        {
            var vm = MakeRow(installed: "1.0.0", latestStable: null);
            Assert.False(vm.HasUpdate);
        }

        [Fact]
        public void UpdateBadge_WhenHasUpdate_ShowsArrowAndVersion()
        {
            var vm = MakeRow(installed: "1.0.0", latestStable: "2.0.0");
            Assert.Equal("→ 2.0.0", vm.UpdateBadge);
        }

        [Fact]
        public void UpdateBadge_WhenNoUpdate_IsEmpty()
        {
            var vm = MakeRow(installed: "2.0.0", latestStable: "2.0.0");
            Assert.Equal(string.Empty, vm.UpdateBadge);
        }

        [Fact]
        public void SourceBadge_ShowsFeedName()
        {
            var vm = MakeRow(source: "myFeed");
            Assert.Equal("⊕ myFeed", vm.SourceBadge);
        }

        [Fact]
        public void SourceBadge_WhenNoSource_IsEmpty()
        {
            var vm = MakeRow(source: null);
            Assert.Equal(string.Empty, vm.SourceBadge);
        }

        [Fact]
        public void IsTransitive_WhenSet_ReturnsTrue()
        {
            var vm = MakeRow(isTransitive: true, requiredBy: "ParentPkg");
            Assert.True(vm.IsTransitive);
            Assert.Equal("required by: ParentPkg", vm.RequiredByDisplay);
        }

        [Fact]
        public void GroupKey_DirectPackage_IsPackages()
        {
            var vm = MakeRow(isTransitive: false);
            Assert.Equal("Packages", vm.GroupKey);
        }

        [Fact]
        public void GroupKey_InstalledDirectPackage_IsInstalled()
        {
            var vm = MakeRow(isTransitive: false, installed: "1.0.0");
            Assert.Equal("Installed", vm.GroupKey);
        }

        [Fact]
        public void GroupKey_TransitivePackage_IsTransitivePackages()
        {
            var vm = MakeRow(isTransitive: true);
            Assert.Equal("Transitive packages", vm.GroupKey);
        }

        [Fact]
        public void IsPrerelease_WhenOnlyPrereleaseVersionExists_ReturnsTrue()
        {
            var vm = MakeRow(latestPre: "2.0.0-beta.1", latestStable: null);
            Assert.True(vm.IsPrerelease);
        }

        [Fact]
        public void InstalledVersionDisplay_WhenInstalled_IncludesVPrefix()
        {
            var vm = MakeRow(installed: "1.2.3");
            Assert.Equal("v1.2.3", vm.InstalledVersionDisplay);
        }

        [Fact]
        public void IsOperationInProgress_DefaultIsFalse()
        {
            var vm = MakeRow();
            Assert.False(vm.IsOperationInProgress);
        }

        [Fact]
        public void IsOperationInProgress_CanBeSetToTrue()
        {
            var vm = MakeRow();
            vm.IsOperationInProgress = true;
            Assert.True(vm.IsOperationInProgress);
        }

        [Fact]
        public void AuthorDisplay_WhenAuthorSet_IncludesByPrefix()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                Authors = "James",
            });
            Assert.Equal("by James", vm.AuthorDisplay);
        }

        [Fact]
        public void AuthorDisplay_WhenNoAuthor_IsEmpty()
        {
            var vm = new PackageRowViewModel(new PackageModel { PackageId = "Pkg" });
            Assert.Equal(string.Empty, vm.AuthorDisplay);
        }
    }
}
