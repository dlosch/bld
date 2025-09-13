using bld.Infrastructure;
using bld.Services.NuGet;
using Spectre.Console;

namespace bld.Services;

/// <summary>
/// Visualizer for reverse dependency graphs using Spectre.Console
/// </summary>
internal sealed class ReverseDependencyTreeVisualizer {
    private readonly IConsoleOutput? _console;
    
    public ReverseDependencyTreeVisualizer(IConsoleOutput? console) {
        _console = console; // Allow null for testing
    }
    
    /// <summary>
    /// Displays the reverse dependency analysis with rich visualization
    /// </summary>
    public async Task DisplayReverseDependencyAnalysisAsync(
        ReverseDependencyAnalysis analysis,
        bool excludeFrameworkPackages = false,
        CancellationToken cancellationToken = default) {
        
        ArgumentNullException.ThrowIfNull(analysis);
        
        // Header
        _console?.WriteRule("[bold cyan]Reverse Dependency Analysis[/]");
        _console?.WriteInfo($"Shows which packages depend on each package (reverse of standard dependency tree)");
        
        if (excludeFrameworkPackages) {
            _console?.WriteInfo("[dim]Framework packages (Microsoft.*/System.*/NETStandard.*) are excluded[/]");
        }
        
        // Summary statistics
        DisplaySummaryStatistics(analysis);
        
        // Most referenced packages
        DisplayMostReferencedPackages(analysis);
        
        // Detailed reverse dependency tree
        await DisplayDetailedReverseDependenciesAsync(analysis, cancellationToken);
        
        // Leaf packages (packages with no dependents)
        DisplayLeafPackages(analysis);
        
        // Package categorization
        DisplayPackageCategorization(analysis);
    }
    
    /// <summary>
    /// Displays summary statistics
    /// </summary>
    private void DisplaySummaryStatistics(ReverseDependencyAnalysis analysis) {
        _console?.WriteRule("[bold green]Summary Statistics[/]");
        
        var summaryTable = new Table().Border(TableBorder.Simple);
        summaryTable.AddColumn("Metric");
        summaryTable.AddColumn("Count");
        summaryTable.AddColumn("Percentage");
        
        summaryTable.AddRow("📦 Total Packages", analysis.TotalPackages.ToString(), "100%");
        summaryTable.AddRow("🎯 Explicit References", analysis.ExplicitPackages.ToString(), 
            $"{(analysis.ExplicitPackages * 100.0 / Math.Max(analysis.TotalPackages, 1)):F1}%");
        summaryTable.AddRow("📄 Transitive References", analysis.TransitivePackages.ToString(), 
            $"{(analysis.TransitivePackages * 100.0 / Math.Max(analysis.TotalPackages, 1)):F1}%");
        summaryTable.AddRow("🏢 Framework Packages", analysis.FrameworkPackages.ToString(), 
            $"{(analysis.FrameworkPackages * 100.0 / Math.Max(analysis.TotalPackages, 1)):F1}%");
        
        _console?.WriteTable(summaryTable);
    }
    
    /// <summary>
    /// Displays the most referenced packages
    /// </summary>
    private void DisplayMostReferencedPackages(ReverseDependencyAnalysis analysis) {
        if (!analysis.MostReferencedPackages.Any()) {
            return;
        }
        
        _console?.WriteRule("[bold yellow]Most Referenced Packages[/]");
        _console?.WriteInfo("Packages that are dependencies of the most other packages:");
        
        var refTable = new Table().Border(TableBorder.Simple);
        refTable.AddColumn("Package");
        refTable.AddColumn("Reference Count");
        refTable.AddColumn("Type");
        refTable.AddColumn("Version");
        
        foreach (var package in analysis.MostReferencedPackages) {
            if (package.ReferenceCount == 0) continue;
            
            var typeIcon = package.IsExplicit ? "🎯" : "📄";
            var frameworkIcon = package.IsFrameworkPackage ? "🏢" : "🌐";
            var packageType = package.IsExplicit ? 
                $"{typeIcon} [green]Explicit[/] {frameworkIcon}" : 
                $"{typeIcon} [yellow]Transitive[/] {frameworkIcon}";
            
            refTable.AddRow(
                Markup.Escape(package.PackageId),
                package.ReferenceCount.ToString(),
                packageType,
                Markup.Escape(package.Version)
            );
        }
        
        _console?.WriteTable(refTable);
    }
    
