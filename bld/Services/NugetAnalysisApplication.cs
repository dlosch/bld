using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace bld.Services;

/// <summary>
/// Application for analyzing NuGet package references
/// </summary>
internal class NugetAnalysisApplication {
    private readonly IConsoleOutput _console;
    private bool _isInitialized = false;

    public NugetAnalysisApplication(IConsoleOutput console) {
        _console = console;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task InitAsync(CleaningOptions options) {
        // Register MSBuild defaults before any MSBuild types are loaded
        MSBuildService.RegisterMSBuildDefaults(_console, options);
        _isInitialized = true;
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    /// <returns>1 when any solution or project could not be analyzed, otherwise 0.</returns>
    public async Task<int> RunAsync(string[] rootPaths, CleaningOptions options, string? whitelistBlacklistFile, bool aggregate = false, bool showProjects = true, bool markdownOutput = false, bool includeTransitive = false) {
        if (!_isInitialized) {
            throw new InvalidOperationException("Application not initialized. Call InitAsync first.");
        }

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
                _console.WriteError($"Failed to parse whitelist/blacklist file: {ex.FormatMessage()}");
                return 1;
            }
        }

        var categorizer = new NugetPackageCategorizer(whitelistBlacklistRules);
        var packageExtractor = new NugetPackageExtractor(_console, errorSink, categorizer);
        var cache = new ProjCfgCache(_console);

        _console.WriteRule("[bold blue]NuGet Package Analysis[/]");
        _console.WriteInfo("Analyzing NuGet package references...");

        var stopwatch = Stopwatch.StartNew();

        var parallelOptions = new ParallelOptions {
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism
        };

        try {
            var allSlns = new ConcurrentBag<string>();
            await Parallel.ForEachAsync(rootPaths, parallelOptions, async (rootPath, ct) => {
                await foreach (var sln in scanner.Enumerate(rootPath)) {
                    allSlns.Add(sln);
                }
            });

            var allProjCfgs = new ConcurrentBag<ProjCfg>();
            await Parallel.ForEachAsync(allSlns, parallelOptions, async (sln, ct) => {
                await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem)) {
                    if (cache.Add(projCfg)) {
                        allProjCfgs.Add(projCfg);
                    }
                }
            });

            var allProjectAnalyses = new ConcurrentBag<ProjectNugetAnalysis>();

            await _console.StartStatusAsync($"Analyzing {allProjCfgs.Count} project configurations...", async ctx => {
                var count = 0;
                var total = allProjCfgs.Count;

                await Parallel.ForEachAsync(allProjCfgs, parallelOptions, async (projCfg, ct) => {
                    var current = Interlocked.Increment(ref count);
                    ctx.Status($"Analyzing projects: {current}/{total} ([bold]{Markup.Escape(Path.GetFileName(projCfg.Path))}[/])");

                    try {
                        var globalProperties = GetGlobalProperties(options);
                        var analysis = packageExtractor.AnalyzeProject(projCfg, globalProperties, includeTransitive);

                        if (analysis.Packages.Any()) {
                            allProjectAnalyses.Add(analysis);
                        }
                    }
                    catch (Exception ex) {
                        _console.WriteError($"Failed to analyze project {projCfg.Path}: {ex.FormatMessage()}");
                    }
                });
            });

            // Display results with unique projects only (deduplicate by path)
            var uniqueAnalyses = allProjectAnalyses
                .GroupBy(a => a.ProjectPath)
                .Select(g => g.First())
                .ToList();

