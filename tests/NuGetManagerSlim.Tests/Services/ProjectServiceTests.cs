using System;
using System.IO;
using System.Linq;
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
    }
}
