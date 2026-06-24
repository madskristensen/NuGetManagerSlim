using System.Linq;
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
            string[]? requiredByIds = null,
            string? source = "nuget.org",
            bool includePrerelease = false)
        {
            return new PackageRowViewModel(new PackageModel
            {
                PackageId = id,
                InstalledVersion = installed != null ? NuGetVersion.Parse(installed) : null,
                LatestStableVersion = latestStable != null ? NuGetVersion.Parse(latestStable) : null,
                LatestPrereleaseVersion = latestPre != null ? NuGetVersion.Parse(latestPre) : null,
                IsTransitive = isTransitive,
                RequiredByPackageId = requiredBy,
                RequiredByPackageIds = requiredByIds ?? (requiredBy != null ? new[] { requiredBy } : System.Array.Empty<string>()),
                SourceName = source,
            })
            {
                IncludePrerelease = includePrerelease,
            };
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
        public void RequiredByDisplay_WithMultipleAncestors_ListsAll()
        {
            var vm = MakeRow(isTransitive: true, requiredByIds: new[] { "PkgA", "PkgB" });
            Assert.Equal("required by: PkgA, PkgB", vm.RequiredByDisplay);
            Assert.Equal(new[] { "PkgA", "PkgB" }, vm.RequiredByPackageIds);
        }

        [Fact]
        public void RequiredByDisplay_WhenNotTransitive_IsEmpty()
        {
            var vm = MakeRow(isTransitive: false, requiredByIds: new[] { "PkgA" });
            Assert.Equal(string.Empty, vm.RequiredByDisplay);
        }

        [Fact]
        public void HasRequiredBy_WithAncestors_IsTrue()
        {
            var vm = MakeRow(isTransitive: true, requiredBy: "ParentPkg");
            Assert.True(vm.HasRequiredBy);
        }

        [Fact]
        public void HasRequiredBy_TransitiveWithoutAncestors_IsFalse()
        {
            var vm = MakeRow(isTransitive: true, requiredByIds: System.Array.Empty<string>());
            Assert.False(vm.HasRequiredBy);
        }

        [Fact]
        public void HasRequiredBy_WhenNotTransitive_IsFalse()
        {
            var vm = MakeRow(isTransitive: false, requiredByIds: new[] { "PkgA" });
            Assert.False(vm.HasRequiredBy);
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
        public void HasUpdate_StableOnlyMode_IgnoresNewerPrerelease()
        {
            // Installed is the latest stable; only a newer prerelease exists. With
            // prereleases excluded (default) this is not an update.
            var vm = MakeRow(installed: "2.0.0", latestStable: "2.0.0", latestPre: "3.0.0-beta");
            Assert.False(vm.HasUpdate);
            Assert.Equal(NuGetVersion.Parse("2.0.0"), vm.UpdateCandidateVersion);
        }

        [Fact]
        public void HasUpdate_PrereleaseMode_OffersNewerPrerelease()
        {
            var vm = MakeRow(installed: "2.0.0", latestStable: "2.0.0", latestPre: "3.0.0-beta",
                includePrerelease: true);
            Assert.True(vm.HasUpdate);
            Assert.Equal(NuGetVersion.Parse("3.0.0-beta"), vm.UpdateCandidateVersion);
            Assert.Equal("→ 3.0.0-beta", vm.UpdateBadge);
            Assert.Equal("v2.0.0 → v3.0.0-beta", vm.VersionInformation);
        }

        [Fact]
        public void HasUpdate_PrereleaseMode_PrefersStableWhenHigher()
        {
            // A stable higher than the newest prerelease wins as the candidate even
            // when prereleases are included.
            var vm = MakeRow(installed: "1.0.0", latestStable: "3.0.0", latestPre: "2.0.0-beta",
                includePrerelease: true);
            Assert.True(vm.HasUpdate);
            Assert.Equal(NuGetVersion.Parse("3.0.0"), vm.UpdateCandidateVersion);
        }

        [Fact]
        public void HasUpdate_PrereleaseMode_NoNewerVersion_ReturnsFalse()
        {
            var vm = MakeRow(installed: "3.0.0", latestStable: "3.0.0", latestPre: "2.0.0-beta",
                includePrerelease: true);
            Assert.False(vm.HasUpdate);
        }

        [Fact]
        public void IncludePrerelease_Toggle_RaisesUpdateNotifications()
        {
            var vm = MakeRow(installed: "2.0.0", latestStable: "2.0.0", latestPre: "3.0.0-beta");
            Assert.False(vm.HasUpdate);

            var changed = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

            vm.IncludePrerelease = true;

            Assert.True(vm.HasUpdate);
            Assert.Contains(nameof(vm.HasUpdate), changed);
            Assert.Contains(nameof(vm.UpdateCandidateVersion), changed);
            Assert.Contains(nameof(vm.UpdateBadge), changed);
        }

        private static PackageRowViewModel MakeCappedRow(
            string id,
            string installed,
            string[] versions,
            int? cap,
            bool includePrerelease = false)
        {
            var parsed = versions.Select(NuGetVersion.Parse).ToList();
            var (maxStable, maxPre) = NuGetManagerSlim.Services.NuGetFeedService.BuildMaxByMajor(parsed);
            var (latestStable, latestPre) = NuGetManagerSlim.Services.NuGetFeedService.SelectLatestVersions(parsed);
            return new PackageRowViewModel(new PackageModel
            {
                PackageId = id,
                InstalledVersion = NuGetVersion.Parse(installed),
                LatestStableVersion = latestStable,
                LatestPrereleaseVersion = latestPre,
                MaxStableByMajor = maxStable,
                MaxPrereleaseByMajor = maxPre,
            })
            {
                IncludePrerelease = includePrerelease,
                TargetFrameworkMajorCap = cap,
            };
        }

        [Fact]
        public void UpdateCandidate_CappedFamilyWithinTfm_StopsAtTfmMajor()
        {
            var vm = MakeCappedRow(
                "System.Text.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "8.0.4", "9.0.0" },
                cap: 8);

            Assert.Equal(NuGetVersion.Parse("8.0.4"), vm.UpdateCandidateVersion);
            Assert.True(vm.HasUpdate);
            Assert.Equal("→ 8.0.4", vm.UpdateBadge);
        }

        [Fact]
        public void UpdateCandidate_NonCappedFamily_IgnoresCap()
        {
            var vm = MakeCappedRow(
                "Newtonsoft.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "8.0.4", "9.0.0" },
                cap: 8);

            Assert.Equal(NuGetVersion.Parse("9.0.0"), vm.UpdateCandidateVersion);
        }

        [Fact]
        public void UpdateCandidate_NoCap_OffersLatest()
        {
            var vm = MakeCappedRow(
                "System.Text.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "8.0.4", "9.0.0" },
                cap: null);

            Assert.Equal(NuGetVersion.Parse("9.0.0"), vm.UpdateCandidateVersion);
        }

        [Fact]
        public void UpdateCandidate_CapAtOrAboveLatest_OffersLatest()
        {
            var vm = MakeCappedRow(
                "System.Text.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "8.0.4", "9.0.0" },
                cap: 9);

            Assert.Equal(NuGetVersion.Parse("9.0.0"), vm.UpdateCandidateVersion);
        }

        [Fact]
        public void UpdateCandidate_CappedFamily_PrereleaseModeRespectsCap()
        {
            var vm = MakeCappedRow(
                "System.Text.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "8.0.4", "8.1.0-beta", "9.0.0" },
                cap: 8,
                includePrerelease: true);

            Assert.Equal(NuGetVersion.Parse("8.1.0-beta"), vm.UpdateCandidateVersion);
        }

        [Fact]
        public void UpdateCandidate_CappedFamily_NoPerMajorData_FallsBackOnlyWithinCap()
        {
            // No per-major maps (un-enriched row): an above-cap latest must not be
            // suggested, so the candidate is null rather than the 9.0.0 latest.
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "System.Text.Json",
                InstalledVersion = NuGetVersion.Parse("8.0.3"),
                LatestStableVersion = NuGetVersion.Parse("9.0.0"),
            })
            {
                TargetFrameworkMajorCap = 8,
            };

            Assert.Null(vm.UpdateCandidateVersion);
            Assert.False(vm.HasUpdate);
        }

        [Fact]
        public void TargetFrameworkMajorCap_Toggle_RaisesUpdateNotifications()
        {
            var vm = MakeCappedRow(
                "System.Text.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "8.0.4", "9.0.0" },
                cap: null);
            Assert.Equal(NuGetVersion.Parse("9.0.0"), vm.UpdateCandidateVersion);

            var changed = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

            vm.TargetFrameworkMajorCap = 8;

            Assert.Equal(NuGetVersion.Parse("8.0.4"), vm.UpdateCandidateVersion);
            Assert.Contains(nameof(vm.UpdateCandidateVersion), changed);
            Assert.Contains(nameof(vm.HasUpdate), changed);
            Assert.Contains(nameof(vm.UpdateBadge), changed);
        }

        [Fact]
        public void IsUpdateCapped_WhenNewerMajorHeldBack_IsTrueWithTooltip()
        {
            var vm = MakeCappedRow(
                "System.Text.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "8.0.4", "9.0.0" },
                cap: 8);

            Assert.True(vm.IsUpdateCappedByTargetFramework);
            Assert.Contains("8.x", vm.UpdateCapTooltip);
            Assert.Contains("9.0.0", vm.UpdateCapTooltip);
        }

        [Fact]
        public void IsUpdateCapped_WhenCappedFamilyButNoNewerMajor_IsFalse()
        {
            var vm = MakeCappedRow(
                "System.Text.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "8.0.4" },
                cap: 8);

            Assert.False(vm.IsUpdateCappedByTargetFramework);
            Assert.Equal(string.Empty, vm.UpdateCapTooltip);
        }

        [Fact]
        public void IsUpdateCapped_WhenNonCappedFamily_IsFalse()
        {
            var vm = MakeCappedRow(
                "Newtonsoft.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "9.0.0" },
                cap: 8);

            Assert.False(vm.IsUpdateCappedByTargetFramework);
            Assert.Equal(string.Empty, vm.UpdateCapTooltip);
        }

        [Fact]
        public void IsUpdateCapped_WhenNoCap_IsFalse()
        {
            var vm = MakeCappedRow(
                "System.Text.Json",
                installed: "8.0.3",
                versions: new[] { "8.0.3", "9.0.0" },
                cap: null);

            Assert.False(vm.IsUpdateCappedByTargetFramework);
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
        public void ApplyMetadata_AppliesLatestVersion_WhenMetadataHasNoAuthorsAndZeroDownloads()
        {
            // Regression for issue #15: private/Azure Artifacts/GitHub feeds often
            // return empty authors and a zero download count. The latest-version
            // backfill must still be applied so the update badge and Updates view
            // work; previously this combination skipped the merge entirely.
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
                Authors = string.Empty,
                DownloadCount = 0,
            });

            Assert.True(vm.HasUpdate);
            Assert.Equal(NuGetVersion.Parse("2.0.0"), vm.LatestStableVersion);
        }

        [Fact]
        public void ApplyMetadata_DoesNotOverwriteExistingDownloadCount()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
                DownloadCount = 5_000,
            });

            vm.ApplyMetadata(new PackageModel
            {
                PackageId = "Pkg",
                LatestStableVersion = NuGetVersion.Parse("2.0.0"),
                DownloadCount = 0,
            });

            Assert.True(vm.HasUpdate);
            Assert.Equal("5.0K downloads", vm.DownloadCountDisplay);
        }

        [Fact]
        public void UpdateButtonAccessibleName_ContainsPackageIdAndVersion()
        {
            var vm = MakeRow(installed: "1.0.0", latestStable: "2.0.0");
            Assert.Contains("TestPkg", vm.UpdateButtonAccessibleName);
            Assert.Contains("2.0.0", vm.UpdateButtonAccessibleName);
        }

        [Fact]
        public void HasVulnerabilities_WhenNoneOnModel_ReturnsFalse()
        {
            var vm = MakeRow(installed: "1.0.0");
            Assert.False(vm.HasVulnerabilities);
            Assert.Equal(string.Empty, vm.VulnerabilityBadge);
        }

        [Fact]
        public void HasVulnerabilities_WhenModelCarriesAdvisory_ReturnsTrue()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
                Vulnerabilities = new[]
                {
                    new PackageVulnerabilityInfo { Severity = 2, AdvisoryUrl = "https://example.com/a" },
                },
            });
            Assert.True(vm.HasVulnerabilities);
            Assert.Contains("High", vm.VulnerabilityBadge);
        }

        [Fact]
        public void ApplyVulnerabilities_PromotesRowToVulnerableAndPicksMaxSeverity()
        {
            var vm = MakeRow(installed: "1.0.0");
            Assert.False(vm.HasVulnerabilities);

            vm.ApplyVulnerabilities(new[]
            {
                new PackageVulnerabilityInfo { Severity = 1, AdvisoryUrl = "https://example.com/a" },
                new PackageVulnerabilityInfo { Severity = 3, AdvisoryUrl = "https://example.com/b" },
            });

            Assert.True(vm.HasVulnerabilities);
            Assert.Equal(2, vm.Vulnerabilities.Count);
            // Badge reflects the highest severity (Critical) and the advisory count.
            Assert.Contains("Critical", vm.VulnerabilityBadge);
            Assert.Contains("(2)", vm.VulnerabilityBadge);
        }

        [Fact]
        public void ApplyVulnerabilities_WithEmptyList_ClearsVulnerableState()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
                Vulnerabilities = new[]
                {
                    new PackageVulnerabilityInfo { Severity = 2, AdvisoryUrl = "https://example.com/a" },
                },
            });
            Assert.True(vm.HasVulnerabilities);

            vm.ApplyVulnerabilities(System.Array.Empty<PackageVulnerabilityInfo>());

            Assert.False(vm.HasVulnerabilities);
            Assert.Equal(string.Empty, vm.VulnerabilityBadge);
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

        [Fact]
        public void VersionInformation_NoInstalled_ReturnsPrereleaseVersion()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                LatestPrereleaseVersion = NuGetVersion.Parse("1.0.0"),
            });
            Assert.Equal("v1.0.0", vm.VersionInformation);
        }

        [Fact]
        public void VersionInformation_NoInstalled_ReturnsLatestVersion()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                LatestStableVersion = NuGetVersion.Parse("1.0.0"),
            });
            Assert.Equal("v1.0.0", vm.VersionInformation);
        }

        [Fact]
        public void VersionInformation_InstalledNoUpdate_ReturnsInstalledVersion()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
                LatestStableVersion = NuGetVersion.Parse("1.0.0"),
            });
            Assert.True(vm.IsInstalled);
            Assert.Equal("v1.0.0", vm.VersionInformation);
        }

        [Fact]
        public void VersionInformation_InstalledHasUpdate_ReturnsInstalledVersionAndNewVersion()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
                LatestStableVersion = NuGetVersion.Parse("2.0.0"),
            });
            Assert.True(vm.IsInstalled);
            Assert.True(vm.HasUpdate);
            Assert.Equal("v1.0.0 → v2.0.0", vm.VersionInformation);
        }

        [Fact]
        public void IsDeprecated_WhenModelNotDeprecated_ReturnsFalse()
        {
            var vm = MakeRow(installed: "1.0.0");
            Assert.False(vm.IsDeprecated);
        }

        [Fact]
        public void IsDeprecated_WhenModelDeprecated_ReturnsTrueWithReasonTooltip()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
                IsDeprecated = true,
                DeprecationReason = "Legacy, Other",
            });
            Assert.True(vm.IsDeprecated);
            Assert.Contains("Legacy, Other", vm.DeprecationTooltip);
        }

        [Fact]
        public void DeprecationTooltip_WhenNoReason_UsesGenericMessage()
        {
            var vm = new PackageRowViewModel(new PackageModel
            {
                PackageId = "Pkg",
                InstalledVersion = NuGetVersion.Parse("1.0.0"),
                IsDeprecated = true,
            });
            Assert.Equal("This package is deprecated", vm.DeprecationTooltip);
        }

        [Fact]
        public void ApplyMetadata_PromotesRowToDeprecated()
        {
            var vm = MakeRow(installed: "1.0.0");
            Assert.False(vm.IsDeprecated);

            vm.ApplyMetadata(new PackageModel
            {
                PackageId = "TestPkg",
                IsDeprecated = true,
                DeprecationReason = "Critical bug",
            });

            Assert.True(vm.IsDeprecated);
            Assert.Contains("Critical bug", vm.DeprecationTooltip);
        }
    }
}
