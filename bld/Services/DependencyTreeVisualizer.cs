using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using Spectre.Console;
using NuGet.Versioning;

namespace bld.Services;

/// <summary>
/// Service for creating enhanced tree visualizations of dependency graphs using Spectre.Console
/// </summary>
internal class DependencyTreeVisualizer {
    private readonly IConsoleOutput _console;
    private readonly VulnerabilityService _vulnerabilityService;
    
    public DependencyTreeVisualizer(IConsoleOutput console, VulnerabilityService vulnerabilityService) {
        _console = console;
        _vulnerabilityService = vulnerabilityService;
    }
    
    /// <summary>
    /// Creates and displays an enhanced dependency tree visualization
    /// </summary>
    public async Task DisplayDependencyTreeAsync(
        PackageDependencyGraph graph, 
        EnhancedDependencyAnalysis analysis,
        bool showVulnerabilities = true,
        CancellationToken cancellationToken = default) {
        
        _console.WriteRule("[bold blue]Dependency Tree Structure[/]");
        
        // Get vulnerability data if requested
        Dictionary<string, List<PackageVulnerability>>? vulnerabilities = null;
        if (showVulnerabilities) {
            var allPackageIds = graph.AllPackages.Select(p => p.PackageId).Distinct();
            vulnerabilities = await _vulnerabilityService.GetVulnerabilitiesAsync(allPackageIds, cancellationToken);
        }
        
        // Create enhanced package references
        var enhancedPackages = CreateEnhancedPackageReferences(graph, analysis, vulnerabilities);
        
        // Display summary first
        DisplaySummaryPanel(analysis);
        
        // Display tree for each root package
        foreach (var rootPackage in graph.RootPackages) {
            var tree = CreatePackageTree(rootPackage, enhancedPackages, vulnerabilities);
            _console.WriteTable(CreateTreeTable(tree));
            AnsiConsole.WriteLine();
        }
        
        // Display conflicts and vulnerabilities summary
        if (analysis.VersionConflicts.Any() || analysis.VersionIncompatibilities.Any()) {
            DisplayConflictsPanel(analysis);
        }
        
        if (showVulnerabilities && vulnerabilities?.Values.SelectMany(v => v).Any() == true) {
            DisplayVulnerabilitiesPanel(vulnerabilities);
        }
    }
    
    private Dictionary<string, EnhancedPackageReference> CreateEnhancedPackageReferences(
        PackageDependencyGraph graph,
        EnhancedDependencyAnalysis analysis,
        Dictionary<string, List<PackageVulnerability>>? vulnerabilities) {
        
        var result = new Dictionary<string, EnhancedPackageReference>();
        
        // Create lookup for conflicts
        var conflictLookup = analysis.VersionConflicts
            .ToDictionary(c => c.PackageId, c => c.Versions.ToList(), StringComparer.OrdinalIgnoreCase);
        
        foreach (var package in graph.AllPackages) {
            var packageVulns = vulnerabilities?.GetValueOrDefault(package.PackageId, []) ?? [];
            var conflictingVersions = conflictLookup.GetValueOrDefault(package.PackageId, []);
            
            var enhanced = new EnhancedPackageReference {
                PackageId = package.PackageId,
                Version = package.Version,
                TargetFramework = package.TargetFramework,
                IsPrerelease = package.IsPrerelease,
                IsRootPackage = package.IsRootPackage,
                IsExplicit = package.IsRootPackage, // Root packages are explicit
                Depth = package.Depth,
                VersionRange = package.VersionRange,
                RetrievedAt = package.RetrievedAt,
                Vulnerabilities = packageVulns,
                ConflictingVersions = conflictingVersions
            };
            
            result[GetPackageKey(package)] = enhanced;
        }
        
        return result;
    }
    
    private string GetPackageKey(PackageReference package) {
        return $"{package.PackageId}:{package.Version}:{package.TargetFramework}";
    }
    
    private Tree CreatePackageTree(
        DependencyGraphNode rootPackage, 
        Dictionary<string, EnhancedPackageReference> enhancedPackages,
        Dictionary<string, List<PackageVulnerability>>? vulnerabilities) {
        
        var rootKey = $"{rootPackage.PackageId}:{rootPackage.Version}:{rootPackage.TargetFramework}";
        var rootEnhanced = enhancedPackages.GetValueOrDefault(rootKey);
        
        var tree = new Tree(CreatePackageNodeText(rootPackage, rootEnhanced, true));
        
        AddChildrenToTree(tree, rootPackage, enhancedPackages, vulnerabilities);
        
        return tree;
    }
    