    /// <summary>
    /// Displays detailed reverse dependencies for each package
    /// </summary>
    private async Task DisplayDetailedReverseDependenciesAsync(
        ReverseDependencyAnalysis analysis,
        CancellationToken cancellationToken) {
        
        _console?.WriteRule("[bold blue]Detailed Reverse Dependencies[/]");
        _console?.WriteInfo("For each package, shows which other packages depend on it:");
        
        var packagesWithDependents = analysis.ReverseNodes
            .Where(n => n.DependentPackages.Any())
            .OrderByDescending(n => n.ReferenceCount)
            .ThenBy(n => n.PackageId)
            .ToList();
        
        if (!packagesWithDependents.Any()) {
            _console?.WriteWarning("No packages with dependents found.");
            return;
        }
        
        // Limit display to prevent overwhelming output
        var displayLimit = Math.Min(packagesWithDependents.Count, 20);
        var packagesToShow = packagesWithDependents.Take(displayLimit).ToList();
        
        foreach (var package in packagesToShow) {
            cancellationToken.ThrowIfCancellationRequested();
            
            var tree = new Tree($"🎯 [bold]{Markup.Escape(package.PackageId)}[/] [dim]v{Markup.Escape(package.Version)}[/]");
            tree.Style = package.IsExplicit ? Style.Parse("green") : Style.Parse("yellow");
            
            // Add package info
            var infoNode = tree.AddNode($"📊 [bold]Referenced by {package.ReferenceCount} package(s)[/]");
            
            // Add type info
            var typeInfo = package.IsExplicit ? "🎯 Explicit reference" : "📄 Transitive dependency";
            if (package.IsFrameworkPackage) {
                typeInfo += " 🏢 Framework package";
            }
            infoNode.AddNode(typeInfo);
            
            // Add dependent packages
            if (package.DependentPackages.Any()) {
                var dependentsNode = tree.AddNode($"📦 [bold]Dependent Packages[/]");
                
                var groupedDependents = package.DependentPackages
                    .GroupBy(d => d.PackageId)
                    .OrderBy(g => g.Key)
                    .ToList();
                
                foreach (var dependentGroup in groupedDependents) {
                    var dependent = dependentGroup.First();
                    var icon = dependent.IsRootPackage ? "🎯" : "📄";
                    var color = dependent.IsRootPackage ? "green" : "yellow";
                    
                    dependentsNode.AddNode($"{icon} [{color}]{Markup.Escape(dependent.PackageId)}[/] [dim]v{Markup.Escape(dependent.Version)}[/]");
                }
            }
            
            // Add dependency paths (limited to avoid overwhelming output)
            if (package.DependencyPaths.Any()) {
                var pathsNode = tree.AddNode($"🛤️  [bold]Dependency Paths[/] [dim](showing up to 5)[/]");
                
                foreach (var path in package.DependencyPaths.Take(5)) {
                    pathsNode.AddNode($"[dim]{Markup.Escape(path)}[/]");
                }
                
                if (package.DependencyPaths.Count > 5) {
                    pathsNode.AddNode($"[dim]... and {package.DependencyPaths.Count - 5} more path(s)[/]");
                }
            }
            
            AnsiConsole.Write(tree);
            _console?.WriteInfo(""); // Add spacing
            
            // Add a small delay to allow for cancellation
            await Task.Delay(1, cancellationToken);
        }
        
        if (packagesWithDependents.Count > displayLimit) {
            _console?.WriteInfo($"[dim]... and {packagesWithDependents.Count - displayLimit} more package(s) with dependents[/]");
        }
    }
    
