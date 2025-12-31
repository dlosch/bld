using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace bld.Services;

/// <summary>
/// Application for analyzing NuGet package references
/// </summary>
public class NugetAnalysisApplication {
    private readonly IConsoleOutput _console;
    private readonly List<Err> _errors = new();
    private bool _isInitialized = false;

    internal NugetAnalysisApplication(IConsoleOutput console) {
        _console = console;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal Task InitAsync(CleaningOptions options) {
        // Register MSBuild defaults before any MSBuild types are loaded
        MSBuildService.RegisterMSBuildDefaults(_console, options);
        _isInitialized = true;
        return Task.CompletedTask;
    }

    public class NugetAnalysisResult {
        public List<ProjectNugetAnalysis> Projects { get; set; } = new();
        public bool Aggregated { get; set; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal async Task<NugetAnalysisResult> RunAsync(string[] rootPaths, CleaningOptions options, string? whitelistBlacklistFile, bool aggregate = false, bool showProjects = true) {
        if (!_isInitialized) {
            throw new InvalidOperationException("Application not initialized. Call InitAsync first.");
        }

        var result = new NugetAnalysisResult {
            Aggregated = aggregate
        };

        using var msbuildService = new MSBuildService(_console);
        var errorSink = new ErrorSink(_console);
        var scanner = new SlnScanner(options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);
        var fileSystem = new FileSystem(_console, errorSink);

        // Parse whitelist/blacklist file if provided
        WhitelistBlacklistRules? whitelistBlacklistRules = null;
        if (!string.IsNullOrWhiteSpace(whitelistBlacklistFile)) {
            try {
                whitelistBlacklistRules = WhitelistBlacklistParser.ParseFile(whitelistBlacklistFile);
                _console.WriteInfo($"Loaded whitelist/blacklist rules from: {whitelistBlacklistFile}");
                _console.WriteDebug($"Whitelist patterns: {whitelistBlacklistRules.WhitelistPatterns.Count}");
                _console.WriteDebug($"Blacklist patterns: {whitelistBlacklistRules.BlacklistPatterns.Count}");
                _console.WriteDebug($"Microsoft patterns: {whitelistBlacklistRules.MicrosoftPatterns.Count}");
                _console.WriteDebug($"Trusted patterns: {whitelistBlacklistRules.TrustedPatterns.Count}");
            }
            catch (Exception ex) {
                _console.WriteError($"Failed to parse whitelist/blacklist file: {ex.Message}");
                return result;
            }
        }

        var categorizer = new NugetPackageCategorizer(whitelistBlacklistRules);
        var packageExtractor = new NugetPackageExtractor(_console, errorSink, categorizer);

        _console.WriteRule("[bold blue]NuGet Package Analysis[/]");

        var stopwatch = Stopwatch.StartNew();

        try {
            var allProjectAnalyses = new List<ProjectNugetAnalysis>();

            foreach (var rootPath in rootPaths) {
                await foreach (var sln in scanner.Enumerate(rootPath)) {
                    _console.WriteDebug($"Processing solution: {sln}");

                    await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem)) {
                        try {
                            var globalProperties = GetGlobalProperties(options);
                            var analysis = packageExtractor.AnalyzeProject(projCfg, globalProperties);

                            if (analysis.Packages.Any()) {
                                allProjectAnalyses.Add(analysis);
                            }
                        }
                        catch (Exception ex) {
                            _console.WriteError($"Failed to analyze project {projCfg.Path}: {ex.Message}");
                        }
                    }
                }
            }

            // Display results with unique projects only (deduplicate by path)
            var uniqueAnalyses = allProjectAnalyses
                .GroupBy(a => a.ProjectPath)
                .Select(g => g.First())
                .ToList();

            result.Projects = uniqueAnalyses;

            if (aggregate) {
                DisplayAggregateResults(uniqueAnalyses, categorizer, showProjects);
            }
            else {
                DisplayResults(uniqueAnalyses, categorizer);
            }

        }
        finally {
            stopwatch.Stop();
            _console.WriteInfo($"Analysis completed in {stopwatch.Elapsed:mm\\:ss\\.fff}");

            if (_errors.Count > 0) {
                _console.WriteError($"Analysis completed with {_errors.Count} error(s).");
            }
        }

        return result;
    }

    private static Dictionary<string, string> GetGlobalProperties(CleaningOptions options) {
        var dict = new Dictionary<string, string>();
        if (options.VSToolsPath is { }) {
            dict["VSToolsPath"] = options.VSToolsPath;
        }
        if (options.VSRootPath is { } && Directory.Exists(Path.Combine(options.VSRootPath, "MSBuild"))) {
            dict["MSBuildExtensionsPath"] = Path.Combine(options.VSRootPath, "MSBuild");
        }
        return dict;
    }

    private void DisplayResults(List<ProjectNugetAnalysis> analyses, NugetPackageCategorizer categorizer) {
        if (!analyses.Any()) {
            _console.WriteWarning("No projects with NuGet package references found.");
            return;
        }

        _console.WriteInfo($"Found {analyses.Count} project(s) with NuGet packages:");

        foreach (var analysis in analyses.OrderBy(a => a.ProjectName)) {
            DisplayProjectAnalysis(analysis, categorizer);
        }

        // Summary
        var totalPackages = analyses.SelectMany(a => a.Packages).Count();
        var uniquePackages = analyses.SelectMany(a => a.Packages).Select(p => p.Name).Distinct().Count();

        _console.WriteRule("[bold green]Summary[/]");
        _console.WriteInfo($"Total packages across all projects: {totalPackages}");
        _console.WriteInfo($"Unique packages: {uniquePackages}");
    }

    private void DisplayProjectAnalysis(ProjectNugetAnalysis analysis, NugetPackageCategorizer categorizer) {
        var content = new List<string>();
        content.Add($"[dim]Path: {analysis.ProjectPath}[/]");
        content.Add($"[dim]Total packages: {analysis.Packages.Count}[/]");
        content.Add("");

        // Display packages by category
        AddCategorySection(content, "Microsoft Official .NET Packages", analysis.MicrosoftOfficialPackages);
        AddCategorySection(content, "Microsoft Non-Official Packages", analysis.MicrosoftNonOfficialPackages);
        AddCategorySection(content, "Known Trusted Packages", analysis.TrustedThirdPartyPackages);
        AddCategorySection(content, "Other Packages", analysis.OtherPackages);

        var panel = new Panel(string.Join("\n", content))
            .Header($"[bold blue]{analysis.ProjectName}[/]")
            .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);
    }

