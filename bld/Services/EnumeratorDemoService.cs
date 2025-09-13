using bld.Infrastructure;
using bld.Models;

namespace bld.Services;

/// <summary>
/// Demonstration service showing the new Enumerator functionality
/// </summary>
internal class EnumeratorDemoService(IConsoleOutput console, CleaningOptions options) {

    /// <summary>
    /// Demonstrates enumeration of solution files and their projects
    /// </summary>
    public async Task EnumerateSolutionProjectsAsync(string rootPath) {
        var errorSink = new ErrorSink(console);
        var enumerator = new Enumerator(options, errorSink);
        
        console.WriteInfo($"Enumerating projects from solutions in: {rootPath}");
        
        var projectCount = 0;
        await foreach (var projectPath in enumerator.EnumerateProjectPaths(rootPath, EnumerationType.Sln)) {
            projectCount++;
            console.WriteInfo($"  {projectCount}. {projectPath}");
        }
        
        console.WriteInfo($"Found {projectCount} projects from solution files");
    }

    /// <summary>
    /// Demonstrates enumeration of project files directly
    /// </summary>
    public async Task EnumerateProjectFilesAsync(string rootPath) {
        var errorSink = new ErrorSink(console);
        var enumerator = new Enumerator(options, errorSink);
        
        console.WriteInfo($"Enumerating project files directly in: {rootPath}");
        
        var projectCount = 0;
        await foreach (var projectPath in enumerator.EnumerateProjectPaths(rootPath, EnumerationType.Project)) {
            projectCount++;
            console.WriteInfo($"  {projectCount}. {projectPath}");
        }
        
        console.WriteInfo($"Found {projectCount} project files");
    }

    /// <summary>
    /// Compares the two enumeration approaches
    /// </summary>
    public async Task CompareEnumerationApproachesAsync(string rootPath) {
        console.WriteRule("Enumerator Comparison Demo");
        
        console.WriteInfo("1. Projects from Solutions (.sln, .slnx, .slnf):");
        await EnumerateSolutionProjectsAsync(rootPath);
        
        console.WriteInfo("");
        console.WriteInfo("2. Projects from Direct File Search (.csproj, .vbproj, .sqlproj, etc.):");
        await EnumerateProjectFilesAsync(rootPath);
        
        console.WriteRule("Demo Complete");
    }
}