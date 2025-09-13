using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.Text;
using Xunit.Abstractions;
using Spectre.Console;

namespace bld.Tests;

public class EnumeratorTests(ITestOutputHelper output) {
    private readonly ITestOutputHelper _output = output;

    // Initialize MSBuild once for all tests
    static EnumeratorTests() {
        var console = new TestConsoleOutput(null!); // Null output for static initialization
        var options = new CleaningOptions();
        MSBuildInitializer.Initialize(console, options);
    }

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

    [Fact]
    public async Task EnumerateProjCfg_WithValidSolutionFile_ReturnsProjCfgWithConfigurations() {
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
            
            // Create solution file with configurations
            await File.WriteAllTextAsync(slnPath, CreateSolutionWithConfigurations(projPath1, projPath2));
            
            var options = new CleaningOptions();
            var errorSink = new ErrorSink(new TestConsoleOutput(_output));
            var enumerator = new Enumerator(options, errorSink);

            // Act
            var projCfgs = new List<ProjCfg>();
            await foreach (var projCfg in enumerator.EnumerateProjCfg(slnPath, EnumerationType.Sln)) {
                projCfgs.Add(projCfg);
            }

            // Assert
            Assert.Equal(4, projCfgs.Count); // 2 projects * 2 configurations each
            
            // Check that we have both Debug and Release configurations for each project
            var proj1Cfgs = projCfgs.Where(p => p.Path == projPath1).ToList();
            var proj2Cfgs = projCfgs.Where(p => p.Path == projPath2).ToList();
            
            Assert.Equal(2, proj1Cfgs.Count);
            Assert.Equal(2, proj2Cfgs.Count);
            
            Assert.Contains(proj1Cfgs, p => p.Configuration == "Debug");
            Assert.Contains(proj1Cfgs, p => p.Configuration == "Release");
            Assert.Contains(proj2Cfgs, p => p.Configuration == "Debug");
            Assert.Contains(proj2Cfgs, p => p.Configuration == "Release");

            // Check that solution reference is properly set
            Assert.All(projCfgs, projCfg => Assert.NotNull(projCfg.Proj.Parent));
            Assert.All(projCfgs, projCfg => Assert.Equal(slnPath, projCfg.Proj.Parent!.Path));
        }
        finally {
            CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnumerateProjCfg_WithValidProjectFile_ReturnsDefaultConfigurations() {
        // Arrange
        var tempDir = CreateTempDirectory();
        var projPath = Path.Combine(tempDir, "Test.csproj");
        
        try {
            await File.WriteAllTextAsync(projPath, CreateSimpleCsprojContent());
            
            var options = new CleaningOptions();
            var errorSink = new ErrorSink(new TestConsoleOutput(_output));
            var enumerator = new Enumerator(options, errorSink);

            // Act
            var projCfgs = new List<ProjCfg>();
            await foreach (var projCfg in enumerator.EnumerateProjCfg(projPath, EnumerationType.Project)) {
                projCfgs.Add(projCfg);
            }

            // Assert
            Assert.Equal(2, projCfgs.Count); // Debug and Release by default
            Assert.Contains(projCfgs, p => p.Configuration == "Debug");
            Assert.Contains(projCfgs, p => p.Configuration == "Release");
            Assert.All(projCfgs, projCfg => Assert.Equal(projPath, projCfg.Path));
            Assert.All(projCfgs, projCfg => Assert.Null(projCfg.Proj.Parent)); // No solution parent
        }
        finally {
            CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnumerateProjCfg_WithValidProjectFileNoDebug_ReturnsOnlyRelease() {
        // Arrange
        var tempDir = CreateTempDirectory();
        var projPath = Path.Combine(tempDir, "Test.csproj");
        
        try {
            await File.WriteAllTextAsync(projPath, CreateSimpleCsprojContent());
            
            var options = new CleaningOptions();
            var errorSink = new ErrorSink(new TestConsoleOutput(_output));
            var enumerator = new Enumerator(options, errorSink);

            // Act - Disable default Debug configuration
            var projCfgs = new List<ProjCfg>();
            await foreach (var projCfg in enumerator.EnumerateProjCfg(projPath, EnumerationType.Project, createDefaultDebugConfiguration: false)) {
                projCfgs.Add(projCfg);
            }

            // Assert
            Assert.Single(projCfgs); // Only Release
            Assert.Equal("Release", projCfgs[0].Configuration);
            Assert.Equal(projPath, projCfgs[0].Path);
        }
        finally {
            CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnumerateProjCfg_WithDirectoryContainingProjects_ReturnsAllProjectConfigurations() {
        // Arrange
        var tempDir = CreateTempDirectory();
        var projPath1 = Path.Combine(tempDir, "Proj1", "Test1.csproj");
        var projPath2 = Path.Combine(tempDir, "Proj2", "Test2.fsproj");
        
        try {
            // Create directory structure and project files
            Directory.CreateDirectory(Path.GetDirectoryName(projPath1)!);
            Directory.CreateDirectory(Path.GetDirectoryName(projPath2)!);
            
            await File.WriteAllTextAsync(projPath1, CreateSimpleCsprojContent());
            await File.WriteAllTextAsync(projPath2, CreateSimpleFsprojContent());
            
            var options = new CleaningOptions();
            var errorSink = new ErrorSink(new TestConsoleOutput(_output));
            var enumerator = new Enumerator(options, errorSink);

            // Act
            var projCfgs = new List<ProjCfg>();
            await foreach (var projCfg in enumerator.EnumerateProjCfg(tempDir, EnumerationType.Project)) {
                projCfgs.Add(projCfg);
            }

            // Assert
            Assert.Equal(4, projCfgs.Count); // 2 projects * 2 configurations each
            
            // Check that we have both projects
            var proj1Cfgs = projCfgs.Where(p => p.Path == projPath1).ToList();
            var proj2Cfgs = projCfgs.Where(p => p.Path == projPath2).ToList();
            
            Assert.Equal(2, proj1Cfgs.Count);
            Assert.Equal(2, proj2Cfgs.Count);
            
            // All should have Debug and Release configurations
            Assert.Contains(proj1Cfgs, p => p.Configuration == "Debug");
            Assert.Contains(proj1Cfgs, p => p.Configuration == "Release");
            Assert.Contains(proj2Cfgs, p => p.Configuration == "Debug");
            Assert.Contains(proj2Cfgs, p => p.Configuration == "Release");
        }
        finally {
            CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnumerateProjCfg_WithVcxprojInSolution_ExtractsPlatformConfigurations() {
        // Arrange
        var tempDir = CreateTempDirectory();
        var slnPath = Path.Combine(tempDir, "test.sln");
        var vcxprojPath = Path.Combine(tempDir, "NativeProject", "NativeProject.vcxproj");
        
        try {
            // Create directory structure
            Directory.CreateDirectory(Path.GetDirectoryName(vcxprojPath)!);
            
            // Create vcxproj file
            await File.WriteAllTextAsync(vcxprojPath, CreateSimpleVcxprojContent());
            
            // Create solution file with platform configurations for vcxproj
            await File.WriteAllTextAsync(slnPath, CreateSolutionWithPlatformConfigurations(vcxprojPath));
            
            var options = new CleaningOptions();
            var errorSink = new ErrorSink(new TestConsoleOutput(_output));
            var enumerator = new Enumerator(options, errorSink);

            // Act
            var projCfgs = new List<ProjCfg>();
            await foreach (var projCfg in enumerator.EnumerateProjCfg(slnPath, EnumerationType.Sln)) {
                projCfgs.Add(projCfg);
            }

            // Assert
            Assert.Equal(4, projCfgs.Count); // Debug|x64, Debug|Win32, Release|x64, Release|Win32
            
            // Check that platform is set for vcxproj
            Assert.All(projCfgs, projCfg => Assert.NotNull(projCfg.Platform));
            Assert.Contains(projCfgs, p => p.Configuration == "Debug" && p.Platform == "x64");
            Assert.Contains(projCfgs, p => p.Configuration == "Debug" && p.Platform == "Win32");
            Assert.Contains(projCfgs, p => p.Configuration == "Release" && p.Platform == "x64");
            Assert.Contains(projCfgs, p => p.Configuration == "Release" && p.Platform == "Win32");
        }
        finally {
            CleanupTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnumerateProjCfg_WithNonExistentPath_ReturnsEmpty() {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var options = new CleaningOptions();
        var errorSink = new ErrorSink(new TestConsoleOutput(_output));
        var enumerator = new Enumerator(options, errorSink);

        // Act
        var projCfgs = new List<ProjCfg>();
        await foreach (var projCfg in enumerator.EnumerateProjCfg(nonExistentPath, EnumerationType.Project)) {
            projCfgs.Add(projCfg);
        }

        // Assert
        Assert.Empty(projCfgs);
    }

    private static string CreateSolutionWithConfigurations(string projPath1, string projPath2) {
        var proj1Guid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
        var proj2Guid = "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}";
        var proj1Id = Guid.NewGuid().ToString().ToUpper();
        var proj2Id = Guid.NewGuid().ToString().ToUpper();
        
        return $@"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{proj1Guid}"") = ""Project1"", ""{projPath1.Replace(Path.DirectorySeparatorChar, '\\')}"", ""{{{proj1Id}}}""
EndProject
Project(""{proj2Guid}"") = ""Project2"", ""{projPath2.Replace(Path.DirectorySeparatorChar, '\\')}"", ""{{{proj2Id}}}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{{{proj1Id}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{{{proj1Id}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{{{proj1Id}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{{{proj1Id}}}.Release|Any CPU.Build.0 = Release|Any CPU
		{{{proj2Id}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{{{proj2Id}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{{{proj2Id}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{{{proj2Id}}}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
EndGlobal";
    }

    private static string CreateSolutionWithPlatformConfigurations(string vcxprojPath) {
        var vcxprojGuid = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
        var projId = Guid.NewGuid().ToString().ToUpper();
        
        return $@"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{vcxprojGuid}"") = ""NativeProject"", ""{vcxprojPath.Replace(Path.DirectorySeparatorChar, '\\')}"", ""{{{projId}}}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|x64 = Debug|x64
		Debug|x86 = Debug|x86
		Release|x64 = Release|x64
		Release|x86 = Release|x86
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{{{projId}}}.Debug|x64.ActiveCfg = Debug|x64
		{{{projId}}}.Debug|x64.Build.0 = Debug|x64
		{{{projId}}}.Debug|x86.ActiveCfg = Debug|Win32
		{{{projId}}}.Debug|x86.Build.0 = Debug|Win32
		{{{projId}}}.Release|x64.ActiveCfg = Release|x64
		{{{projId}}}.Release|x64.Build.0 = Release|x64
		{{{projId}}}.Release|x86.ActiveCfg = Release|Win32
		{{{projId}}}.Release|x86.Build.0 = Release|Win32
	EndGlobalSection
EndGlobal";
    }

    private static string CreateSimpleVcxprojContent() {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup Label="ProjectConfigurations">
                <ProjectConfiguration Include="Debug|Win32">
                  <Configuration>Debug</Configuration>
                  <Platform>Win32</Platform>
                </ProjectConfiguration>
                <ProjectConfiguration Include="Release|Win32">
                  <Configuration>Release</Configuration>
                  <Platform>Win32</Platform>
                </ProjectConfiguration>
                <ProjectConfiguration Include="Debug|x64">
                  <Configuration>Debug</Configuration>
                  <Platform>x64</Platform>
                </ProjectConfiguration>
                <ProjectConfiguration Include="Release|x64">
                  <Configuration>Release</Configuration>
                  <Platform>x64</Platform>
                </ProjectConfiguration>
              </ItemGroup>
              <PropertyGroup Label="Globals">
                <VCProjectVersion>16.0</VCProjectVersion>
                <Keyword>Win32Proj</Keyword>
                <WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>
              </PropertyGroup>
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
            </Project>
            """;
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
        var proj1Guid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"; // C# project GUID
        var proj2Guid = "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}"; // VB project GUID
        var proj1Id = Guid.NewGuid().ToString().ToUpper();
        var proj2Id = Guid.NewGuid().ToString().ToUpper();
        
        return $@"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{proj1Guid}"") = ""Project1"", ""{projPath1.Replace(Path.DirectorySeparatorChar, '\\')}"", ""{{{proj1Id}}}""
EndProject
Project(""{proj2Guid}"") = ""Project2"", ""{projPath2.Replace(Path.DirectorySeparatorChar, '\\')}"", ""{{{proj2Id}}}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{{{proj1Id}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{{{proj1Id}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{{{proj1Id}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{{{proj1Id}}}.Release|Any CPU.Build.0 = Release|Any CPU
		{{{proj2Id}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{{{proj2Id}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{{{proj2Id}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{{{proj2Id}}}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
EndGlobal";
    }
}

internal class TestConsoleOutput(ITestOutputHelper? output) : IConsoleOutput {
    public void WriteError(string message, Exception? exception = default) => output?.WriteLine($"ERROR: {message}");
    public void WriteWarning(string message) => output?.WriteLine($"WARNING: {message}");
    public void WriteInfo(string message) => output?.WriteLine($"INFO: {message}");
    public void WriteVerbose(string message) => output?.WriteLine($"VERBOSE: {message}");
    public void WriteDebug(string message) => output?.WriteLine($"DEBUG: {message}");
    public void WriteRule(string title) => output?.WriteLine($"=== {title} ===");
    public void WriteTable(Table table) => output?.WriteLine("TABLE: " + table.ToString());
    public bool Confirm(string message, bool defaultValue = false) => defaultValue;
    public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull => throw new NotImplementedException();
    public void StartProgress(string description, Action<ProgressContext> action) => action(null!);
    public Task StartProgressAsync(string description, Func<ProgressContext, Task> action) => action(null!);
    public Task StartStatusAsync(string description, Func<StatusContext, Task> action) => action(null!);
    public void WriteException(Exception exception) => output?.WriteLine($"EXCEPTION: {exception.Message}");
    public void WriteOutput(string caption, string content) => output?.WriteLine($"{caption}: {content}");
}