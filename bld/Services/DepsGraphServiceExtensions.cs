using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using Spectre.Console;

namespace bld.Services;

/// <summary>
/// Extensions for OutdatedService to provide dependency graph functionality
/// </summary>
internal static class DepsGraphServiceExtensions {
    
    /// <summary>
    /// Builds and displays a comprehensive dependency graph from discovered packages
    /// </summary>
    /// <param name="allPackageReferences">Package references discovered by OutdatedService</param>
    /// <param name="console">Console output service</param>
    /// <param name="includePrerelease">Whether to include prerelease packages</param>
    /// <param name="maxDepth">Maximum depth to traverse</param>
    /// <param name="showAnalysis">Whether to show detailed analysis</param>
    /// <param name="showVulnerabilities">Whether to check and display vulnerability information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The built dependency graph</returns>
    public static async Task<PackageDependencyGraph> BuildAndShowDependencyGraphAsync(
        this Dictionary<string, PackageInfoContainer> allPackageReferences,
        IConsoleOutput console,
        bool includePrerelease = false,
        int maxDepth = 8,
        bool showAnalysis = true,
        bool showVulnerabilities = true,
        CancellationToken cancellationToken = default) {
        
        ArgumentNullException.ThrowIfNull(allPackageReferences);
        ArgumentNullException.ThrowIfNull(console);
        
        console.WriteRule("[bold blue]Dependency Graph Analysis[/]");
        
        var graphService = new DependencyGraphService(console);
        var dependencyGraph = await graphService.BuildDependencyGraphAsync(
            allPackageReferences, 
            includePrerelease, 
            maxDepth, 
            cancellationToken);
        
        // Get vulnerability information if requested
        Dictionary<string, List<PackageVulnerability>>? vulnerabilities = null;
        if (showVulnerabilities) {
            using var httpClient = new HttpClient();
            var vulnerabilityService = new VulnerabilityService(httpClient, console);
            var packageIds = dependencyGraph.AllPackages.Select(p => p.PackageId).Distinct();
            vulnerabilities = await vulnerabilityService.GetVulnerabilitiesAsync(packageIds, cancellationToken);
        }
        
        // Perform enhanced analysis
        var enhancedAnalysis = await graphService.AnalyzeDependencyGraphEnhancedAsync(
            dependencyGraph, 
            vulnerabilities, 
            cancellationToken);
        
        // Create and display enhanced tree visualization
        using var httpClient2 = new HttpClient();
        var vulnerabilityService2 = new VulnerabilityService(httpClient2, console);
        var treeVisualizer = new DependencyTreeVisualizer(console, vulnerabilityService2);
        
        await treeVisualizer.DisplayDependencyTreeAsync(
            dependencyGraph, 
            enhancedAnalysis, 
            showVulnerabilities, 
            cancellationToken);
        
        // Show legacy summary if requested - disabled per user feedback for cleaner output
        // if (showAnalysis) {
        //     DisplayLegacySummary(enhancedAnalysis, console);
        // }
        
        return dependencyGraph;
    }
    
    /// <summary>
    /// Builds and displays a reverse dependency graph from discovered packages
    /// </summary>
    /// <param name="allPackageReferences">Package references discovered by OutdatedService</param>
    /// <param name="console">Console output service</param>
    /// <param name="includePrerelease">Whether to include prerelease packages</param>
    /// <param name="maxDepth">Maximum depth to traverse</param>
    /// <param name="excludeFrameworkPackages">Whether to exclude Microsoft/System/NETStandard packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The reverse dependency analysis</returns>
    public static async Task<ReverseDependencyAnalysis> BuildAndShowReverseDependencyGraphAsync(
        this Dictionary<string, PackageInfoContainer> allPackageReferences,
        IConsoleOutput console,
        bool includePrerelease = false,
        int maxDepth = 8,
        bool excludeFrameworkPackages = false,
        CancellationToken cancellationToken = default) {
        
        ArgumentNullException.ThrowIfNull(allPackageReferences);
        ArgumentNullException.ThrowIfNull(console);
        
        // First build the forward dependency graph
        var forwardGraph = await BuildAndShowDependencyGraphAsync(
            allPackageReferences, 
            console, 
            includePrerelease, 
            maxDepth, 
            showAnalysis: false, // Don't show analysis for forward graph
            showVulnerabilities: false, // Don't show vulnerabilities for forward graph
            cancellationToken);
        
        // Build reverse dependency graph
        var reverseService = new ReverseDependencyGraphService(console);
        var reverseAnalysis = reverseService.BuildReverseDependencyGraph(forwardGraph, excludeFrameworkPackages);
        
        // Display reverse dependency visualization
        var reverseVisualizer = new ReverseDependencyTreeVisualizer(console);
        await reverseVisualizer.DisplayReverseDependencyAnalysisAsync(
            reverseAnalysis, 
            excludeFrameworkPackages, 
            cancellationToken);
        
        return reverseAnalysis;
    }
    