    /// <summary>
    /// Displays leaf packages (packages with no dependents)
    /// </summary>
    private void DisplayLeafPackages(ReverseDependencyAnalysis analysis) {
        if (!analysis.LeafPackages.Any()) {
            return;
        }
        
        _console?.WriteRule("[bold magenta]Leaf Packages[/]");
        _console?.WriteInfo("Packages that have no other packages depending on them:");
        
        var leafTable = new Table().Border(TableBorder.Simple);
        leafTable.AddColumn("Package");
        leafTable.AddColumn("Version");
        leafTable.AddColumn("Type");
        leafTable.AddColumn("Framework");
        
        var leafPackagesToShow = analysis.LeafPackages.Take(15).ToList();
        
        foreach (var leafPackage in leafPackagesToShow) {
            var typeIcon = leafPackage.IsExplicit ? "🎯" : "📄";
            var frameworkIcon = leafPackage.IsFrameworkPackage ? "🏢" : "🌐";
            var packageType = leafPackage.IsExplicit ? 
                $"{typeIcon} [green]Explicit[/] {frameworkIcon}" : 
                $"{typeIcon} [yellow]Transitive[/] {frameworkIcon}";
            
            leafTable.AddRow(
                Markup.Escape(leafPackage.PackageId),
                Markup.Escape(leafPackage.Version),
                packageType,
                Markup.Escape(leafPackage.TargetFramework)
            );
        }
        
        _console?.WriteTable(leafTable);
        
        if (analysis.LeafPackages.Count > 15) {
            _console?.WriteInfo($"[dim]... and {analysis.LeafPackages.Count - 15} more leaf package(s)[/]");
        }
    }
    
    /// <summary>
    /// Displays package categorization breakdown
    /// </summary>
    private void DisplayPackageCategorization(ReverseDependencyAnalysis analysis) {
        _console?.WriteRule("[bold cyan]Package Categorization[/]");
        
        var categories = analysis.ReverseNodes
            .GroupBy(n => CategorizePackage(n.PackageId))
            .OrderByDescending(g => g.Count())
            .ToList();
        
        var categoryTable = new Table().Border(TableBorder.Simple);
        categoryTable.AddColumn("Category");
        categoryTable.AddColumn("Package Count");
        categoryTable.AddColumn("Avg. Reference Count");
        categoryTable.AddColumn("Examples");
        
        foreach (var category in categories) {
            var avgRefs = category.Average(p => p.ReferenceCount);
            var examples = string.Join(", ", category
                .OrderByDescending(p => p.ReferenceCount)
                .Take(3)
                .Select(p => p.PackageId));
            
            categoryTable.AddRow(
                category.Key,
                category.Count().ToString(),
                avgRefs.ToString("F1"),
                Markup.Escape(examples)
            );
        }
        
        _console?.WriteTable(categoryTable);
    }
    
    /// <summary>
    /// Categorizes a package based on its ID
    /// </summary>
    private static string CategorizePackage(string packageId) {
        var lowerId = packageId.ToLowerInvariant();
        return lowerId switch {
            var p when p.StartsWith("microsoft.") => "🏢 [blue]Microsoft[/]",
            var p when p.StartsWith("system.") => "🏢 [blue]System[/]",
            var p when p.StartsWith("netstandard.") => "🏢 [blue]NETStandard[/]",
            var p when p.StartsWith("runtime.") => "🏢 [blue]Runtime[/]",
            var p when p.Contains("newtonsoft") => "📦 [green]JSON/Serialization[/]",
            var p when p.Contains("logging") => "📝 [cyan]Logging[/]",
            var p when p.Contains("test") => "🧪 [yellow]Testing[/]",
            var p when p.Contains("entity") || p.Contains("ef") => "🗃️ [purple]Data/ORM[/]",
            var p when p.Contains("http") || p.Contains("web") => "🌐 [orange3]HTTP/Web[/]",
            var p when p.Contains("azure") => "☁️ [blue]Azure[/]",
            var p when p.Contains("aspnet") => "🌐 [red]ASP.NET[/]",
            _ => "📦 [dim]Third-party[/]"
        };
    }
}