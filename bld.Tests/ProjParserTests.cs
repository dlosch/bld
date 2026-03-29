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
