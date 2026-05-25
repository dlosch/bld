using bld.Infrastructure;
using bld.Models;
using bld.Services;
using Xunit.Abstractions;

namespace bld.Tests;

public class ProjParserTests(ITestOutputHelper Console) {
    
    [Fact]
    public void GetPackageReferences_WithCpmEnabledAndNoImportedProps_DoesNotThrow() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-projparser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var projectPath = Path.Combine(tempDir, "Sample.csproj");
            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                </Project>
                """);

            var console = new TestConsole(Console);
            MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
            var errorSink = new ErrorSink(console);
            var parser = new ProjParser(console, errorSink, new CleaningOptions());
            var projCfg = new ProjCfg(new Proj(projectPath, null), "Debug");

            var refs = parser.GetPackageReferences(projCfg);

            Assert.NotNull(refs);
            Assert.True(refs!.UseCpm ?? false);
            Assert.Null(refs.CpmFile);
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void GetPackageReferences_DerivesCpmFileFromPackageVersionSource() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-cpm-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var cpmPath = Path.Combine(tempDir, "Directory.Packages.props");
            File.WriteAllText(cpmPath, """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Orphan.Pkg" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var projectPath = Path.Combine(tempDir, "Sample.csproj");
            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var console = new TestConsole(Console);
            MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
            var errorSink = new ErrorSink(console);
            var parser = new ProjParser(console, errorSink, new CleaningOptions());
            var projCfg = new ProjCfg(new Proj(projectPath, null), "Release");

            var refs = parser.GetPackageReferences(projCfg);

            Assert.NotNull(refs);
            Assert.True(refs!.UseCpm ?? false);
            Assert.NotNull(refs.CpmFile);
            Assert.Equal(cpmPath, refs.CpmFile, StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(refs.PackageVersions);
            Assert.True(refs.PackageVersions!.ContainsKey("Orphan.Pkg"));
            Assert.Equal(cpmPath, refs.PackageVersions["Orphan.Pkg"].SourceFile, StringComparer.OrdinalIgnoreCase);
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void GetPackageReferences_TracksSourceFilePerPackageVersion_AcrossImports() {
        // PackageVersion entries split between Directory.Packages.props and an imported props file
        // must each report the actual file they were declared in. Without per-item attribution,
        // orphan detection and --comment-orphans would write to the wrong file.
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-cpm-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var importedPath = Path.Combine(tempDir, "Imported.props");
            File.WriteAllText(importedPath, """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="Pkg.From.Imported" Version="9.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var cpmPath = Path.Combine(tempDir, "Directory.Packages.props");
            File.WriteAllText(cpmPath, """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <Import Project="Imported.props" />
                  <ItemGroup>
                    <PackageVersion Include="Pkg.From.Cpm" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var projectPath = Path.Combine(tempDir, "Sample.csproj");
            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var console = new TestConsole(Console);
            MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
            var errorSink = new ErrorSink(console);
            var parser = new ProjParser(console, errorSink, new CleaningOptions());
            var projCfg = new ProjCfg(new Proj(projectPath, null), "Release");

            var refs = parser.GetPackageReferences(projCfg);

            Assert.NotNull(refs);
            Assert.True(refs!.UseCpm ?? false);
            Assert.NotNull(refs.PackageVersions);
            Assert.True(refs.PackageVersions!.ContainsKey("Pkg.From.Cpm"));
            Assert.True(refs.PackageVersions.ContainsKey("Pkg.From.Imported"));
            Assert.Equal(cpmPath, refs.PackageVersions["Pkg.From.Cpm"].SourceFile, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(importedPath, refs.PackageVersions["Pkg.From.Imported"].SourceFile, StringComparer.OrdinalIgnoreCase);
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void GetProjectReferences_ResolvesRelativePathsAndSkipsMissing() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-projref-{Guid.NewGuid():N}");
        var libDir = Path.Combine(tempDir, "Lib");
        Directory.CreateDirectory(libDir);
        try {
            var libPath = Path.Combine(libDir, "Lib.csproj");
            File.WriteAllText(libPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var appPath = Path.Combine(tempDir, "App.csproj");
            File.WriteAllText(appPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Lib/Lib.csproj" />
                    <ProjectReference Include="DoesNotExist/Ghost.csproj" />
                  </ItemGroup>
                </Project>
                """);

            var console = new TestConsole(Console);
            MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
            var errorSink = new ErrorSink(console);
            var parser = new ProjParser(console, errorSink, new CleaningOptions());

            var refs = parser.GetProjectReferences(appPath);

            Assert.Single(refs);
            Assert.Equal(libPath, refs[0], StringComparer.OrdinalIgnoreCase);
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void LoadProject_NormalizesBaseOutputAndIntermediatePaths() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-projparser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var projectPath = Path.Combine(tempDir, "Paths.csproj");
            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <BaseOutputPath>bin\custom\</BaseOutputPath>
                    <BaseIntermediateOutputPath>obj\custom\</BaseIntermediateOutputPath>
                  </PropertyGroup>
                </Project>
                """);

            var console = new TestConsole();
            MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
            var errorSink = new ErrorSink(console);
            var parser = new ProjParser(console, errorSink, new CleaningOptions());
            var projCfg = new ProjCfg(new Proj(projectPath, null), "Debug");

            var info = parser.LoadProject(projCfg, Array.Empty<string>());

            Assert.NotNull(info);
            Assert.NotNull(info!.BaseOutputPath);
            Assert.NotNull(info.IntermediateOutputPath);

            if (Path.DirectorySeparatorChar != '\\') {
                Assert.DoesNotContain('\\', info.BaseOutputPath!);
                Assert.DoesNotContain('\\', info.IntermediateOutputPath!);
            }
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