    private void AddChildrenToTree(
        IHasTreeNodes parent,
        DependencyGraphNode node,
        Dictionary<string, EnhancedPackageReference> enhancedPackages,
        Dictionary<string, List<PackageVulnerability>>? vulnerabilities) {
        
        foreach (var child in node.Dependencies.OrderBy(d => d.PackageId)) {
            var childKey = $"{child.PackageId}:{child.Version}:{child.TargetFramework}";
            var childEnhanced = enhancedPackages.GetValueOrDefault(childKey);
            
            var childNode = parent.AddNode(CreatePackageNodeText(child, childEnhanced, false));
            
            // Recursively add children (with depth limit to prevent cycles)
            if (child.Depth < 10 && child.Dependencies.Any()) {
                AddChildrenToTree(childNode, child, enhancedPackages, vulnerabilities);
            }
        }
    }
    
    private string CreatePackageNodeText(
        DependencyGraphNode node, 
        EnhancedPackageReference? enhanced, 
        bool isRoot) {
        
        var text = $"[bold]{Markup.Escape(node.PackageId)}[/] [dim]v{Markup.Escape(node.Version)}[/]";
        
        // Add explicit/transitive marker
        if (isRoot) {
            text = $"[green]📦 {text} (explicit)[/]";
        } else {
            text = $"[yellow]📄 {text} (transitive)[/]";
        }
        
        // Add version range if available
        if (!string.IsNullOrEmpty(node.VersionRange)) {
            text += $" [dim]({Markup.Escape(node.VersionRange)})[/]";
        }
        
        // Add framework
        text += $" [cyan]{Markup.Escape(node.TargetFramework)}[/]";
        
        // Add warnings/issues
        var issues = new List<string>();
        
        if (enhanced?.HasVulnerabilities == true) {
            var highSeverity = enhanced.Vulnerabilities.Any(v => 
                v.Severity.Equals("High", StringComparison.OrdinalIgnoreCase) ||
                v.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
            
            if (highSeverity) {
                issues.Add("[red]🚨 HIGH VULNERABILITY[/]");
            } else {
                issues.Add("[yellow]⚠️ vulnerability[/]");
            }
        }
        
        if (enhanced?.HasVersionConflicts == true) {
            issues.Add("[orange3]⚡ version conflict[/]");
        }
        
        if (node.IsPrerelease) {
            issues.Add("[purple]🧪 prerelease[/]");
        }
        
        if (issues.Any()) {
            text += $" {string.Join(" ", issues)}";
        }
        
        return text;
    }
    
    private Table CreateTreeTable(Tree tree) {
        var table = new Table()
            .Border(TableBorder.None)
            .AddColumn(new TableColumn("Dependency Tree").NoWrap());
        
        table.AddRow(tree);
        return table;
    }
    
    private void DisplaySummaryPanel(EnhancedDependencyAnalysis analysis) {
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold blue]Dependency Summary[/]");
        
        summaryTable.AddColumn(new TableColumn("Metric").LeftAligned());
        summaryTable.AddColumn(new TableColumn("Count").RightAligned());
        
        summaryTable.AddRow("📦 Explicit Packages", analysis.ExplicitPackages.ToString());
        summaryTable.AddRow("📄 Transitive Packages", analysis.TransitivePackages.ToString());
        summaryTable.AddRow("📊 Total Packages", analysis.TotalPackages.ToString());
        summaryTable.AddRow("📏 Maximum Depth", analysis.MaxDepth.ToString());
        summaryTable.AddRow("🏢 Microsoft Packages", analysis.MicrosoftPackages.ToString());
        summaryTable.AddRow("🌐 Third-party Packages", analysis.ThirdPartyPackages.ToString());
        
        if (analysis.VulnerablePackages > 0) {
            summaryTable.AddRow("[red]🚨 Vulnerable Packages[/]", $"[red]{analysis.VulnerablePackages}[/]");
        }
        
        if (analysis.VersionConflicts.Any()) {
            summaryTable.AddRow("[orange3]⚡ Version Conflicts[/]", $"[orange3]{analysis.VersionConflicts.Count}[/]");
        }
        
        if (analysis.UnresolvedPackages > 0) {
            summaryTable.AddRow("[yellow]❌ Unresolved[/]", $"[yellow]{analysis.UnresolvedPackages}[/]");
        }
        
        _console.WriteTable(summaryTable);
        AnsiConsole.WriteLine();
    }
    