            if (markdownOutput) {
                DisplayMarkdownResults(uniqueAnalyses, includeTransitive);
            }
            else if (aggregate) {
                DisplayAggregateResults(uniqueAnalyses, categorizer, showProjects);
            }
            else {
                DisplayResults(uniqueAnalyses, categorizer);
            }

        }
        finally {
            stopwatch.Stop();
            _console.WriteInfo($"Analysis completed in {stopwatch.Elapsed:mm\\:ss\\.fff}");
            errorSink.WriteTo();
        }
        // The errors were always printed; the exit code just never followed them.
        return errorSink.HasErrors ? 1 : 0;
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

        _console.WriteLine($"Found {analyses.Count} project(s) with NuGet packages:");

        foreach (var analysis in analyses.OrderBy(a => a.ProjectName)) {
            DisplayProjectAnalysis(analysis, categorizer);
        }

        // Summary
        var totalPackages = analyses.SelectMany(a => a.DirectPackages).Count();
        var uniquePackages = analyses.SelectMany(a => a.DirectPackages).Select(p => p.Name).Distinct().Count();
        var transitive = analyses.SelectMany(a => a.TransitivePackages).ToList();

        _console.WriteRule("[bold green]Summary[/]");
        _console.WriteLine($"Total packages across all projects: {totalPackages}");
        _console.WriteLine($"Unique packages: {uniquePackages}");
        if (transitive.Count > 0) {
            _console.WriteLine($"Transitive packages: {transitive.Count} ({transitive.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()} unique)");
        }
    }

    private void DisplayProjectAnalysis(ProjectNugetAnalysis analysis, NugetPackageCategorizer categorizer) {
        var content = new List<string>();
        content.Add($"[dim]Path: {Markup.Escape(analysis.ProjectPath)}[/]");
        content.Add($"[dim]Total packages: {analysis.DirectPackages.Count()}[/]");
        var transitive = analysis.TransitivePackages.ToList();
        if (transitive.Count > 0) {
            content.Add($"[dim]Transitive packages: {transitive.Count}[/]");
        }
        content.Add("");

        // Display packages by category
        AddCategorySection(content, "Microsoft Official .NET Packages", analysis.MicrosoftOfficialPackages);
        AddCategorySection(content, "Microsoft Non-Official Packages", analysis.MicrosoftNonOfficialPackages);
        AddCategorySection(content, "Known Trusted Packages", analysis.TrustedThirdPartyPackages);
        AddCategorySection(content, "Other Packages", analysis.OtherPackages);

        if (transitive.Count > 0) {
            content.Add("[bold]Transitive Packages[/]");
            content.Add("");
            AddCategorySection(content, "Microsoft Official .NET Packages", transitive.Where(p => p.Category == NugetPackageCategory.MicrosoftOfficial));
            AddCategorySection(content, "Microsoft Non-Official Packages", transitive.Where(p => p.Category == NugetPackageCategory.MicrosoftNonOfficial));
            AddCategorySection(content, "Known Trusted Packages", transitive.Where(p => p.Category == NugetPackageCategory.TrustedThirdParty));
            AddCategorySection(content, "Other Packages", transitive.Where(p => p.Category == NugetPackageCategory.Other));
        }

        var table = new Table().Border(TableBorder.Rounded)
            .Title($"[bold blue]{Markup.Escape(analysis.ProjectName ?? "")}[/]")
            .AddColumn(new TableColumn("Details").LeftAligned())
            .HideHeaders();
        table.AddRow(new Markup(string.Join("\n", content)));
        _console.WriteTable(table);
    }

    private void AddCategorySection(List<string> content, string categoryName, IEnumerable<NugetPackageInfo> packages) {
        var packageList = packages.ToList();
        if (!packageList.Any()) {
            return;
        }

        content.Add($"[bold yellow]{Markup.Escape(categoryName)}:[/]");

        foreach (var package in packageList.OrderBy(p => p.Name)) {
            var packageInfo = $"• {Markup.Escape(package.Name)} ({Markup.Escape(package.Version)}){KindSuffix(package.Kind)}";

            // Add coloring and pattern information based on whitelist/blacklist/microsoft/trusted
            if (!string.IsNullOrWhiteSpace(package.BlacklistMatch)) {
                packageInfo = $"[red]{packageInfo} ({Markup.Escape(package.BlacklistMatch)})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(package.WhitelistMatch)) {
                packageInfo = $"[green]{packageInfo} ({Markup.Escape(package.WhitelistMatch)})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(package.MicrosoftMatch)) {
                packageInfo = $"{packageInfo} ({Markup.Escape(package.MicrosoftMatch)})";
            }
            else if (!string.IsNullOrWhiteSpace(package.TrustedMatch)) {
                packageInfo = $"{packageInfo} ({Markup.Escape(package.TrustedMatch)})";
            }

            if (package.IsTransitive && package.RequestedBy.Count > 0) {
                packageInfo += $" [dim]via {Markup.Escape(string.Join(", ", package.RequestedBy))}[/]";
            }

            content.Add(packageInfo);
        }
        content.Add("");
    }

    private static string KindSuffix(PackageItemKind kind) => kind switch {
        PackageItemKind.GlobalPackageReference => " [dim](global)[/]",
        PackageItemKind.PackageDownload => " [dim](download)[/]",
        _ => string.Empty,
    };

    private void DisplayAggregateResults(List<ProjectNugetAnalysis> analyses, NugetPackageCategorizer categorizer, bool showProjects) {
        if (!analyses.Any()) {
            _console.WriteWarning("No projects with NuGet package references found.");
            return;
        }

        _console.WriteLine($"Found {analyses.Count} project(s) with NuGet packages (aggregate view):");
        _console.WriteLine("");

        // Group packages by name across all projects
        var allPackages = analyses
            .SelectMany(a => a.Packages.Select(p => new { Package = p, Analysis = a }))
            .ToList();
        
        var packageGroups = allPackages
            .GroupBy(pa => pa.Package.Name)
            .Select(g => new AggregatedPackage {
                Name = g.Key,
                Category = g.First().Package.Category,
                Kind = g.First().Package.Kind,
                IsTransitive = g.All(pa => pa.Package.IsTransitive),
                RequestedBy = g.SelectMany(pa => pa.Package.RequestedBy).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
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

        var table = new Table().Border(TableBorder.Rounded)
            .Title("[bold blue]Aggregated Package View[/]")
            .AddColumn(new TableColumn("Details").LeftAligned())
            .HideHeaders();
        table.AddRow(new Markup(string.Join("\n", content)));
        _console.WriteTable(table);

        // Summary
        var totalPackages = allPackages.Count(pa => !pa.Package.IsTransitive);
        var uniquePackages = packageGroups.Count(p => !p.IsTransitive);
        var transitiveUnique = packageGroups.Count(p => p.IsTransitive);

        _console.WriteRule("[bold green]Summary[/]");
        _console.WriteLine($"Total package references across all projects: {totalPackages}");
        _console.WriteLine($"Unique packages: {uniquePackages}");
        if (transitiveUnique > 0) {
            _console.WriteLine($"Unique transitive packages: {transitiveUnique}");
        }
    }

    private void AddAggregateCategorySection(List<string> content, string categoryName, List<AggregatedPackage> packages, bool showProjects) {
        if (!packages.Any()) {
            return;
        }

        content.Add($"[bold yellow]{Markup.Escape(categoryName)}:[/]");

        foreach (var pkg in packages.OrderBy(p => p.Name)) {
            var versions = pkg.Occurrences.Select(o => o.Version).Distinct().ToList();
            var versionInfo = versions.Count == 1 
                ? $"({Markup.Escape(versions[0])})" 
                : $"(multiple versions: {string.Join(", ", versions.Select(Markup.Escape))})";

            var packageInfo = $"• {Markup.Escape(pkg.Name)} {versionInfo}{KindSuffix(pkg.Kind)}";

            // Add coloring based on match type (use first occurrence)
            var firstOccurrence = pkg.Occurrences.First();
            if (!string.IsNullOrWhiteSpace(firstOccurrence.BlacklistMatch)) {
                packageInfo = $"[red]{packageInfo} ({Markup.Escape(firstOccurrence.BlacklistMatch)})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(firstOccurrence.WhitelistMatch)) {
                packageInfo = $"[green]{packageInfo} ({Markup.Escape(firstOccurrence.WhitelistMatch)})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(firstOccurrence.MicrosoftMatch)) {
                packageInfo = $"{packageInfo} ({Markup.Escape(firstOccurrence.MicrosoftMatch)})";
            }
            else if (!string.IsNullOrWhiteSpace(firstOccurrence.TrustedMatch)) {
                packageInfo = $"{packageInfo} ({Markup.Escape(firstOccurrence.TrustedMatch)})";
            }

            if (pkg.IsTransitive) {
                packageInfo += pkg.RequestedBy.Count > 0
                    ? $" [dim]transitive via {Markup.Escape(string.Join(", ", pkg.RequestedBy))}[/]"
                    : " [dim]transitive[/]";
            }

            content.Add(packageInfo);

            // Show which projects reference this package if enabled
            if (showProjects) {
                foreach (var occurrence in pkg.Occurrences.OrderBy(o => o.ProjectName)) {
                    var projectInfo = $"    [dim]→ {Markup.Escape(occurrence.ProjectName)}";
                    if (versions.Count > 1) {
                        projectInfo += $" (v{Markup.Escape(occurrence.Version)})";
                    }
                    projectInfo += "[/]";
                    content.Add(projectInfo);
                }
            }
        }
        content.Add("");
    }

    private void DisplayMarkdownResults(List<ProjectNugetAnalysis> analyses, bool includeTransitive) {
        if (!analyses.Any()) {
            _console.WriteWarning("No projects with NuGet package references found.");
            return;
        }

        var rows = analyses
            .SelectMany(a => a.Packages.Select(p => new {
                Package = p,
                ProjectName = string.IsNullOrWhiteSpace(a.ProjectName) ? Path.GetFileNameWithoutExtension(a.ProjectPath) : a.ProjectName
            }))
            .GroupBy(x => x.Package.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => {
                var versions = g.Select(x => x.Package.Version)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var projects = g.Select(x => x.ProjectName ?? "Unknown")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var trustedComment = GetTrustComment(g.Select(x => x.Package));

                var row = new List<string?> {
                    g.Key,
                    versions.Length == 0 ? string.Empty : string.Join(", ", versions),
                    trustedComment,
                    string.Join("<br>", projects)
                };
                if (includeTransitive) {
                    // "direct" as soon as one project references it itself.
                    row.Add(g.All(x => x.Package.IsTransitive) ? "transitive" : "direct");
                }
                return (IReadOnlyList<string?>)row;
            })
            .ToList();

        var headers = new List<string> { "Package name", "Package Version", "Trusted", "Projects" };
        if (includeTransitive) headers.Add("Direct/Transitive");

        MarkdownTableFormatter.Write(
            _console,
            "NuGet packages (markdown)",
            headers,
            rows);
    }

    private static string GetTrustComment(IEnumerable<NugetPackageInfo> packages) {
        var packageList = packages.ToList();
        if (packageList.Any(p => !string.IsNullOrWhiteSpace(p.BlacklistMatch))) {
            return "Not trusted (blacklisted)";
        }

        if (packageList.Any(p =>
            p.Category is NugetPackageCategory.MicrosoftOfficial
            or NugetPackageCategory.MicrosoftNonOfficial
            or NugetPackageCategory.TrustedThirdParty
            || !string.IsNullOrWhiteSpace(p.WhitelistMatch)
            || !string.IsNullOrWhiteSpace(p.MicrosoftMatch)
            || !string.IsNullOrWhiteSpace(p.TrustedMatch))) {
            return "Trusted";
        }

        return "Not trusted";
    }
}