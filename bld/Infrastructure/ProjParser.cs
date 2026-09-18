

using bld.Models;
using bld.Services;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using NuGet.Versioning;
using System.Collections.Concurrent;

namespace bld.Infrastructure;

internal record class Pkg(string Id, string? Version, string? VersionOverride = default, string? CpmVersion = default, PackageItemKind Kind = PackageItemKind.PackageReference) {
    public string EffectiveVersion => VersionOverride ?? Version ?? CpmVersion ?? string.Empty;
};

internal record class PackageVersionEntry(string? Version, string? SourceFile);

internal record class ProjectPackageReferenceInfo(
        ProjCfg Proj,
        string[] TargetFrameworks,
        bool? UseCpm,
        string? CpmFile,
        Dictionary<string, Pkg> PackageReferences,
        Dictionary<string, PackageVersionEntry>? PackageVersions,
        IReadOnlyList<string> ProjectReferences,
        // The project file and every import outside the SDK: what the evaluation cache hashes.
        IReadOnlyList<string> ContributingFiles) {
    // FirstOrDefault, not First: a project with no TargetFramework/TargetFrameworks/TargetFrameworkVersion
    // (a .vcxproj carrying PackageReferences, say) threw here from inside Parallel.ForEachAsync, which
    // cancelled every project not yet scanned while the run carried on with the partial result.
    public string TargetFramework => TargetFrameworks.FirstOrDefault() ?? string.Empty;
}
internal record class ProjectPackage(string PackageId, string? Version);

internal sealed class ProjParser(IConsoleOutput Output, ErrorSink ErrorSink, CleaningOptions Options) : IDisposable {

    // A ProjectCollection caches every props/targets file it has imported. With a fresh collection
    // per evaluation the whole SDK import chain was read and parsed again for every project. One
    // collection serves every evaluation of this parser, concurrently: that is how MSBuild's own
    // static graph evaluates projects in parallel, and the collection's project list, toolsets and
    // import cache are locked internally.
    private readonly ConcurrentBag<ProjectCollection> _collections = new();

