using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace bld.Services;

internal sealed class DepsGraphService(IConsoleOutput _console, CleaningOptions _options) {

    /// <summary>
    /// Builds and analyzes a comprehensive dependency graph from discovered package references
    /// </summary>
    /// <param name="rootPath">Root path to scan for solutions/projects</param>
    /// <param name="includePrerelease">Whether to include prerelease packages</param>
    /// <param name="maxDepth">Maximum depth to traverse dependencies</param>
    /// <param name="showAnalysis">Whether to show detailed analysis</param>
    /// <param name="exportPath">Optional path to export dependency graph data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> BuildDependencyGraphAsync(
        string rootPath,
        bool includePrerelease = false,
        int maxDepth = 8,
        bool showAnalysis = true,
        string? exportPath = null,
        CancellationToken cancellationToken = default) {

        _console.WriteRule("[bold blue]bld dependency-graph (BETA)[/]");
        _console.WriteInfo("Discovering packages and building dependency graph...");

        var stopwatch = Stopwatch.StartNew();

        var discoveryService = new PackageDiscoveryService(_console, _options);
        var (allPackageReferences, projectCount, errorSink) = await discoveryService.DiscoverPackageReferencesAsync(rootPath, cancellationToken);

        if (allPackageReferences.Count == 0) {
            _console.WriteInfo("No package references found.");
            return 0;
        }

        _console.WriteInfo($"Found {allPackageReferences.Count} unique packages across {projectCount} projects");

        // Now build the dependency graph using the new functionality
        try {
            var dependencyGraph = await allPackageReferences.BuildAndShowDependencyGraphAsync(
                _console,
                includePrerelease,
                maxDepth,
                showAnalysis,
                true, // showVulnerabilities
                cancellationToken);

            // Export if requested
            if (!string.IsNullOrEmpty(exportPath)) {
                var format = Path.GetExtension(exportPath).TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(format)) format = "json";

                await dependencyGraph.ExportDependencyGraphAsync(exportPath, format, _console);
            }

            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();

            return 0;
        }
        catch (Exception ex) {
            _console.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Builds and displays a reverse dependency graph from discovered package references
    /// </summary>
    /// <param name="rootPath">Root path to scan for solutions/projects</param>
    /// <param name="includePrerelease">Whether to include prerelease packages</param>
    /// <param name="excludeFrameworkPackages">Whether to exclude Microsoft/System/NETStandard packages</param>
    /// <param name="maxDepth">Maximum depth to traverse dependencies</param>
    /// <param name="exportPath">Optional path to export reverse dependency graph data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> BuildReverseDependencyGraphAsync(
        string rootPath,
        bool includePrerelease = false,
        bool excludeFrameworkPackages = false,
        int maxDepth = 8,
        string? exportPath = null,
        CancellationToken cancellationToken = default) {

        _console.WriteRule("[bold blue]bld reverse-dependency-graph (BETA)[/]");
        _console.WriteInfo("Discovering packages and building reverse dependency graph...");

        var stopwatch = Stopwatch.StartNew();

        var discoveryService = new PackageDiscoveryService(_console, _options);
        var (allPackageReferences, projectCount, errorSink) = await discoveryService.DiscoverPackageReferencesAsync(rootPath, cancellationToken);

        if (allPackageReferences.Count == 0) {
            _console.WriteInfo("No package references found.");
            return 0;
        }

        _console.WriteInfo($"Found {allPackageReferences.Count} unique packages across {projectCount} projects");

        // Now build the reverse dependency graph using the new functionality
        try {
            var reverseAnalysis = await allPackageReferences.BuildAndShowReverseDependencyGraphAsync(
                _console,
                includePrerelease,
                maxDepth,
                excludeFrameworkPackages,
                cancellationToken);

            // Export if requested (would need to implement export for reverse analysis)
            if (!string.IsNullOrEmpty(exportPath)) {
                await ExportReverseAnalysisAsync(reverseAnalysis, exportPath, _console, cancellationToken);
            }

            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();

            return 0;
        }
        catch (Exception ex) {
            _console.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Exports reverse dependency analysis to various formats
    /// </summary>
    private static async Task ExportReverseAnalysisAsync(
        ReverseDependencyAnalysis analysis,
        string outputPath,
        IConsoleOutput console,
        CancellationToken cancellationToken = default) {

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
            Directory.CreateDirectory(directory);
        }

        var format = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(format)) format = "json";

        switch (format) {
            case "json":
                var json = System.Text.Json.JsonSerializer.Serialize(analysis, new System.Text.Json.JsonSerializerOptions {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(outputPath, json, cancellationToken);
                break;

            case "csv":
                var csv = new System.Text.StringBuilder();
                csv.AppendLine("PackageId,Version,TargetFramework,IsExplicit,IsFrameworkPackage,ReferenceCount,DependentPackages");

                foreach (var node in analysis.ReverseNodes.OrderBy(n => n.PackageId)) {
                    var dependentPackageIds = string.Join("|", node.DependentPackages.Select(d => d.PackageId));
                    csv.AppendLine($"{node.PackageId},{node.Version},{node.TargetFramework},{node.IsExplicit},{node.IsFrameworkPackage},{node.ReferenceCount},\"{dependentPackageIds}\"");
                }

                await File.WriteAllTextAsync(outputPath, csv.ToString(), cancellationToken);
                break;

            default:
                throw new ArgumentException($"Unsupported export format: {format}");
        }

        console.WriteInfo($"Reverse dependency analysis exported to: {outputPath}");
    }

}
