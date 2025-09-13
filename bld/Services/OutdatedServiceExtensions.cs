using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using Spectre.Console;

namespace bld.Services;

/// <summary>
/// Extensions for OutdatedService to provide dependency graph functionality
/// </summary>
internal static class OutdatedServiceExtensions {
    
    /// <summary>
    /// Builds and displays a comprehensive dependency graph from discovered packages
    /// </summary>
    /// <param name="allPackageReferences">Package references discovered by OutdatedService</param>
    /// <param name="console">Console output service</param>
    /// <param name="includePrerelease">Whether to include prerelease packages</param>
    /// <param name="maxDepth">Maximum depth to traverse</param>
    /// <param name="showAnalysis">Whether to show detailed analysis</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The built dependency graph</returns>
    public static async Task<PackageDependencyGraph> BuildAndShowDependencyGraphAsync(
        this Dictionary<string, OutdatedService.PackageInfoContainer> allPackageReferences,
        IConsoleOutput console,
        bool includePrerelease = false,
        int maxDepth = 5,
        bool showAnalysis = true,
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
        
        // Display summary table
        DisplayDependencyGraphSummary(dependencyGraph, console);
        
        if (showAnalysis) {
            var analysis = graphService.AnalyzeDependencyGraph(dependencyGraph);
            DisplayDependencyGraphAnalysis(analysis, console);
        }
        
        return dependencyGraph;
    }
    
    /// <summary>
    /// Displays a summary table of the dependency graph
    /// </summary>
    private static void DisplayDependencyGraphSummary(PackageDependencyGraph graph, IConsoleOutput console) {
        var summaryTable = new Table().Border(TableBorder.Rounded);
        summaryTable.AddColumn(new TableColumn("Metric").LeftAligned());
        summaryTable.AddColumn(new TableColumn("Count").RightAligned());
        
        summaryTable.AddRow("Root Packages", graph.RootPackages.Count.ToString());
        summaryTable.AddRow("Total Packages", graph.TotalPackageCount.ToString());
        summaryTable.AddRow("Max Depth", graph.MaxDepth.ToString());
        summaryTable.AddRow("Unresolved", graph.UnresolvedPackages.Count.ToString());
        
        console.WriteTable(summaryTable);
    }
    
    /// <summary>
    /// Displays detailed analysis of the dependency graph
    /// </summary>
    private static void DisplayDependencyGraphAnalysis(DependencyGraphAnalysis analysis, IConsoleOutput console) {
        console.WriteInfo("\n[bold]Dependency Analysis:[/]");
        
        // Package distribution
        console.WriteInfo($"Microsoft packages: {analysis.MicrosoftPackages}");
        console.WriteInfo($"Third-party packages: {analysis.ThirdPartyPackages}");
        
        // Depth distribution
        if (analysis.PackagesByDepth.Any()) {
            console.WriteInfo("\nPackages by depth:");
            foreach (var (depth, count) in analysis.PackagesByDepth.OrderBy(kvp => kvp.Key)) {
                console.WriteInfo($"  Depth {depth}: {count} packages");
            }
        }
        
        // Most common dependencies
        if (analysis.MostCommonDependencies.Any()) {
            console.WriteInfo("\nMost common dependencies:");
            var depTable = new Table().Border(TableBorder.Simple);
            depTable.AddColumn("Package");
            depTable.AddColumn("Used By");
            
            foreach (var dep in analysis.MostCommonDependencies) {
                depTable.AddRow(dep.PackageId, dep.Frequency.ToString());
            }
            console.WriteTable(depTable);
        }
        
        // Version conflicts
        if (analysis.VersionConflicts.Any()) {
            console.WriteWarning($"\n[yellow]Version conflicts detected ({analysis.VersionConflicts.Count} packages):[/]");
            var conflictTable = new Table().Border(TableBorder.Simple);
            conflictTable.AddColumn("Package");
            conflictTable.AddColumn("Versions");
            
            foreach (var conflict in analysis.VersionConflicts.Take(10)) {
                conflictTable.AddRow(
                    conflict.PackageId,
                    string.Join(", ", conflict.Versions)
                );
            }
            console.WriteTable(conflictTable);
            
            if (analysis.VersionConflicts.Count > 10) {
                console.WriteWarning($"... and {analysis.VersionConflicts.Count - 10} more conflicts");
            }
        }
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