    private void DisplayConflictsPanel(EnhancedDependencyAnalysis analysis) {
        _console.WriteRule("[bold orange3]Version Conflicts & Incompatibilities[/]");
        
        if (analysis.VersionConflicts.Any()) {
            var conflictsTable = new Table()
                .Border(TableBorder.Simple)
                .Title("[orange3]Version Conflicts[/]");
            
            conflictsTable.AddColumn("Package");
            conflictsTable.AddColumn("Conflicting Versions");
            conflictsTable.AddColumn("Impact");
            
            foreach (var conflict in analysis.VersionConflicts) {
                var impact = AssessConflictImpact(conflict.Versions);
                conflictsTable.AddRow(
                    Markup.Escape(conflict.PackageId),
                    string.Join(", ", conflict.Versions.Select(v => Markup.Escape(v))),
                    impact
                );
            }
            
            _console.WriteTable(conflictsTable);
            AnsiConsole.WriteLine();
        }
        
        if (analysis.VersionIncompatibilities.Any()) {
            var incompatTable = new Table()
                .Border(TableBorder.Simple)
                .Title("[red]Version Incompatibilities[/]");
            
            incompatTable.AddColumn("Package");
            incompatTable.AddColumn("Incompatible Versions");
            incompatTable.AddColumn("Reason");
            
            foreach (var incompatibility in analysis.VersionIncompatibilities) {
                incompatTable.AddRow(
                    Markup.Escape(incompatibility.PackageId),
                    string.Join(", ", incompatibility.IncompatibleVersions.Select(v => Markup.Escape(v))),
                    Markup.Escape(incompatibility.Reason)
                );
            }
            
            _console.WriteTable(incompatTable);
        }
    }
    
    private string AssessConflictImpact(IReadOnlyList<string> versions) {
        if (versions.Count == 2 && 
            NuGetVersion.TryParse(versions[0], out var v1) && 
            NuGetVersion.TryParse(versions[1], out var v2)) {
            
            var majorDiff = Math.Abs(v1.Major - v2.Major);
            var minorDiff = Math.Abs(v1.Minor - v2.Minor);
            
            if (majorDiff > 0) {
                return "[red]⚠️ Major version difference - likely breaking[/]";
            } else if (minorDiff > 0) {
                return "[yellow]⚠️ Minor version difference - may have issues[/]";
            } else {
                return "[green]✅ Patch difference - likely safe[/]";
            }
        }
        
        return "[yellow]⚠️ Multiple versions - requires review[/]";
    }
    
    private void DisplayVulnerabilitiesPanel(Dictionary<string, List<PackageVulnerability>> vulnerabilities) {
        _console.WriteRule("[bold red]Security Vulnerabilities[/]");
        
        var vulnTable = new Table()
            .Border(TableBorder.Heavy)
            .Title("[red]Vulnerable Packages[/]");
        
        vulnTable.AddColumn("Package");
        vulnTable.AddColumn("Severity");
        vulnTable.AddColumn("Affected Versions");
        vulnTable.AddColumn("Title");
        vulnTable.AddColumn("CVSS");
        
        foreach (var (packageId, packageVulns) in vulnerabilities.Where(kvp => kvp.Value.Any())) {
            foreach (var vuln in packageVulns.OrderByDescending(v => v.Severity)) {
                var severityColor = vuln.Severity.ToLowerInvariant() switch {
                    "critical" => "red",
                    "high" => "red",
                    "medium" => "yellow",
                    "low" => "green",
                    _ => "white"
                };
                
                vulnTable.AddRow(
                    Markup.Escape(packageId),
                    $"[{severityColor}]{Markup.Escape(vuln.Severity)}[/]",
                    Markup.Escape(vuln.AffectedVersionRange),
                    Markup.Escape(vuln.Title),
                    string.IsNullOrEmpty(vuln.CvssScore) ? "-" : Markup.Escape(vuln.CvssScore)
                );
            }
        }
        
        _console.WriteTable(vulnTable);
        
        AnsiConsole.MarkupLine("[dim]💡 Tip: Use 'dotnet list package --vulnerable' for more vulnerability details[/]");
    }
}