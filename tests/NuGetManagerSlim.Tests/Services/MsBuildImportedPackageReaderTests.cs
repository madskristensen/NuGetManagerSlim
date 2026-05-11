using System;
using System.IO;
using System.Linq;
using System.Threading;
using NuGetManagerSlim.Services;
using Xunit;

namespace NuGetManagerSlim.Tests.Services
{
    public class MsBuildImportedPackageReaderTests : IDisposable
    {
        private readonly string _tempDir;

        public MsBuildImportedPackageReaderTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "NuGetManagerSlim.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public void ReadImportedPackages_NoImportFiles_ReturnsEmpty()
        {
            var projectDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projectDir);
            var project = Path.Combine(projectDir, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var result = MsBuildImportedPackageReader.ReadImportedPackages(project, CancellationToken.None);
            Assert.Empty(result);
        }

        [Fact]
        public void ReadImportedPackages_DirectoryBuildProps_PackageReferenceIsReturned()
        {
            var projectDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projectDir);
            var project = Path.Combine(projectDir, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="StyleCop.Analyzers" Version="1.2.0-beta.556" PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """);

            var result = MsBuildImportedPackageReader.ReadImportedPackages(project, CancellationToken.None).ToList();

            Assert.Single(result);
            Assert.Equal("StyleCop.Analyzers", result[0].PackageId);
            Assert.Equal("1.2.0-beta.556", result[0].InstalledVersion?.ToString());
        }

        [Fact]
        public void ReadImportedPackages_CentralPackageManagement_AppliesVersionFromDirectoryPackagesProps()
        {
            var projectDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projectDir);
            var project = Path.Combine(projectDir, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Serilog" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Packages.props"), """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Serilog" Version="3.1.1" />
                  </ItemGroup>
                </Project>
                """);

            var result = MsBuildImportedPackageReader.ReadImportedPackages(project, CancellationToken.None).ToList();

            Assert.Single(result);
            Assert.Equal("Serilog", result[0].PackageId);
            Assert.Equal("3.1.1", result[0].InstalledVersion?.ToString());
        }

        [Fact]
        public void ReadImportedPackages_VersionWithMsBuildExpression_IsSkipped()
        {
            var projectDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projectDir);
            var project = Path.Combine(projectDir, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Acme.Lib" Version="$(AcmeVersion)" />
                  </ItemGroup>
                </Project>
                """);

            var result = MsBuildImportedPackageReader.ReadImportedPackages(project, CancellationToken.None).ToList();
            Assert.Empty(result);
        }

        [Fact]
        public void ReadImportedPackages_ClosestFileWins()
        {
            // Two Directory.Build.props files, one in project's parent and one
            // higher up the tree. The closer one should win when both declare
            // the same id.
            var projectDir = Path.Combine(_tempDir, "Layer1", "App");
            Directory.CreateDirectory(projectDir);
            var project = Path.Combine(projectDir, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="12.0.3" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(_tempDir, "Layer1", "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                  </ItemGroup>
                </Project>
                """);

            var result = MsBuildImportedPackageReader.ReadImportedPackages(project, CancellationToken.None).ToList();
            Assert.Single(result);
            Assert.Equal("Newtonsoft.Json", result[0].PackageId);
            Assert.Equal("13.0.3", result[0].InstalledVersion?.ToString());
        }

        [Fact]
        public void ReadImportedPackages_MalformedXml_ReturnsEmpty()
        {
            var projectDir = Path.Combine(_tempDir, "App");
            Directory.CreateDirectory(projectDir);
            var project = Path.Combine(projectDir, "App.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project><not closed");

            var result = MsBuildImportedPackageReader.ReadImportedPackages(project, CancellationToken.None);
            Assert.Empty(result);
        }
    }
}