    private void AddCategorySection(List<string> content, string categoryName, IEnumerable<NugetPackageInfo> packages) {
        var packageList = packages.ToList();
        if (!packageList.Any()) {
            return;
        }

        content.Add($"[bold yellow]{categoryName}:[/]");

        foreach (var package in packageList.OrderBy(p => p.Name)) {
            var packageInfo = $"• {package.Name} ({package.Version})";

            // Add coloring and pattern information based on whitelist/blacklist/microsoft/trusted
            if (!string.IsNullOrWhiteSpace(package.BlacklistMatch)) {
                packageInfo = $"[red]{packageInfo} ({package.BlacklistMatch})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(package.WhitelistMatch)) {
                packageInfo = $"[green]{packageInfo} ({package.WhitelistMatch})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(package.MicrosoftMatch)) {
                packageInfo = $"{packageInfo} ({package.MicrosoftMatch})";
            }
            else if (!string.IsNullOrWhiteSpace(package.TrustedMatch)) {
                packageInfo = $"{packageInfo} ({package.TrustedMatch})";
            }

            content.Add(packageInfo);
        }
        content.Add("");
    }

    private void DisplayAggregateResults(List<ProjectNugetAnalysis> analyses, NugetPackageCategorizer categorizer, bool showProjects) {
        if (!analyses.Any()) {
            _console.WriteWarning("No projects with NuGet package references found.");
            return;
        }

        _console.WriteInfo($"Found {analyses.Count} project(s) with NuGet packages (aggregate view):");
        _console.WriteInfo("");

        // Group packages by name across all projects
        var allPackages = analyses
            .SelectMany(a => a.Packages.Select(p => new { Package = p, Analysis = a }))
            .ToList();
        
        var packageGroups = allPackages
            .GroupBy(pa => pa.Package.Name)
            .Select(g => new AggregatedPackage {
                Name = g.Key,
                Category = g.First().Package.Category,
                Occurrences = g.Select(pa => new PackageOccurrence {
                    ProjectName = pa.Analysis.ProjectName ?? "Unknown",
                    ProjectPath = pa.Analysis.ProjectPath,
                    Version = pa.Package.Version,
                    WhitelistMatch = pa.Package.WhitelistMatch,
                    BlacklistMatch = pa.Package.BlacklistMatch,
                    MicrosoftMatch = pa.Package.MicrosoftMatch,
                    TrustedMatch = pa.Package.TrustedMatch
                }).ToList()
            })
            .ToList();

        // Separate by category
        var microsoftOfficialPackages = packageGroups.Where(p => p.Category == NugetPackageCategory.MicrosoftOfficial).ToList();
        var microsoftNonOfficialPackages = packageGroups.Where(p => p.Category == NugetPackageCategory.MicrosoftNonOfficial).ToList();
        var trustedPackages = packageGroups.Where(p => p.Category == NugetPackageCategory.TrustedThirdParty).ToList();
        var otherPackages = packageGroups.Where(p => p.Category == NugetPackageCategory.Other).ToList();

        var content = new List<string>();

        // Display each category
        AddAggregateCategorySection(content, "Microsoft Official .NET Packages", microsoftOfficialPackages, showProjects);
        AddAggregateCategorySection(content, "Microsoft Non-Official Packages", microsoftNonOfficialPackages, showProjects);
        AddAggregateCategorySection(content, "Known Trusted Packages", trustedPackages, showProjects);
        AddAggregateCategorySection(content, "Other Packages", otherPackages, showProjects);

        var panel = new Panel(string.Join("\n", content))
            .Header("[bold blue]Aggregated Package View[/]")
            .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);

