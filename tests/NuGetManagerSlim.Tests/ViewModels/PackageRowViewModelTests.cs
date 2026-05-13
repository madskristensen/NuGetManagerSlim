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

        [Fact]
        public void HasUpdate_WhenLatestExceedsAllowedRange_ReturnsTrue()
        {
            // AllowedVersionRange caps the target version during install but does not
            // affect whether an update is shown. The absolute latest (3.0.0) exceeds the
            // range, but a newer version may still exist within the range (e.g. 1.9.0).
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "TestPkg",
                InstalledVersion = NuGetVersion.Parse("1.2.0"),
                LatestStableVersion = NuGetVersion.Parse("3.0.0"),
                AllowedVersionRange = NuGet.Versioning.VersionRange.Parse("[1.2.0,2)"),
            });
            Assert.True(vm.HasUpdate);
        }

        [Fact]
        public void HasUpdate_WhenLatestWithinAllowedRange_ReturnsTrue()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "TestPkg",
                InstalledVersion = NuGetVersion.Parse("1.2.0"),
                LatestStableVersion = NuGetVersion.Parse("1.9.0"),
                AllowedVersionRange = NuGet.Versioning.VersionRange.Parse("[1.2.0,2)"),
            });
            Assert.True(vm.HasUpdate);
        }

        [Fact]
        public void HasUpdate_WhenAllowedRangeIsNull_UsesVersionComparison()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "TestPkg",
                InstalledVersion = NuGetVersion.Parse("1.2.0"),
                LatestStableVersion = NuGetVersion.Parse("3.0.0"),
                AllowedVersionRange = null,
            });
            Assert.True(vm.HasUpdate);
        }

        [Fact]
        public void HasUpdate_WhenTransitive_ReturnsFalse()
        {
            var vm = MakeRow(installed: "1.0.0", latestStable: "2.0.0", isTransitive: true);
            Assert.False(vm.HasUpdate);
        }

        [Fact]
        public void DownloadCountDisplay_WhenMillions_FormatsMSuffix()
        {
            var vm = new PackageRowViewModel(new PackageModel { PackageId = "Pkg", DownloadCount = 2_500_000 });
            Assert.Equal("2.5M downloads", vm.DownloadCountDisplay);
        }

        [Fact]
        public void DownloadCountDisplay_WhenThousands_FormatsKSuffix()
        {
            var vm = new PackageRowViewModel(new PackageModel { PackageId = "Pkg", DownloadCount = 5_000 });
            Assert.Equal("5.0K downloads", vm.DownloadCountDisplay);
        }

        [Fact]
        public void DownloadCountDisplay_WhenBillions_FormatsBSuffix()
        {
            var vm = new PackageRowViewModel(new PackageModel { PackageId = "Pkg", DownloadCount = 2_000_000_000 });
            Assert.Equal("2.0B downloads", vm.DownloadCountDisplay);
        }

        [Fact]
        public void DownloadCountDisplay_WhenSmallNumber_FormatsAsInteger()
        {
            var vm = new PackageRowViewModel(new PackageModel { PackageId = "Pkg", DownloadCount = 999 });
            Assert.Equal("999 downloads", vm.DownloadCountDisplay);
        }

        [Fact]
        public void DownloadCountDisplay_WhenZero_IsEmpty()
        {
            var vm = new PackageRowViewModel(new PackageModel { PackageId = "Pkg", DownloadCount = 0 });
            Assert.Equal(string.Empty, vm.DownloadCountDisplay);
        }

        [Fact]
        public void InstalledVersionDisplay_WhenNotInstalled_FallsBackToLatestStable()
        {
            var vm = MakeRow(installed: null, latestStable: "3.0.0");
            Assert.Equal("v3.0.0", vm.InstalledVersionDisplay);
        }

        [Fact]
        public void InstalledVersionDisplay_WhenNotInstalledAndNoStable_FallsBackToPrerelease()
        {
            var vm = MakeRow(installed: null, latestPre: "2.0.0-beta.1");
            Assert.Equal("v2.0.0-beta.1", vm.InstalledVersionDisplay);
        }

        [Fact]
        public void InstalledVersionDisplay_WhenNoVersionAtAll_IsEmpty()
        {
            var vm = MakeRow(installed: null);
            Assert.Equal(string.Empty, vm.InstalledVersionDisplay);
        }

        [Fact]
        public void CanQuickInstall_WhenNotInstalledAndLatestStableKnown_ReturnsTrue()
        {
            var vm = MakeRow(installed: null, latestStable: "1.0.0");
            Assert.True(vm.CanQuickInstall);
        }

        [Fact]
        public void CanQuickInstall_WhenInstalled_ReturnsFalse()
        {
            var vm = MakeRow(installed: "1.0.0", latestStable: "1.0.0");
            Assert.False(vm.CanQuickInstall);
        }

        [Fact]
        public void CanQuickInstall_WhenNotInstalledAndNoVersionKnown_ReturnsFalse()
        {
            var vm = MakeRow(installed: null, latestStable: null);
            Assert.False(vm.CanQuickInstall);
        }

        [Fact]
        public void CanQuickUninstall_WhenInstalledAndNotTransitive_ReturnsTrue()
        {
            var vm = MakeRow(installed: "1.0.0", isTransitive: false);
            Assert.True(vm.CanQuickUninstall);
        }

        [Fact]
        public void CanQuickUninstall_WhenTransitive_ReturnsFalse()
        {
            var vm = MakeRow(installed: "1.0.0", isTransitive: true);
            Assert.False(vm.CanQuickUninstall);
        }

        [Fact]
        public void CanQuickUninstall_WhenNotInstalled_ReturnsFalse()
        {
            var vm = MakeRow(installed: null);
            Assert.False(vm.CanQuickUninstall);
        }

        [Fact]
        public void MarkIconFailed_SetsHasIconToFalse()
        {
            var vm = new PackageRowViewModel(new PackageModel { PackageId = "Pkg", IconUrl = "https://example.com/icon.png" });
            Assert.True(vm.HasIcon);
            vm.MarkIconFailed();
            Assert.False(vm.HasIcon);
        }

        [Fact]
        public void ApplyMetadata_UpdatesLatestStableVersionOnRow()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
            });
            Assert.False(vm.HasUpdate);

            vm.ApplyMetadata(new PackageModel
            {
                PackageId = "Pkg",
                LatestStableVersion = NuGetVersion.Parse("2.0.0"),
                Authors = "Someone",
            });

            Assert.True(vm.HasUpdate);
        }

        [Fact]
        public void UpdateButtonAccessibleName_ContainsPackageIdAndVersion()
        {
            var vm = MakeRow(installed: "1.0.0", latestStable: "2.0.0");
            Assert.Contains("TestPkg", vm.UpdateButtonAccessibleName);
            Assert.Contains("2.0.0", vm.UpdateButtonAccessibleName);
        }

        [Fact]
        public void InstallButtonAccessibleName_ContainsPackageId()
        {
            var vm = MakeRow();
            Assert.Contains("TestPkg", vm.InstallButtonAccessibleName);
        }

        [Fact]
        public void UninstallButtonAccessibleName_ContainsPackageId()
        {
            var vm = MakeRow();
            Assert.Contains("TestPkg", vm.UninstallButtonAccessibleName);
        }
    }
}