    /// <summary>
    /// Displays a legacy summary for backward compatibility
    /// </summary>
    private static void DisplayLegacySummary(EnhancedDependencyAnalysis analysis, IConsoleOutput console) {
        console.WriteRule("[bold green]Additional Analysis Details[/]");
        
        // Depth distribution
        if (analysis.PackagesByDepth.Any()) {
            console.WriteInfo("\n[bold]Package Distribution by Depth:[/]");
            var depthTable = new Table().Border(TableBorder.Simple);
            depthTable.AddColumn("Depth");
            depthTable.AddColumn("Package Count");
            depthTable.AddColumn("Percentage");
            
            foreach (var (depth, count) in analysis.PackagesByDepth.OrderBy(kvp => kvp.Key)) {
                var percentage = (count * 100.0 / analysis.TotalPackages).ToString("F1");
                depthTable.AddRow(
                    depth.ToString(), 
                    count.ToString(),
                    $"{percentage}%"
                );
            }
            console.WriteTable(depthTable);
        }
        
        // Most common dependencies
        if (analysis.MostCommonDependencies.Any()) {
            console.WriteInfo("\n[bold]Most Common Transitive Dependencies:[/]");
            var depTable = new Table().Border(TableBorder.Simple);
            depTable.AddColumn("Package");
            depTable.AddColumn("Used By # Projects");
            depTable.AddColumn("Category");
            
            foreach (var dep in analysis.MostCommonDependencies) {
                var category = CategorizePackage(dep.PackageId);
                depTable.AddRow(
                    Markup.Escape(dep.PackageId), 
                    dep.Frequency.ToString(),
                    category
                );
            }
            console.WriteTable(depTable);
        }
    }
    
    private static string CategorizePackage(string packageId) {
        return packageId.ToLowerInvariant() switch {
            var p when p.StartsWith("microsoft.") => "[blue]Microsoft[/]",
            var p when p.StartsWith("system.") => "[blue]System[/]",
            var p when p.StartsWith("newtonsoft.") => "[green]JSON/Serialization[/]",
            var p when p.Contains("logging") => "[cyan]Logging[/]",
            var p when p.Contains("test") => "[yellow]Testing[/]",
            var p when p.Contains("entity") => "[purple]Data/ORM[/]",
            var p when p.Contains("http") => "[orange3]HTTP/Web[/]",
            _ => "[dim]Third-party[/]"
        };
    }
    
    /// <summary>
    /// Exports the dependency graph to various formats
    /// </summary>
    /// <param name="graph">The dependency graph to export</param>
    /// <param name="outputPath">Output file path</param>
    /// <param name="format">Export format (json, csv, dot)</param>
    /// <param name="console">Console output service</param>
    public static async Task ExportDependencyGraphAsync(
        this PackageDependencyGraph graph,
        string outputPath,
        string format = "json",
        IConsoleOutput? console = null) {
        
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
            Directory.CreateDirectory(directory);
        }
        
        switch (format.ToLowerInvariant()) {
            case "json":
                await ExportToJsonAsync(graph, outputPath);
                break;
            case "csv":
                await ExportToCsvAsync(graph, outputPath);
                break;
            case "dot":
                await ExportToDotAsync(graph, outputPath);
                break;
            default:
                throw new ArgumentException($"Unsupported export format: {format}");
        }
        
        console?.WriteInfo($"Dependency graph exported to: {outputPath}");
    }
    
    private static async Task ExportToJsonAsync(PackageDependencyGraph graph, string outputPath) {
        var json = System.Text.Json.JsonSerializer.Serialize(graph, new System.Text.Json.JsonSerializerOptions {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json);
    }
    
    private static async Task ExportToCsvAsync(PackageDependencyGraph graph, string outputPath) {
        var csv = new System.Text.StringBuilder();
        csv.AppendLine("PackageId,Version,TargetFramework,IsRootPackage,Depth,IsPrerelease,VersionRange");
        
        foreach (var package in graph.AllPackages.OrderBy(p => p.PackageId).ThenBy(p => p.Depth)) {
            csv.AppendLine($"{package.PackageId},{package.Version},{package.TargetFramework},{package.IsRootPackage},{package.Depth},{package.IsPrerelease},\"{package.VersionRange}\"");
        }
        
        await File.WriteAllTextAsync(outputPath, csv.ToString());
    }
    
    private static async Task ExportToDotAsync(PackageDependencyGraph graph, string outputPath) {
        var dot = new System.Text.StringBuilder();
        dot.AppendLine("digraph DependencyGraph {");
        dot.AppendLine("  rankdir=TB;");
        dot.AppendLine("  node [shape=box];");
        
        // Add nodes
        foreach (var package in graph.AllPackages) {
            var style = package.IsRootPackage ? "filled,bold" : "filled";
            var color = package.IsRootPackage ? "lightblue" : "lightgray";
            dot.AppendLine($"  \"{package.PackageId}\" [style=\"{style}\", fillcolor=\"{color}\"];");
        }
        
        // Add edges (this is simplified - would need to reconstruct relationships)
        foreach (var rootPackage in graph.RootPackages) {
            AddDotEdges(dot, rootPackage);
        }
        
        dot.AppendLine("}");
        await File.WriteAllTextAsync(outputPath, dot.ToString());
    }
    
    private static void AddDotEdges(System.Text.StringBuilder dot, DependencyGraphNode node) {
        foreach (var dependency in node.Dependencies) {
            dot.AppendLine($"  \"{node.PackageId}\" -> \"{dependency.PackageId}\";");
            AddDotEdges(dot, dependency);
        }
    }
}