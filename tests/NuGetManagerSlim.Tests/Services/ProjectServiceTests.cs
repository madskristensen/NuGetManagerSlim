using System;
using System.IO;
using System.Linq;
using System.Threading;
using NuGetManagerSlim.Services;
using Xunit;

namespace NuGetManagerSlim.Tests.Services
{
    public class ProjectServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public ProjectServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "NuGetManagerSlim.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public void ReadInstalledFromProject_SdkStyleCsproj_ReturnsPackageReferences()
        {
            var path = Path.Combine(_tempDir, "Sample.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                    <PackageReference Include="Serilog" Version="3.1.1" />
                  </ItemGroup>
                </Project>
                """);

            var packages = ProjectService.ReadInstalledFromProject(path).ToList();

            Assert.Equal(2, packages.Count);
            Assert.Contains(packages, p => p.PackageId == "Newtonsoft.Json" && p.InstalledVersion?.ToString() == "13.0.3");
            Assert.Contains(packages, p => p.PackageId == "Serilog" && p.InstalledVersion?.ToString() == "3.1.1");
        }

        [Fact]
        public void ReadInstalledFromProject_PackageReferenceWithVersionElement_IsParsed()
        {
            var path = Path.Combine(_tempDir, "Sample.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Acme.Lib">
                      <Version>2.5.0</Version>
                    </PackageReference>
                  </ItemGroup>
                </Project>
                """);

            var packages = ProjectService.ReadInstalledFromProject(path).ToList();

            Assert.Single(packages);
            Assert.Equal("Acme.Lib", packages[0].PackageId);
            Assert.Equal("2.5.0", packages[0].InstalledVersion?.ToString());
        }

        [Fact]
        public void ReadInstalledFromProject_LegacyPackagesConfig_IsParsed()
        {
            var path = Path.Combine(_tempDir, "Legacy.csproj");
            File.WriteAllText(path, "<Project ToolsVersion=\"4.0\"><ItemGroup /></Project>");
            File.WriteAllText(Path.Combine(_tempDir, "packages.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <packages>
                  <package id="EntityFramework" version="6.4.4" targetFramework="net48" />
                  <package id="log4net" version="2.0.15" targetFramework="net48" />
                </packages>
                """);

            var packages = ProjectService.ReadInstalledFromProject(path).ToList();

            Assert.Equal(2, packages.Count);
            Assert.Contains(packages, p => p.PackageId == "EntityFramework" && p.InstalledVersion?.ToString() == "6.4.4");
            Assert.Contains(packages, p => p.PackageId == "log4net" && p.InstalledVersion?.ToString() == "2.0.15");
        }

        [Fact]
        public void ReadInstalledFromProject_MissingFile_ReturnsEmpty()
        {
            var packages = ProjectService.ReadInstalledFromProject(Path.Combine(_tempDir, "DoesNotExist.csproj")).ToList();
            Assert.Empty(packages);
        }

        [Fact]
        public void ReadInstalledFromProject_NoPackageReferences_ReturnsEmpty()
        {
            var path = Path.Combine(_tempDir, "Empty.csproj");
            File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup /></Project>");

            var packages = ProjectService.ReadInstalledFromProject(path).ToList();

            Assert.Empty(packages);
        }

        [Fact]
        public void ReadInstalledFromProjectWithImports_MergesProjectAndDirectoryBuildProps()
        {
            var projDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projDir);
            var proj = Path.Combine(projDir, "App.csproj");

            File.WriteAllText(proj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="StyleCop.Analyzers" Version="1.2.0-beta.556" />
                  </ItemGroup>
                </Project>
                """);

            var packages = ProjectService.ReadInstalledFromProjectWithImports(proj, CancellationToken.None);

            Assert.Contains(packages, p => p.PackageId == "Newtonsoft.Json" && p.InstalledVersion?.ToString() == "13.0.3");
            Assert.Contains(packages, p => p.PackageId == "StyleCop.Analyzers" && p.InstalledVersion?.ToString() == "1.2.0-beta.556");
        }

        [Fact]
        public void ReadInstalledFromProjectWithImports_ProjectVersionWinsOverImportVersion()
        {
            var projDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projDir);
            var proj = Path.Combine(projDir, "App.csproj");

            File.WriteAllText(proj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="12.0.3" />
                  </ItemGroup>
                </Project>
                """);

            var packages = ProjectService.ReadInstalledFromProjectWithImports(proj, CancellationToken.None);

            var nj = Assert.Single(packages, p => p.PackageId == "Newtonsoft.Json");
            // MergeInstalled keeps the higher version, so 13.0.3 from the
            // project file wins over 12.0.3 from the import.
            Assert.Equal("13.0.3", nj.InstalledVersion?.ToString());
        }

        [Fact]
        public void ReadInstalledFromProject_PackageReferenceWithVersionRange_CapturesAllowedRange()
        {
            var path = Path.Combine(_tempDir, "Sample.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="EntityFrameworkCore.SqlServer.HierarchyId" Version="[1.2.0,2)" />
                  </ItemGroup>
                </Project>
                """);

            var packages = ProjectService.ReadInstalledFromProject(path).ToList();

            Assert.Single(packages);
            var pkg = packages[0];
            Assert.Equal("EntityFrameworkCore.SqlServer.HierarchyId", pkg.PackageId);
            Assert.Null(pkg.InstalledVersion);
            Assert.NotNull(pkg.AllowedVersionRange);
            Assert.True(pkg.AllowedVersionRange!.Satisfies(NuGet.Versioning.NuGetVersion.Parse("1.9.0")));
            Assert.False(pkg.AllowedVersionRange!.Satisfies(NuGet.Versioning.NuGetVersion.Parse("2.0.0")));
        }

