using bld.Infrastructure;
using bld.Models;
using Microsoft.Build.Evaluation;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace bld.Services;

internal class BuildPropsService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public BuildPropsService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    internal record PropsPropertyInfo(
        string Name,
        string EvaluatedValue,
        string UnevaluatedValue,
        string SourceFile,
        int SourceLine);

    internal record PropsOverrideInfo(
        string PropertyName,
        string SharedValue,
        string SharedFile,
        string OverrideValue,
        string OverrideUnevaluatedValue,
        string OverrideFile,
        string ChangeFile,
        bool IsMerged,
        string ProjectPath,
        string? ProjectName);

    internal record PropsDisplayRow(
        string Name,
        string EvaluatedValue,
        string UnevaluatedValue,
        string SourceFile,
        int SourceLine,
        string? OverriddenIn,
        bool IsOverridden,
        bool IsMerged);

    internal record ProjectEvalResult(
        string ProjectPath,
        string? ProjectName,
        IReadOnlyList<string> ImportedPropsFiles,
        IReadOnlyList<string> ImportedTargetsFiles,
        IReadOnlyList<PropsPropertyInfo> Properties,
        IReadOnlyList<PropsOverrideInfo> Overrides);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> AnalyzeAsync(string rootPath, string[]? filterProperties, bool markdownOutput, bool listOnly, bool includeOverridden, CancellationToken cancellationToken) {
        MSBuildInitializer.Initialize(_console, _options);

        _console.WriteRule("[bold blue]Directory.Build.props Analysis (BETA)[/]");
        _console.WriteInfo($"Scanning: {rootPath}");

        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);
        var fileSystem = new FileSystem(_console, errorSink);
        var cache = new ProjCfgCache(_console);
        var globalProperties = BuildGlobalProperties(_options);

        var parallelOptions = new ParallelOptions {
            MaxDegreeOfParallelism = _options.Parallel ? _options.MaxDegreeOfParallelism : 1,
            CancellationToken = cancellationToken
        };
        var filterSet = filterProperties is { Length: > 0 }
            ? new HashSet<string>(filterProperties, StringComparer.OrdinalIgnoreCase)
            : null;
        var projectCollectionPoolSize = Math.Max(1, parallelOptions.MaxDegreeOfParallelism);
        using var projectCollectionPool = new ProjectCollectionPool(projectCollectionPoolSize);

        // Find all solution/project files
        var allSlns = new ConcurrentBag<string>();
        await foreach (var sln in slnScanner.Enumerate(rootPath)) {
            allSlns.Add(sln);
        }

        var allProjCfgs = new ConcurrentBag<ProjCfg>();
        await Parallel.ForEachAsync(allSlns, parallelOptions, async (sln, ct) => {
            await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem, createDefaultDebugConfiguration: false)) {
                if (cache.Add(projCfg)) {
                    allProjCfgs.Add(projCfg);
                }
            }
        });

        // Deduplicate by project path — we only need one evaluation per project
        var uniqueProjectPaths = allProjCfgs
            .Select(p => p.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uniqueProjectPaths.Count == 0) {
            _console.WriteInfo("No projects found via solution files. Performing filesystem discovery...");
            return FallbackDiscovery(rootPath);
        }

        _console.WriteInfo($"Found {uniqueProjectPaths.Count} project(s)");

        // --list: lightweight path — only resolve imports, no property extraction
        if (listOnly) {
            var imports = new ConcurrentBag<(string Project, List<string> Props)>();

            await _console.StartStatusAsync($"Resolving imports for {uniqueProjectPaths.Count} projects...", async ctx => {
                var count = 0;
                var total = uniqueProjectPaths.Count;

                await Parallel.ForEachAsync(uniqueProjectPaths, parallelOptions, (projectPath, ct) => {
                    var current = Interlocked.Increment(ref count);
                    ctx.Status($"Resolving: {current}/{total} ([bold]{Path.GetFileName(projectPath)}[/])");

                    var (props, _) = projectCollectionPool.Execute(projectPath,
                        pc => ResolveImportedBuildFiles(projectPath, globalProperties, pc));
                    imports.Add((projectPath, props));
                    return ValueTask.CompletedTask;
                });
            });

            DisplayListOnly(imports.ToList(), rootPath);
            errorSink.WriteTo();
            return 0;
        }

        // Full evaluation — extract properties and overrides
        var results = new ConcurrentBag<ProjectEvalResult>();

        await _console.StartStatusAsync($"Evaluating {uniqueProjectPaths.Count} projects...", async ctx => {
            var count = 0;
            var total = uniqueProjectPaths.Count;

            await Parallel.ForEachAsync(uniqueProjectPaths, parallelOptions, (projectPath, ct) => {
                var current = Interlocked.Increment(ref count);
                ctx.Status($"Evaluating: {current}/{total} ([bold]{Path.GetFileName(projectPath)}[/])");

                var result = projectCollectionPool.Execute(projectPath,
                    pc => EvaluateProject(projectPath, globalProperties, filterSet, pc));
                if (result != null) {
                    results.Add(result);
                }
                return ValueTask.CompletedTask;
            });
        });

        var resultsList = results.ToList();

        if (markdownOutput) {
            DisplayMarkdownResults(resultsList, rootPath, filterProperties, includeOverridden);
        }
        else {
            DisplayConsoleResults(resultsList, rootPath, filterProperties, includeOverridden);
        }

        errorSink.WriteTo();
        return 0;
    }

    /// <summary>
    /// Evaluates a single project with MSBuild and extracts Directory.Build.props information.
    /// Uses Project.Imports to find imported props/targets files and Project.Properties
    /// with Predecessor chain to trace property provenance.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal ProjectEvalResult? EvaluateProject(
        string projectPath,
        Dictionary<string, string> globalProperties,
        ISet<string>? filterProperties = null,
        ProjectCollection? projectCollection = null) {
        var ownsProjectCollection = projectCollection is null;
        var pc = projectCollection ?? new ProjectCollection();
        Project? project = null;
        try {
            project = new Project(projectPath, globalProperties, null, pc);

            // Use the Imports API to find actually-imported Directory.Build.props files
            var propsFiles = project.Imports
                .Where(i => Path.GetFileName(i.ImportedProject.FullPath)
                    .Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase))
                .Select(i => NormalizeFilePath(i.ImportedProject.FullPath))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();

            var targetsFiles = project.Imports
                .Where(i => Path.GetFileName(i.ImportedProject.FullPath)
                    .Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase))
                .Select(i => NormalizeFilePath(i.ImportedProject.FullPath))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();

            var projectName = project.GetPropertyValue("ProjectName");

            if (propsFiles.Count == 0 && targetsFiles.Count == 0) {
                return new ProjectEvalResult(
                    projectPath, projectName, propsFiles, targetsFiles,
                    Array.Empty<PropsPropertyInfo>(), Array.Empty<PropsOverrideInfo>());
            }

            var propsFileSet = new HashSet<string>(propsFiles, StringComparer.OrdinalIgnoreCase);
            var properties = new List<PropsPropertyInfo>();
            var overrides = new List<PropsOverrideInfo>();

            // Walk each property's evaluation chain to determine:
            // 1. Properties whose final value comes from Directory.Build.props
            // 2. Properties that were defined in Directory.Build.props but overridden elsewhere
            foreach (var prop in project.Properties) {
                if (prop.IsReservedProperty || prop.IsEnvironmentProperty || prop.IsGlobalProperty)
                    continue;
                if (filterProperties is not null && !filterProperties.Contains(prop.Name))
                    continue;

                var currentFile = NormalizeFilePath(prop.Xml?.Location.File);
                var isCurrentFromProps = currentFile != null && propsFileSet.Contains(currentFile);

                if (isCurrentFromProps) {
                    // Property's final value comes from a Directory.Build.props file
                    properties.Add(new PropsPropertyInfo(
                        prop.Name,
                        prop.EvaluatedValue,
                        prop.UnevaluatedValue,
                        currentFile!,
                        prop.Xml!.Location.Line));
                }
                else {
                    // Walk the Predecessor chain to find if this property was originally
                    // defined in a Directory.Build.props and later overridden
                    var chain = new List<ProjectProperty>();
                    ProjectProperty? sharedOrigin = null;
                    var cursor = prop;
                    while (cursor != null) {
                        chain.Add(cursor);
                        var sourceFile = NormalizeFilePath(cursor.Xml?.Location.File);
                        if (sourceFile != null && propsFileSet.Contains(sourceFile)) {
                            sharedOrigin = cursor;
                            break;
                        }
                        cursor = cursor.Predecessor;
                    }

                    if (sharedOrigin != null) {
                        var newerAssignments = chain
                            .TakeWhile(p => !ReferenceEquals(p, sharedOrigin))
                            .Reverse()
                            .ToList();

                        var isMerged = true;
                        string? firstOverridingFile = null;
                        foreach (var assignment in newerAssignments) {
                            if (!ReferencesSelf(assignment, prop.Name)) {
                                isMerged = false;
                                firstOverridingFile = NormalizeFilePath(assignment.Xml?.Location.File);
                                break;
                            }
                        }

                        var finalSourceFile = currentFile ?? "(unknown)";
                        var changeSourceFile = isMerged
                            ? finalSourceFile
                            : firstOverridingFile ?? finalSourceFile;

                        var sharedFile = NormalizeFilePath(sharedOrigin.Xml?.Location.File);
                        if (sharedFile != null) {
                            overrides.Add(new PropsOverrideInfo(
                                prop.Name,
                                sharedOrigin.EvaluatedValue,
                                sharedFile,
                                prop.EvaluatedValue,
                                prop.UnevaluatedValue,
                                finalSourceFile,
                                changeSourceFile,
                                isMerged,
                                projectPath,
                                projectName));
                        }
                    }
                }
            }

            return new ProjectEvalResult(
                projectPath, projectName, propsFiles, targetsFiles, properties, overrides);
        }
        catch (Exception ex) {
            _console.WriteVerbose($"Failed to evaluate {projectPath}: {ex.FormatMessage()}");
            return null;
        }
        finally {
            if (project is not null) {
                pc.UnloadProject(project);
            }
            if (ownsProjectCollection) {
                pc.Dispose();
            }
        }
    }

    /// <summary>
    /// Lightweight: only resolves which Directory.Build.props/targets files a project imports,
    /// without extracting property values or walking the Predecessor chain.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal (List<string> Props, List<string> Targets) ResolveImportedBuildFiles(
        string projectPath,
        Dictionary<string, string> globalProperties,
        ProjectCollection? projectCollection = null) {
        var ownsProjectCollection = projectCollection is null;
        var pc = projectCollection ?? new ProjectCollection();
        Project? project = null;
        try {
            project = new Project(projectPath, globalProperties, null, pc);

            var props = project.Imports
                .Where(i => Path.GetFileName(i.ImportedProject.FullPath)
                    .Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase))
                .Select(i => NormalizeFilePath(i.ImportedProject.FullPath))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();

            var targets = project.Imports
                .Where(i => Path.GetFileName(i.ImportedProject.FullPath)
                    .Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase))
                .Select(i => NormalizeFilePath(i.ImportedProject.FullPath))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();

            return (props, targets);
        }
        catch (Exception ex) {
            _console.WriteVerbose($"Failed to resolve imports for {projectPath}: {ex.FormatMessage()}");
            return (new List<string>(), new List<string>());
        }
        finally {
            if (project is not null) {
                pc.UnloadProject(project);
            }
            if (ownsProjectCollection) {
                pc.Dispose();
            }
        }
    }

    private void DisplayListOnly(List<(string Project, List<string> Props)> imports, string rootPath) {
        if (imports.Count == 0) {
            _console.WriteLine("No projects could be evaluated.");
            return;
        }

        var tree = new Tree(Markup.Escape(rootPath));

        // Track already-created nodes so each props file appears exactly once
        var nodeMap = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var (project, props) in imports.OrderBy(i => i.Project, StringComparer.OrdinalIgnoreCase)) {
            // Chain: outermost (shallowest) props first → innermost → project leaf
            var chain = props
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f.Length)
                .ToList();

            // Walk the chain, reusing existing nodes
            TreeNode? parent = null;
            foreach (var buildFile in chain) {
                if (!nodeMap.TryGetValue(buildFile, out var node)) {
                    var label = $"[green]●[/] {Markup.Escape(buildFile)}";
                    node = parent is null
                        ? tree.AddNode(label)
                        : parent.AddNode(label);
                    nodeMap[buildFile] = node;
                }
                parent = node;
            }

            // Project is always a leaf
            if (parent is not null)
                parent.AddNode(Markup.Escape(project));
            else
                tree.AddNode(Markup.Escape(project));
        }

        _console.WriteLine("");
        AnsiConsole.Write(tree);
    }

    private void DisplayConsoleResults(List<ProjectEvalResult> results, string rootPath, string[]? filterProperties, bool includeOverridden) {
        if (results.Count == 0) {
            _console.WriteLine("No projects could be evaluated.");
            return;
        }

        // Aggregate: which Directory.Build.props files are imported, and by how many projects
        var propsFileProjects = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var targetsFileProjects = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results) {
            foreach (var f in result.ImportedPropsFiles)
                GetOrAdd(propsFileProjects, f).Add(result.ProjectPath);
            foreach (var f in result.ImportedTargetsFiles)
                GetOrAdd(targetsFileProjects, f).Add(result.ProjectPath);
        }

        if (propsFileProjects.Count == 0) {
            _console.WriteLine("\nNo Directory.Build.props files are imported by any project.");

            var onDisk = DiscoverBuildPropsFilesOnDisk(rootPath);
            if (onDisk.Count > 0) {
                _console.WriteInfo("\nDirectory.Build.props files exist on disk but are not imported:");
                foreach (var f in onDisk)
                    _console.WriteLine($"  {f}");
                _console.WriteWarning("These files may not be imported due to a missing import chain or evaluation errors.");
            }
            return;
        }

        // --- Imported files ---
        _console.WriteLine($"\nImported Directory.Build.props files:");
        var idx = 1;
        foreach (var (file, projects) in propsFileProjects.OrderBy(kvp => kvp.Key)) {
            _console.WriteLine($"  {idx++}. {file} ({projects.Count} project(s))");
        }

        if (targetsFileProjects.Count > 0) {
            _console.WriteLine($"\nImported Directory.Build.targets files:");
            foreach (var (file, projects) in targetsFileProjects.OrderBy(kvp => kvp.Key))
                _console.WriteLine($"     {file} ({projects.Count} project(s))");
        }

        var notImporting = results.Count(r => r.ImportedPropsFiles.Count == 0);
        if (notImporting > 0)
            _console.WriteInfo($"{notImporting} project(s) do not import any Directory.Build.props.");

        // --- Properties table ---
        var propertyRows = BuildPropertyRows(results, filterProperties, includeOverridden);
        if (propertyRows.Count > 0) {
            // Group by source file, then sort properties alphabetically within each group
            var grouped = propertyRows
                .GroupBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in grouped) {
                var showOverrideColumn = includeOverridden;
                var table = new Table().Border(TableBorder.Rounded).Expand();
                table.AddColumn(new TableColumn("Property").LeftAligned());
                table.AddColumn(new TableColumn("Value").LeftAligned());
                if (showOverrideColumn)
                    table.AddColumn(new TableColumn("Overridden In").LeftAligned());
                table.AddColumn(new TableColumn("Line").RightAligned());

                foreach (var row in group.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) {
                    var propertyCell = row.IsOverridden
                        ? $"[italic]{Markup.Escape(row.Name)}[/]"
                        : Markup.Escape(row.Name);
                    var overriddenInCell = row.OverriddenIn is null
                        ? "[dim]-[/]"
                        : Markup.Escape(row.IsMerged ? $"(merged) {row.OverriddenIn}" : row.OverriddenIn);

                    if (showOverrideColumn) {
                        table.AddRow(
                            propertyCell,
                            Markup.Escape(BuildPropertyValue(row)),
                            overriddenInCell,
                            row.SourceLine > 0 ? row.SourceLine.ToString() : "-");
                    }
                    else {
                        table.AddRow(
                            propertyCell,
                            Markup.Escape(BuildPropertyValue(row)),
                            row.SourceLine > 0 ? row.SourceLine.ToString() : "-");
                    }
                }

                _console.WriteLine($"\nProperties from {group.Key}");
                _console.WriteTable(table);
            }
        }

    }

    private void DisplayMarkdownResults(List<ProjectEvalResult> results, string rootPath, string[]? filterProperties, bool includeOverridden) {
        if (results.Count == 0) {
            _console.WriteLine("No projects could be evaluated.");
            return;
        }

        // Imported files
        var propsFiles = results.SelectMany(r => r.ImportedPropsFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f)
            .ToList();

        if (propsFiles.Count == 0) {
            _console.WriteLine("No Directory.Build.props files found.");
            return;
        }

        _console.WriteOutput("Imported files",
            string.Join(Environment.NewLine, propsFiles.Select(f => $"- `{f}`")));

        var propertyRows = BuildPropertyRows(results, filterProperties, includeOverridden);
        if (propertyRows.Count > 0) {
            var grouped = propertyRows
                .GroupBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in grouped) {
                var showOverrideColumn = includeOverridden;
                var rows = group
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => showOverrideColumn
                        ? (IReadOnlyList<string?>)new[] {
                            p.IsOverridden ? $"*{p.Name}*" : p.Name,
                            BuildPropertyValue(p),
                            p.OverriddenIn is null
                                ? "-"
                                : p.IsMerged ? $"(merged) {p.OverriddenIn}" : p.OverriddenIn,
                            p.SourceLine > 0 ? p.SourceLine.ToString() : "-"
                        }
                        : new[] {
                            p.IsOverridden ? $"*{p.Name}*" : p.Name,
                            BuildPropertyValue(p),
                            p.SourceLine > 0 ? p.SourceLine.ToString() : "-"
                        });

                MarkdownTableFormatter.Write(
                    _console,
                    $"Properties from {group.Key}",
                    showOverrideColumn
                        ? new[] { "Property", "Value", "Overridden In", "Line" }
                        : new[] { "Property", "Value", "Line" },
                    rows);
            }
        }
    }

    private static List<PropsDisplayRow> BuildPropertyRows(
        List<ProjectEvalResult> results,
        string[]? filterProperties,
        bool includeOverridden) {
        var aggregatedProperties = results
            .SelectMany(r => r.Properties)
            .GroupBy(p => (p.Name, p.EvaluatedValue, p.SourceFile), PropertyKeyComparer.Instance)
            .Select(g => g.First())
            .ToList();

        var aggregatedOverrides = results
            .SelectMany(r => r.Overrides)
            .GroupBy(o => (
                o.PropertyName,
                o.SharedValue,
                o.SharedFile,
                o.OverrideValue,
                o.OverrideUnevaluatedValue,
                o.ChangeFile,
                o.IsMerged), OverrideKeyComparer.Instance)
            .Select(g => g.First())
            .ToList();

        if (filterProperties is { Length: > 0 }) {
            var filterSet = new HashSet<string>(filterProperties, StringComparer.OrdinalIgnoreCase);
            aggregatedProperties = aggregatedProperties.Where(p => filterSet.Contains(p.Name)).ToList();
            aggregatedOverrides = aggregatedOverrides.Where(o => filterSet.Contains(o.PropertyName)).ToList();
        }

        var rows = aggregatedProperties
            .Select(p => new PropsDisplayRow(
                p.Name,
                p.EvaluatedValue,
                p.UnevaluatedValue,
                p.SourceFile,
                p.SourceLine,
                null,
                false,
                false))
            .ToList();

        if (includeOverridden && aggregatedOverrides.Count > 0) {
            rows.AddRange(aggregatedOverrides.Select(o => new PropsDisplayRow(
                o.PropertyName,
                o.OverrideValue,
                o.OverrideUnevaluatedValue,
                o.SharedFile,
                0,
                GetChangeLocation(o),
                true,
                o.IsMerged)));
        }

        return rows
            .GroupBy(r => (
                r.Name,
                r.EvaluatedValue,
                r.UnevaluatedValue,
                r.SourceFile,
                r.OverriddenIn ?? string.Empty,
                r.IsOverridden,
                r.IsMerged), DisplayRowKeyComparer.Instance)
            .Select(g => g.First())
            .ToList();
    }

    private static bool ReferencesSelf(ProjectProperty property, string propertyName) {
        return ReferencesSelf(property.UnevaluatedValue, propertyName);
    }

    private static bool ReferencesSelf(string? unevaluatedValue, string propertyName) {
        if (string.IsNullOrEmpty(propertyName) || string.IsNullOrEmpty(unevaluatedValue)) {
            return false;
        }

        var value = unevaluatedValue;
        var nameLength = propertyName.Length;
        for (var i = 0; i <= value.Length - 2; i++) {
            if (value[i] != '$' || value[i + 1] != '(') {
                continue;
            }

            var cursor = i + 2;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) {
                cursor++;
            }

            if (cursor + nameLength > value.Length) {
                continue;
            }

            if (!value.AsSpan(cursor, nameLength).Equals(propertyName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            cursor += nameLength;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) {
                cursor++;
            }

            if (cursor < value.Length && value[cursor] == ')') {
                return true;
            }
        }

        return false;
    }

    private static string GetChangeLocation(PropsOverrideInfo overrideInfo) {
        return string.Equals(overrideInfo.ChangeFile, "(unknown)", StringComparison.OrdinalIgnoreCase)
            ? overrideInfo.ProjectPath
            : overrideInfo.ChangeFile;
    }

    private static string BuildPropertyValue(PropsDisplayRow row) {
        if (row.UnevaluatedValue != row.EvaluatedValue
            && row.UnevaluatedValue.Contains("$(", StringComparison.Ordinal)) {
            return $"{Truncate(row.EvaluatedValue)}  ({row.UnevaluatedValue})";
        }

        return row.EvaluatedValue;
    }

    private static string? NormalizeFilePath(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':') {
            return normalized.TrimEnd('/');
        }

        return Path.GetFullPath(normalized).Replace('\\', '/').TrimEnd('/');
    }

    private sealed class ProjectCollectionPool : IDisposable {
        private readonly ProjectCollection[] _collections;
        private readonly object[] _locks;

        public ProjectCollectionPool(int size) {
            var normalizedSize = Math.Max(1, size);
            _collections = new ProjectCollection[normalizedSize];
            _locks = new object[normalizedSize];
            for (var i = 0; i < normalizedSize; i++) {
                _collections[i] = new ProjectCollection();
                _locks[i] = new object();
            }
        }

        public TResult Execute<TResult>(string key, Func<ProjectCollection, TResult> action) {
            var slot = GetSlot(key, _collections.Length);
            lock (_locks[slot]) {
                return action(_collections[slot]);
            }
        }

        public void Dispose() {
            foreach (var collection in _collections) {
                collection.UnloadAllProjects();
                collection.Dispose();
            }
        }

        private static int GetSlot(string key, int slotCount) {
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key);
            return (int)((uint)hash % (uint)slotCount);
        }
    }

    /// <summary>
    /// Walks up the directory tree from startPath to discover Directory.Build.props files on disk.
    /// Used as a fallback when no project/solution files are found for MSBuild evaluation.
    /// </summary>
    internal static List<string> DiscoverBuildPropsFilesOnDisk(string startPath) {
        var results = new List<string>();
        var currentDir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);

        while (!string.IsNullOrEmpty(currentDir)) {
            var propsFile = Path.Combine(currentDir, "Directory.Build.props");
            if (File.Exists(propsFile))
                results.Add(Path.GetFullPath(propsFile));

            var parent = Path.GetDirectoryName(currentDir);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, currentDir, StringComparison.OrdinalIgnoreCase))
                break;
            currentDir = parent;
        }

        return results;
    }

    private int FallbackDiscovery(string rootPath) {
        var discovered = DiscoverBuildPropsFilesOnDisk(rootPath);
        if (discovered.Count == 0) {
            _console.WriteLine("No Directory.Build.props files found.");
        }
        else {
            _console.WriteLine($"Found {discovered.Count} Directory.Build.props file(s) on disk:");
            foreach (var file in discovered)
                _console.WriteLine($"  {file}");
        }
        return 0;
    }

    private static List<string> GetOrAdd(Dictionary<string, List<string>> dict, string key) {
        if (!dict.TryGetValue(key, out var list)) {
            list = new List<string>();
            dict[key] = list;
        }
        return list;
    }

    private static string Truncate(string value, int maxLength = 120) {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value.Substring(0, maxLength - 3) + "...";
    }

    private sealed class PropertyKeyComparer : IEqualityComparer<(string Name, string EvaluatedValue, string SourceFile)> {
        public static readonly PropertyKeyComparer Instance = new();
        public bool Equals((string Name, string EvaluatedValue, string SourceFile) x, (string Name, string EvaluatedValue, string SourceFile) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name)
            && StringComparer.Ordinal.Equals(x.EvaluatedValue, y.EvaluatedValue)
            && StringComparer.OrdinalIgnoreCase.Equals(x.SourceFile, y.SourceFile);
        public int GetHashCode((string Name, string EvaluatedValue, string SourceFile) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
                StringComparer.Ordinal.GetHashCode(obj.EvaluatedValue),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceFile));
    }

    private sealed class OverrideKeyComparer : IEqualityComparer<(string PropertyName, string SharedValue, string SharedFile, string OverrideValue, string OverrideUnevaluatedValue, string ChangeFile, bool IsMerged)> {
        public static readonly OverrideKeyComparer Instance = new();
        public bool Equals(
            (string PropertyName, string SharedValue, string SharedFile, string OverrideValue, string OverrideUnevaluatedValue, string ChangeFile, bool IsMerged) x,
            (string PropertyName, string SharedValue, string SharedFile, string OverrideValue, string OverrideUnevaluatedValue, string ChangeFile, bool IsMerged) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.PropertyName, y.PropertyName)
            && StringComparer.Ordinal.Equals(x.SharedValue, y.SharedValue)
            && StringComparer.OrdinalIgnoreCase.Equals(x.SharedFile, y.SharedFile)
            && StringComparer.Ordinal.Equals(x.OverrideValue, y.OverrideValue)
            && StringComparer.Ordinal.Equals(x.OverrideUnevaluatedValue, y.OverrideUnevaluatedValue)
            && StringComparer.OrdinalIgnoreCase.Equals(x.ChangeFile, y.ChangeFile)
            && x.IsMerged == y.IsMerged;
        public int GetHashCode(
            (string PropertyName, string SharedValue, string SharedFile, string OverrideValue, string OverrideUnevaluatedValue, string ChangeFile, bool IsMerged) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PropertyName),
                StringComparer.Ordinal.GetHashCode(obj.SharedValue),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SharedFile),
                StringComparer.Ordinal.GetHashCode(obj.OverrideValue),
                StringComparer.Ordinal.GetHashCode(obj.OverrideUnevaluatedValue),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ChangeFile),
                obj.IsMerged.GetHashCode());
    }

    private sealed class DisplayRowKeyComparer : IEqualityComparer<(string Name, string EvaluatedValue, string UnevaluatedValue, string SourceFile, string OverriddenIn, bool IsOverridden, bool IsMerged)> {
        public static readonly DisplayRowKeyComparer Instance = new();
        public bool Equals(
            (string Name, string EvaluatedValue, string UnevaluatedValue, string SourceFile, string OverriddenIn, bool IsOverridden, bool IsMerged) x,
            (string Name, string EvaluatedValue, string UnevaluatedValue, string SourceFile, string OverriddenIn, bool IsOverridden, bool IsMerged) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name)
            && StringComparer.Ordinal.Equals(x.EvaluatedValue, y.EvaluatedValue)
            && StringComparer.Ordinal.Equals(x.UnevaluatedValue, y.UnevaluatedValue)
            && StringComparer.OrdinalIgnoreCase.Equals(x.SourceFile, y.SourceFile)
            && StringComparer.OrdinalIgnoreCase.Equals(x.OverriddenIn, y.OverriddenIn)
            && x.IsOverridden == y.IsOverridden
            && x.IsMerged == y.IsMerged;
        public int GetHashCode(
            (string Name, string EvaluatedValue, string UnevaluatedValue, string SourceFile, string OverriddenIn, bool IsOverridden, bool IsMerged) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
                StringComparer.Ordinal.GetHashCode(obj.EvaluatedValue),
                StringComparer.Ordinal.GetHashCode(obj.UnevaluatedValue),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceFile),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.OverriddenIn),
                obj.IsOverridden.GetHashCode(),
                obj.IsMerged.GetHashCode());
    }

    private static Dictionary<string, string> BuildGlobalProperties(CleaningOptions options) {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(options.VSToolsPath)) dict["VSToolsPath"] = options.VSToolsPath!;
        if (!string.IsNullOrEmpty(options.VSRootPath) && Directory.Exists(Path.Combine(options.VSRootPath!, "MSBuild")))
            dict["MSBuildExtensionsPath"] = Path.Combine(options.VSRootPath!, "MSBuild");
        return dict;
    }
}