        // Summary
        var totalPackages = allPackages.Count;
        var uniquePackages = packageGroups.Count;

        _console.WriteRule("[bold green]Summary[/]");
        _console.WriteInfo($"Total package references across all projects: {totalPackages}");
        _console.WriteInfo($"Unique packages: {uniquePackages}");
    }

    private void AddAggregateCategorySection(List<string> content, string categoryName, List<AggregatedPackage> packages, bool showProjects) {
        if (!packages.Any()) {
            return;
        }

        content.Add($"[bold yellow]{categoryName}:[/]");

        foreach (var pkg in packages.OrderBy(p => p.Name)) {
            var versions = pkg.Occurrences.Select(o => o.Version).Distinct().ToList();
            var versionInfo = versions.Count == 1 
                ? $"({versions[0]})" 
                : $"(multiple versions: {string.Join(", ", versions)})";

            var packageInfo = $"• {pkg.Name} {versionInfo}";

            // Add coloring based on match type (use first occurrence)
            var firstOccurrence = pkg.Occurrences.First();
            if (!string.IsNullOrWhiteSpace(firstOccurrence.BlacklistMatch)) {
                packageInfo = $"[red]{packageInfo} ({firstOccurrence.BlacklistMatch})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(firstOccurrence.WhitelistMatch)) {
                packageInfo = $"[green]{packageInfo} ({firstOccurrence.WhitelistMatch})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(firstOccurrence.MicrosoftMatch)) {
                packageInfo = $"{packageInfo} ({firstOccurrence.MicrosoftMatch})";
            }
            else if (!string.IsNullOrWhiteSpace(firstOccurrence.TrustedMatch)) {
                packageInfo = $"{packageInfo} ({firstOccurrence.TrustedMatch})";
            }

            content.Add(packageInfo);

            // Show which projects reference this package if enabled
            if (showProjects) {
                foreach (var occurrence in pkg.Occurrences.OrderBy(o => o.ProjectName)) {
                    var projectInfo = $"    [dim]→ {occurrence.ProjectName}";
                    if (versions.Count > 1) {
                        projectInfo += $" (v{occurrence.Version})";
                    }
                    projectInfo += "[/]";
                    content.Add(projectInfo);
                }
            }
        }
        content.Add("");
    }
}