    // Shared across every evaluation of this parser: SDK resolution and file system lookups are
    // cached, so the SDK is located once per run instead of once per project. Nothing changes on
    // disk while a parser is alive, which is what makes the sharing safe.
    private readonly EvaluationContext _evaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);

    /// <summary>When set, <see cref="GetPackageReferences"/> answers from the cache where it can.</summary>
    internal EvaluationCache? Cache { get; set; }

    private static string? _toolsIdentity;
    private static string? _toolsDirectory;

    /// <summary>The MSBuild that evaluates, as path and version. Only valid once MSBuild is registered.</summary>
    internal static string ToolsIdentity => _toolsIdentity ??= $"{typeof(Project).Assembly.Location}|{typeof(Project).Assembly.GetName().Version}";

    private static string ToolsDirectory => _toolsDirectory ??= Path.GetDirectoryName(typeof(Project).Assembly.Location) ?? string.Empty;

    // Files under the SDK or a VS installation are covered by the tools identity in the cache key;
    // hashing hundreds of them per project would cost what the cache is meant to save.
    private bool IsToolsFile(string path) =>
        path.StartsWith(ToolsDirectory, StringComparison.OrdinalIgnoreCase)
        || (Options.VSRootPath is { Length: > 0 } vsRoot && path.StartsWith(vsRoot, StringComparison.OrdinalIgnoreCase));

    private List<string> ContributingFiles(Project project, string projectPath) {
        var files = new List<string> { projectPath };
        foreach (var import in project.Imports) {
            var path = import.ImportedProject?.FullPath;
            if (string.IsNullOrEmpty(path) || IsToolsFile(path)) continue;
            if (!files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
        }
        return files;
    }

    private T Evaluate<T>(string projectPath, IDictionary<string, string> properties, Func<Project, T> read) {
        if (!_collections.TryTake(out var collection)) collection = new ProjectCollection();
        try {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var project = Project.FromFile(projectPath, new ProjectOptions {
                GlobalProperties = properties,
                ProjectCollection = collection,
                EvaluationContext = _evaluationContext
            });
            Output.WriteDebug($"Evaluated {projectPath} in {watch.ElapsedMilliseconds} ms");
            try {
                return read(project);
            }
            finally {
                // Loaded projects pin their evaluation state; the imports stay cached after this.
                collection.UnloadProject(project);
            }
        }
        finally {
            _collections.Add(collection);
        }
    }

    public void Dispose() {
        while (_collections.TryTake(out var collection)) {
            collection.UnloadAllProjects();
            collection.Dispose();
        }
    }

    private Dictionary<string, string> _globalProperties = default!;

    private Dictionary<string, string> GlobalProperties => _globalProperties ??= Init(Options);

    private static Dictionary<string, string> Init(CleaningOptions Options) {
        var dict = new Dictionary<string, string>(3);
        // The SDK's default globs (**/*.cs and friends) walk the whole project tree on every
        // evaluation, and nothing read here - properties, package and project items - depends on
        // them.
        dict["EnableDefaultItems"] = "false";
        if (Options.VSToolsPath is { }) dict["VSToolsPath"] = Options.VSToolsPath;
        if (Options.VSRootPath is { } && Directory.Exists(Path.Combine(Options.VSRootPath, "MSBuild"))) dict["MSBuildExtensionsPath"] = Path.Combine(Options.VSRootPath, "MSBuild");

        return dict;
    }

    /// <summary>
    /// Reads item metadata by name. MSBuild metadata names are case-insensitive, so the previous
    /// `meta.Name == "Version"` comparison missed a lowercase `version="1.2.3"` attribute and the
    /// package was then reported with no version at all.
    /// </summary>
    private static string? Meta(ProjectItem item, string name) {
        var value = item.GetMetadataValue(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal ProjectPackageReferenceInfo? GetPackageReferences(ProjCfg proj) {
        Output.WriteDebug($"Loading project {proj.Path} [{proj.Configuration}]...");
        string projectPath = proj.Path;
        string? configuration = proj.Configuration;

        var properties = BuildProperties(configuration, proj.Platform);
        if (Cache is { } cache && cache.TryGet(proj, properties) is { } cached) return cached;

        try {
            var info = Evaluate(projectPath, properties, project => {
                var usesCpm = SafeBool(project.GetPropertyValue("ManagePackageVersionsCentrally"));

                var packageVersionItems = usesCpm ?? false
                    ? project.GetItems("PackageVersion")
                    : null;

                var versions = packageVersionItems is null
                    ? null
                    : packageVersionItems
                        .DistinctBy(pr => pr.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                                pr => pr.EvaluatedInclude
                                , pr => new PackageVersionEntry(
                                    Meta(pr, "Version"),
                                    pr.Xml?.ContainingProject?.FullPath)
                                , StringComparer.OrdinalIgnoreCase);

                // NuGet.targets turns every GlobalPackageReference into a PackageReference plus a
                // PackageVersion item, and both carry NuGet.targets as their containing file. Re-point
                // the version entry at the file that declares the GlobalPackageReference, or --apply
                // would look for the entry inside the SDK and write nothing.
                var globalItems = project.GetItems("GlobalPackageReference")
                    .DistinctBy(g => g.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.EvaluatedInclude, g => g, StringComparer.OrdinalIgnoreCase);
                if (versions is not null) {
                    foreach (var (id, item) in globalItems) {
                        versions[id] = new PackageVersionEntry(Meta(item, "Version"), item.Xml?.ContainingProject?.FullPath);
                    }
                }

                // Determine the CPM file path. Prefer the actual file that declares the
                // PackageVersion items (works for non-standard CPM filenames or files imported
                // outside the standard auto-import chain). Fall back to the "Directory.Packages.props"
                // import lookup, then to null when no source can be determined.
                string? cpmFile = null;
                if (usesCpm ?? false) {
                    if (versions is not null) {
                        cpmFile = versions.Values
                            .Select(pv => pv.SourceFile)
                            .Where(p => !string.IsNullOrEmpty(p))
                            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Count())
                            .Select(g => g.Key)
                            .FirstOrDefault();
                    }
                    if (string.IsNullOrEmpty(cpmFile)) {
                        cpmFile = project.Imports
                            .FirstOrDefault(imp => string.Equals(Path.GetFileName(imp.ImportedProject?.FullPath), "Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                            .ImportedProject?.FullPath;
                    }
                }

                // EvaluatedInclude, not Xml.Include: the raw attribute keeps property references
                // ("$(Prefix)soft.Json") and, for a multi-id include ("A;B"), is the same string on
                // every produced item - so one of them was dropped by DistinctBy and the package id
                // sent to NuGet was one that does not exist. Orphan detection keys off this same
                // dictionary, so a raw id there meant the matching PackageVersion looked unused.
                // dotnet build picks the first duplicate, not the highest or lowest, and warns only.
                var packageReferences = project.GetItems("PackageReference")
                    .DistinctBy(pr => pr.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pr => pr.EvaluatedInclude, pr =>
                        new Pkg(pr.EvaluatedInclude
                            , Meta(pr, "Version")
                            , Meta(pr, "VersionOverride")
                            , versions?.GetValueOrDefault(pr.EvaluatedInclude)?.Version
                            , globalItems.ContainsKey(pr.EvaluatedInclude) ? PackageItemKind.GlobalPackageReference : PackageItemKind.PackageReference)
                        , StringComparer.OrdinalIgnoreCase);

                // PackageDownload pins exact versions in brackets, possibly several ("[1.2.3];[2.0.0]").
                // The highest one is what an update is measured against.
                foreach (var download in project.GetItems("PackageDownload").DistinctBy(pd => pd.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)) {
                    var id = download.EvaluatedInclude;
                    if (string.IsNullOrWhiteSpace(id) || packageReferences.ContainsKey(id)) continue;
                    var highest = HighestExactVersion(Meta(download, "Version"));
                    if (highest is null) {
                        Output.WriteDebug($"PackageDownload {id} in {projectPath} has no parseable version; skipped.");
                        continue;
                    }
                    packageReferences[id] = new Pkg(id, highest.ToString(), Kind: PackageItemKind.PackageDownload);
                }

                // ProjectReferences come out of the same evaluation: reading them separately cost
                // a second full evaluation of every project.
                return new ProjectPackageReferenceInfo(proj,
                    project.TfmOrTfmsSafe(),
                    usesCpm,
                    cpmFile,
                    packageReferences,
                    versions,
                    ReadProjectReferences(project, projectPath),
                    ContributingFiles(project, projectPath)
                );
            });
            Cache?.Store(info, properties);
            return info;
        }
        catch (Exception xcptn) {
            ErrorSink.AddError($"Failed to load project.", exception: xcptn, config: proj);
            Output.WriteError($"{projectPath} could not be parsed: {xcptn.FormatMessage()}");
            return default;
        }
    }

    private Dictionary<string, string> BuildProperties(string? configuration, string? platform) {
        var properties = new Dictionary<string, string>(GlobalProperties);
        if (!string.IsNullOrEmpty(configuration)) properties["Configuration"] = configuration;
        // Platform matters for .vcxproj, whose output path is <Platform>\<Configuration>\. Without
        // it every platform evaluated identically and only the default one was ever cleaned.
        if (!string.IsNullOrEmpty(platform)) properties["Platform"] = platform;
        return properties;
    }

    // Path.Combine returns rel unchanged when rooted; GetFullPath normalizes either way
    // so paths containing '..' dedupe correctly via OrdinalIgnoreCase.
    private static string[] ReadProjectReferences(Project project, string projectPath) {
        var projDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
        return project.GetItems("ProjectReference")
            .Select(pr => pr.EvaluatedInclude)
            .Where(rel => !string.IsNullOrWhiteSpace(rel))
            .Select(rel => Path.GetFullPath(Path.Combine(projDir, rel)))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal IReadOnlyList<string> GetProjectReferences(string projectPath, string? configuration = null, string? platform = null) {
        try {
            return Evaluate(projectPath, BuildProperties(configuration, platform), project => ReadProjectReferences(project, projectPath));
        }
        catch (Exception xcptn) {
            ErrorSink.AddError($"Failed to evaluate ProjectReferences for {projectPath}.", exception: xcptn);
            Output.WriteDebug($"{projectPath} could not be parsed for ProjectReferences: {xcptn.FormatMessage()}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The highest version in a PackageDownload Version value: a ';'-separated list of exact ranges
    /// such as "[8.0.0]" or "[1.2.3];[2.0.0]". Null when nothing parses.
    /// </summary>
    internal static NuGetVersion? HighestExactVersion(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        NuGetVersion? best = null;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!VersionRange.TryParse(part, out var range) || range.MinVersion is null) continue;
            if (best is null || range.MinVersion > best) best = range.MinVersion;
        }
        return best;
    }

    static bool? SafeBool(string value) => value is string && !string.IsNullOrEmpty(value) && bool.TryParse(value, out var bl) ? bl : default;
    static string? Safe(string value) => value is string && !string.IsNullOrEmpty(value) ? value : default;
    static string? SafeDir(string value) {
        var value2 = Safe(value);
        if (value2 is null) return default;
        value = value2;

        if (Path.DirectorySeparatorChar != '\\') {
            value = value.Replace('\\', Path.DirectorySeparatorChar);
        }
        return value;
    }
    internal ProjectInfo? LoadProject(ProjCfg proj, string[] propertyNames) {
        string projectPath = proj.Path;
        string? configuration = proj.Configuration;

        try {
            return Evaluate(projectPath, BuildProperties(configuration, proj.Platform), project => new ProjectInfo {
                ProjectPath = projectPath,
                ProjectName = Safe(project.GetPropertyValue("ProjectName")),
                AssemblyName = Safe(project.GetPropertyValue("AssemblyName")),
                TargetFramework = Safe(project.GetPropertyValue("TargetFramework")),
                TargetFrameworks = project.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList(),
                Configuration = configuration,
                Platform = Safe(project.GetPropertyValue("Platform")),
                OutDir = SafeDir(project.GetPropertyValue("OutDir")),
                BaseOutputPath = SafeDir(project.GetPropertyValue("BaseOutputPath")),
                IntermediateOutputPath = SafeDir(project.GetPropertyValue("BaseIntermediateOutputPath")),
                PackageOutputPath = SafeDir(project.GetPropertyValue("PackageOutputPath")),
                PublishDir = SafeDir(project.GetPropertyValue("PublishDir")),
                UseArtifactsOutput = SafeBool(project.GetPropertyValue("UseArtifactsOutput")) ?? false,
                ArtifactsPath = SafeDir(project.GetPropertyValue("ArtifactsPath")),
                ArtifactsBinOutputName = Safe(project.GetPropertyValue("ArtifactsBinOutputName")),
                ArtifactsPublishOutputName = Safe(project.GetPropertyValue("ArtifactsPublishOutputName")),
                ArtifactsProjectName = Safe(project.GetPropertyValue("ArtifactsProjectName")),
                PackageId = Safe(project.GetPropertyValue("PackageId")),
                Properties = propertyNames.ToDictionary(p => p, p => project.GetPropertyValue(p)),
            });
        }
        catch (Exception xcptn) {
            ErrorSink.AddError($"Failed to load project.", exception: xcptn, config: proj);
            Output.WriteError($"{projectPath} could not be parsed: {xcptn.FormatMessage()}");
            return default;
        }
    }
}

internal static class ProjParserExtensions {

    // 


    internal static string[] TfmOrTfmsSafe(this Project project, bool FxProjStyleInclude = true) {
        var targetFramework = project.GetPropertyValue("TargetFramework");
        if (!string.IsNullOrEmpty(targetFramework)) {
            return [targetFramework];
        }

        var targetFrameworks = project.GetPropertyValue("TargetFrameworks");
        if (!string.IsNullOrEmpty(targetFrameworks)) {
            return targetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries).ToArray();
        }

        if (!FxProjStyleInclude) return Array.Empty<string>();

        var targetFrameworkVersion = project.GetPropertyValue("TargetFrameworkVersion");
        if (!string.IsNullOrEmpty(targetFrameworkVersion)) {
            return [targetFrameworkVersion];
        }

        return Array.Empty<string>();
    }
}
