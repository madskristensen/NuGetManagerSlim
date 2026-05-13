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
    }
}
