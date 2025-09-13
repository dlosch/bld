using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.Text;
using Xunit.Abstractions;
using Spectre.Console;

namespace bld.Tests;

public class EnumeratorTests(ITestOutputHelper output) {
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task EnumerateProjectPaths_WithValidSolutionFile_ReturnsProjectPaths() {
        // Arrange
        var tempDir = CreateTempDirectory();
        var slnPath = Path.Combine(tempDir, "test.sln");
        var projPath1 = Path.Combine(tempDir, "Project1", "Project1.csproj");
        var projPath2 = Path.Combine(tempDir, "Project2", "Project2.vbproj");
        
        try {
            // Create directory structure
            Directory.CreateDirectory(Path.GetDirectoryName(projPath1)!);
            Directory.CreateDirectory(Path.GetDirectoryName(projPath2)!);
            
            // Create simple project files
            await File.WriteAllTextAsync(projPath1, CreateSimpleCsprojContent());
            await File.WriteAllTextAsync(projPath2, CreateSimpleVbprojContent());
            
            // Create solution file
            await File.WriteAllTextAsync(slnPath, CreateSimpleSolutionContent(projPath1, projPath2));
            
            var options = new CleaningOptions();
            var errorSink = new ErrorSink(new TestConsoleOutput(_output));
            var enumerator = new Enumerator(options, errorSink);

            // Act
            var projectPaths = new List<string>();
            await foreach (var path in enumerator.EnumerateProjectPaths(slnPath, EnumerationType.Sln)) {
                projectPaths.Add(path);
            }

            // Assert
            Assert.Equal(2, projectPaths.Count);
            Assert.Contains(projPath1, projectPaths);
            Assert.Contains(projPath2, projectPaths);
        }
        finally {
            CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnumerateProjectPaths_WithValidProjectFile_ReturnsSinglePath() {
        // Arrange
        var tempDir = CreateTempDirectory();
        var projPath = Path.Combine(tempDir, "Test.csproj");
        
        try {
            await File.WriteAllTextAsync(projPath, CreateSimpleCsprojContent());
            
            var options = new CleaningOptions();
            var errorSink = new ErrorSink(new TestConsoleOutput(_output));
            var enumerator = new Enumerator(options, errorSink);

            // Act
            var projectPaths = new List<string>();
            await foreach (var path in enumerator.EnumerateProjectPaths(projPath, EnumerationType.Project)) {
                projectPaths.Add(path);
            }

            // Assert
            Assert.Single(projectPaths);
            Assert.Equal(projPath, projectPaths[0]);
        }
        finally {
            CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnumerateProjectPaths_WithDirectoryContainingProjects_ReturnsAllProjectPaths() {
        // Arrange
        var tempDir = CreateTempDirectory();
        var projPath1 = Path.Combine(tempDir, "Proj1", "Test1.csproj");
        var projPath2 = Path.Combine(tempDir, "Proj2", "Test2.sqlproj");
        var projPath3 = Path.Combine(tempDir, "Proj3", "Test3.fsproj");
        
        try {
            // Create directory structure and project files
            Directory.CreateDirectory(Path.GetDirectoryName(projPath1)!);
            Directory.CreateDirectory(Path.GetDirectoryName(projPath2)!);
            Directory.CreateDirectory(Path.GetDirectoryName(projPath3)!);
            
            await File.WriteAllTextAsync(projPath1, CreateSimpleCsprojContent());
            await File.WriteAllTextAsync(projPath2, CreateSimpleSqlprojContent());
            await File.WriteAllTextAsync(projPath3, CreateSimpleFsprojContent());
            
            var options = new CleaningOptions();
            var errorSink = new ErrorSink(new TestConsoleOutput(_output));
            var enumerator = new Enumerator(options, errorSink);

            // Act
            var projectPaths = new List<string>();
            await foreach (var path in enumerator.EnumerateProjectPaths(tempDir, EnumerationType.Project)) {
                projectPaths.Add(path);
            }

            // Assert
            Assert.Equal(3, projectPaths.Count);
            Assert.Contains(projPath1, projectPaths);
            Assert.Contains(projPath2, projectPaths);
            Assert.Contains(projPath3, projectPaths);
        }
        finally {
            CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnumerateProjectPaths_WithNonExistentPath_ReturnsEmpty() {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var options = new CleaningOptions();
        var errorSink = new ErrorSink(new TestConsoleOutput(_output));
        var enumerator = new Enumerator(options, errorSink);

        // Act
        var projectPaths = new List<string>();
        await foreach (var path in enumerator.EnumerateProjectPaths(nonExistentPath, EnumerationType.Project)) {
            projectPaths.Add(path);
        }

        // Assert
        Assert.Empty(projectPaths);
    }

    [Fact]
    public async Task EnumerateProjectPaths_WithEmptyPath_ReturnsEmpty() {
        // Arrange
        var options = new CleaningOptions();
        var errorSink = new ErrorSink(new TestConsoleOutput(_output));
        var enumerator = new Enumerator(options, errorSink);

        // Act
        var projectPaths = new List<string>();
        await foreach (var path in enumerator.EnumerateProjectPaths("", EnumerationType.Project)) {
            projectPaths.Add(path);
        }

        // Assert
        Assert.Empty(projectPaths);
    }

    private static string CreateTempDirectory() {
        var tempPath = Path.Combine(Path.GetTempPath(), "EnumeratorTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        return tempPath;
    }

    private static void CleanupTempDirectory(string path) {
        if (Directory.Exists(path)) {
            Directory.Delete(path, true);
        }
    }

    private static string CreateSimpleCsprojContent() {
        return """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
    }

    private static string CreateSimpleVbprojContent() {
        return """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
    }

    private static string CreateSimpleSqlprojContent() {
        return """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
    }

    private static string CreateSimpleFsprojContent() {
        return """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
    }

    private static string CreateSimpleSolutionContent(string projPath1, string projPath2) {
        var proj1Guid = Guid.NewGuid().ToString().ToUpper();
        var proj2Guid = Guid.NewGuid().ToString().ToUpper();
        var slnGuid = Guid.NewGuid().ToString().ToUpper();
        
        return $"Microsoft Visual Studio Solution File, Format Version 12.00\n" +
               $"# Visual Studio Version 17\n" +
               $"VisualStudioVersion = 17.0.31903.59\n" +
               $"MinimumVisualStudioVersion = 10.0.40219.1\n" +
               $"Project(\"{{{proj1Guid}}}\") = \"Project1\", \"{projPath1.Replace(Path.DirectorySeparatorChar, '\\')}\", \"{{{Guid.NewGuid().ToString().ToUpper()}}}\"\n" +
               $"EndProject\n" +
               $"Project(\"{{{proj2Guid}}}\") = \"Project2\", \"{projPath2.Replace(Path.DirectorySeparatorChar, '\\')}\", \"{{{Guid.NewGuid().ToString().ToUpper()}}}\"\n" +
               $"EndProject\n" +
               $"Global\n" +
               $"    GlobalSection(SolutionConfigurationPlatforms) = preSolution\n" +
               $"        Debug|Any CPU = Debug|Any CPU\n" +
               $"        Release|Any CPU = Release|Any CPU\n" +
               $"    EndGlobalSection\n" +
               $"    GlobalSection(ProjectConfigurationPlatforms) = postSolution\n" +
               $"    EndGlobalSection\n" +
               $"EndGlobal\n";
    }
}

internal class TestConsoleOutput(ITestOutputHelper output) : IConsoleOutput {
    public void WriteError(string message, Exception? exception = default) => output.WriteLine($"ERROR: {message}");
    public void WriteWarning(string message) => output.WriteLine($"WARNING: {message}");
    public void WriteInfo(string message) => output.WriteLine($"INFO: {message}");
    public void WriteVerbose(string message) => output.WriteLine($"VERBOSE: {message}");
    public void WriteDebug(string message) => output.WriteLine($"DEBUG: {message}");
    public void WriteRule(string title) => output.WriteLine($"=== {title} ===");
    public void WriteTable(Table table) => output.WriteLine("TABLE: " + table.ToString());
    public bool Confirm(string message, bool defaultValue = false) => defaultValue;
    public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull => throw new NotImplementedException();
    public void StartProgress(string description, Action<ProgressContext> action) => action(null!);
    public Task StartProgressAsync(string description, Func<ProgressContext, Task> action) => action(null!);
    public Task StartStatusAsync(string description, Func<StatusContext, Task> action) => action(null!);
    public void WriteException(Exception exception) => output.WriteLine($"EXCEPTION: {exception.Message}");
    public void WriteOutput(string caption, string content) => output.WriteLine($"{caption}: {content}");
}