        [Fact]
        public void ReadInstalledFromProject_PackagesConfigWithAllowedVersions_CapturesRange()
        {
            var path = Path.Combine(_tempDir, "Legacy.csproj");
            File.WriteAllText(path, "<Project ToolsVersion=\"4.0\"><ItemGroup /></Project>");
            File.WriteAllText(Path.Combine(_tempDir, "packages.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <packages>
                  <package id="EntityFrameworkCore.SqlServer.HierarchyId" version="1.2.0" allowedVersions="[1.2.0,2)" targetFramework="net48" />
                </packages>
                """);

            var packages = ProjectService.ReadInstalledFromProject(path).ToList();

            Assert.Single(packages);
            var pkg = packages[0];
            Assert.Equal("EntityFrameworkCore.SqlServer.HierarchyId", pkg.PackageId);
            Assert.Equal("1.2.0", pkg.InstalledVersion?.ToString());
            Assert.NotNull(pkg.AllowedVersionRange);
            Assert.True(pkg.AllowedVersionRange!.Satisfies(NuGet.Versioning.NuGetVersion.Parse("1.9.0")));
            Assert.False(pkg.AllowedVersionRange!.Satisfies(NuGet.Versioning.NuGetVersion.Parse("2.0.0")));
        }

        [Fact]
        public void MergeInstalled_PreservesAllowedVersionRangeWhenNewEntryHasIt()
        {
            var projDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projDir);
            var proj = Path.Combine(projDir, "App.csproj");

            // Exact version in csproj (as if NuGet API resolved it) + allowedVersions in packages.config
            File.WriteAllText(proj, "<Project ToolsVersion=\"4.0\"><ItemGroup /></Project>");
            File.WriteAllText(Path.Combine(projDir, "packages.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <packages>
                  <package id="SomePkg" version="1.2.0" allowedVersions="[1.2.0,2)" targetFramework="net48" />
                </packages>
                """);

            var packages = ProjectService.ReadInstalledFromProjectWithImports(proj, CancellationToken.None);

            var pkg = Assert.Single(packages, p => p.PackageId == "SomePkg");
            Assert.Equal("1.2.0", pkg.InstalledVersion?.ToString());
            Assert.NotNull(pkg.AllowedVersionRange);
        }

        [Fact]
        public void MergeInstalled_DoesNotContaminateHigherVersionWithUnrelatedRange()
        {
            // Regression test for issue #5.
            // When Project A has PkgX at 1.5.0 with allowedVersions="[1.0,2)" and
            // Project B has PkgX at 7.0.0 with no constraint, the merged entry should
            // keep the higher version (7.0.0) WITHOUT inheriting the [1.0,2) range.
            // Simulated via a single project directory where both a .csproj (high version,
            // SDK-style PackageReference) and packages.config (low version, range) contribute
            // entries for the same package - exercising the same MergeInstalled code paths.
            var projDir = Path.Combine(_tempDir, "Multi");
            Directory.CreateDirectory(projDir);
            var proj = Path.Combine(projDir, "Multi.csproj");

            // SDK-style csproj: PkgX at 7.0.0 (no range)
            File.WriteAllText(proj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="PkgX" Version="7.0.0" />
                  </ItemGroup>
                </Project>
                """);

            // packages.config: PkgX at 1.5.0 with allowedVersions="[1.0,2)"
            File.WriteAllText(Path.Combine(projDir, "packages.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <packages>
                  <package id="PkgX" version="1.5.0" allowedVersions="[1.0,2)" targetFramework="net48" />
                </packages>
                """);

            var packages = ProjectService.ReadInstalledFromProjectWithImports(proj, CancellationToken.None);

            var pkg = Assert.Single(packages, p => p.PackageId == "PkgX");
            // Must keep the higher version.
            Assert.Equal("7.0.0", pkg.InstalledVersion?.ToString());
            // The range from the lower-version entry must NOT bleed onto the 7.0.0 entry
            // because 7.0.0 does not satisfy [1.0,2).
            Assert.Null(pkg.AllowedVersionRange);
        }

        [Fact]
        public void ParseTransitives_CollectsAllDirectAncestors()
        {
            // Issue #19: a transitive package pulled in by two different direct
            // packages should list both as "required by".
            using var doc = System.Text.Json.JsonDocument.Parse("""
                {
                  "project": {
                    "frameworks": {
                      "net48": {
                        "dependencies": {
                          "DirectA": { "target": "Package" },
                          "DirectB": { "target": "Package" }
                        }
                      }
                    }
                  },
                  "targets": {
                    "net48": {
                      "DirectA/1.0.0": { "type": "package", "dependencies": { "Shared": "1.0.0" } },
                      "DirectB/1.0.0": { "type": "package", "dependencies": { "Shared": "1.0.0" } },
                      "Shared/1.0.0": { "type": "package" }
                    }
                  }
                }
                """);

            var transitives = ProjectService.ParseTransitivesFromAssets(doc.RootElement, "App", CancellationToken.None);

            var shared = Assert.Single(transitives, p => p.PackageId == "Shared");
            Assert.True(shared.IsTransitive);
            Assert.Equal(new[] { "DirectA", "DirectB" }, shared.RequiredByPackageIds);
            Assert.Equal("DirectA", shared.RequiredByPackageId);
            // Direct packages are excluded from the transitive list.
            Assert.DoesNotContain(transitives, p => p.PackageId == "DirectA" || p.PackageId == "DirectB");
        }

        [Fact]
        public void ParseTransitives_DeepTransitive_ResolvesToDirectAncestor()
        {
            // A transitive reached only through another transitive should report
            // the top-level direct package, not the intermediate one.
            using var doc = System.Text.Json.JsonDocument.Parse("""
                {
                  "project": {
                    "frameworks": {
                      "net48": {
                        "dependencies": { "Root": { "target": "Package" } }
                      }
                    }
                  },
                  "targets": {
                    "net48": {
                      "Root/1.0.0": { "type": "package", "dependencies": { "Mid": "1.0.0" } },
                      "Mid/1.0.0": { "type": "package", "dependencies": { "Leaf": "1.0.0" } },
                      "Leaf/1.0.0": { "type": "package" }
                    }
                  }
                }
                """);

            var transitives = ProjectService.ParseTransitivesFromAssets(doc.RootElement, "Deep", CancellationToken.None);

            var leaf = Assert.Single(transitives, p => p.PackageId == "Leaf");
            Assert.Equal(new[] { "Root" }, leaf.RequiredByPackageIds);
            var mid = Assert.Single(transitives, p => p.PackageId == "Mid");
            Assert.Equal(new[] { "Root" }, mid.RequiredByPackageIds);
        }

        [Fact]
        public void ReadInstalledFromProject_SdkStyle_AttributesTargetFramework()
        {
            // Issue #30: every package carries the project's target framework so
            // the update cap can later be resolved per package.
            var path = Path.Combine(_tempDir, "Sample.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="8.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var pkg = Assert.Single(ProjectService.ReadInstalledFromProject(path).ToList());
            Assert.Equal(new[] { "net8.0" }, pkg.ReferencingFrameworks);
        }

        [Fact]
        public void ReadInstalledFromProject_MultiTarget_AttributesAllFrameworks()
        {
            var path = Path.Combine(_tempDir, "Sample.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net48;net8.0</TargetFrameworks></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="8.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var pkg = Assert.Single(ProjectService.ReadInstalledFromProject(path).ToList());
            Assert.Equal(new[] { "net48", "net8.0" }, pkg.ReferencingFrameworks);
        }

        [Fact]
        public void ReadInstalledFromProject_Net8OnlyCappedFamily_ResolvesCapTo8()
        {
            // The core of issue #30: a package referenced only by a net8.0 project
            // is capped at major 8 even though, before the fix, a net48 project
            // elsewhere in the solution disabled the cap for everything.
            var path = Path.Combine(_tempDir, "Sample.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="8.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var pkg = Assert.Single(ProjectService.ReadInstalledFromProject(path).ToList());
            Assert.Equal(8, TargetFrameworkCap.ResolveCap(pkg.ReferencingFrameworks));
        }

        [Fact]
        public void ReadInstalledFromProject_MixedNet48Net8_ResolvesToNoCap()
        {
            // A package referenced by a net48 leg must stay uncapped so its net48
            // compatibility isn't broken - consistent with the existing rule.
            var path = Path.Combine(_tempDir, "Sample.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net48;net8.0</TargetFrameworks></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="8.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var pkg = Assert.Single(ProjectService.ReadInstalledFromProject(path).ToList());
            Assert.Null(TargetFrameworkCap.ResolveCap(pkg.ReferencingFrameworks));
        }

        [Fact]
        public void ReadInstalledFromProject_LegacyFrameworkVersion_ResolvesToNoCap()
        {
            var path = Path.Combine(_tempDir, "Legacy.csproj");
            File.WriteAllText(path, """
                <Project ToolsVersion="4.0">
                  <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
                  <ItemGroup />
                </Project>
                """);
            File.WriteAllText(Path.Combine(_tempDir, "packages.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <packages>
                  <package id="Microsoft.Extensions.Caching.Memory" version="3.1.0" targetFramework="net48" />
                </packages>
                """);

            var pkg = Assert.Single(ProjectService.ReadInstalledFromProject(path).ToList());
            Assert.Equal(new[] { "v4.8" }, pkg.ReferencingFrameworks);
            Assert.Null(TargetFrameworkCap.ResolveCap(pkg.ReferencingFrameworks));
        }

        [Fact]
        public void ReadInstalledFromProjectWithImports_AttributesFrameworksToImportedPackages()
        {
            var projDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projDir);
            var proj = Path.Combine(projDir, "App.csproj");

            File.WriteAllText(proj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Extensions.Logging" Version="8.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var packages = ProjectService.ReadInstalledFromProjectWithImports(proj, CancellationToken.None);

            var direct = Assert.Single(packages, p => p.PackageId == "Newtonsoft.Json");
            Assert.Equal(new[] { "net8.0" }, direct.ReferencingFrameworks);
            var imported = Assert.Single(packages, p => p.PackageId == "Microsoft.Extensions.Logging");
            Assert.Equal(new[] { "net8.0" }, imported.ReferencingFrameworks);
        }

        [Fact]
        public void ReadInstalledFromProjectWithImports_FrameworkInDirectoryBuildProps_ResolvesCap()
        {
            // Issue #32: the project centralizes <TargetFramework> in a
            // Directory.Build.props, so the csproj declares none. The cap must
            // still resolve from the import, otherwise a runtime-locked family
            // (e.g. Microsoft.Windows.Compatibility) is wrongly offered a higher
            // major.
            var projDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projDir);
            var proj = Path.Combine(projDir, "App.csproj");

            File.WriteAllText(proj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Windows.Compatibility" Version="8.0.28" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                </Project>
                """);

            var packages = ProjectService.ReadInstalledFromProjectWithImports(proj, CancellationToken.None);

            var pkg = Assert.Single(packages, p => p.PackageId == "Microsoft.Windows.Compatibility");
            Assert.Equal(new[] { "net8.0" }, pkg.ReferencingFrameworks);
            Assert.Equal(8, TargetFrameworkCap.ResolveCap(pkg.ReferencingFrameworks));
        }

        [Fact]
        public void ReadInstalledFromProjectWithImports_FrameworkExpressionInProps_DoesNotResolveCap()
        {
            // An unevaluated MSBuild expression in the import must not be treated
            // as a literal moniker; with no resolvable framework there is no cap.
            var projDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projDir);
            var proj = Path.Combine(projDir, "App.csproj");

            File.WriteAllText(proj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Windows.Compatibility" Version="8.0.28" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup><TargetFramework>$(DefaultTfm)</TargetFramework></PropertyGroup>
                </Project>
                """);

            var packages = ProjectService.ReadInstalledFromProjectWithImports(proj, CancellationToken.None);

            var pkg = Assert.Single(packages, p => p.PackageId == "Microsoft.Windows.Compatibility");
            Assert.Empty(pkg.ReferencingFrameworks);
            Assert.Null(TargetFrameworkCap.ResolveCap(pkg.ReferencingFrameworks));
        }
    }
}
