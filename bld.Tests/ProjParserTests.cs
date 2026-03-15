using bld.Infrastructure;
using bld.Models;
using bld.Services;
using Spectre.Console;

namespace bld.Tests;

public class ProjParserTests {
    private sealed class TestConsole : IConsoleOutput {
        public void WriteLine(string message) { }
        public void WriteInfo(string message) { }
        public void WriteWarning(string message) { }
        public void WriteError(string message, Exception? exception = default) { }
        public void WriteDebug(string message) { }
        public void WriteVerbose(string message) { }
        public void WriteTable(Table table) { }
        public void WriteRule(string title) { }
        public bool Confirm(string message, bool defaultValue = false) => defaultValue;
        public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull => default!;
        public void StartProgress(string description, Action<ProgressContext> action) { }
        public Task StartProgressAsync(string description, Func<ProgressContext, Task> action) => Task.CompletedTask;
        public Task StartStatusAsync(string description, Func<StatusContext, Task> action) => Task.CompletedTask;
        public void WriteException(Exception exception) { }
        public void WriteOutput(string caption, string? content = default) { }
        public void WriteHeader(string caption, string? additionaltext = default) { }
    }

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

            var console = new TestConsole();
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
