

using bld.Models;
using bld.Services;
using Microsoft.Build.Evaluation;
using NuGet.Versioning;

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
        Dictionary<string, PackageVersionEntry>? PackageVersions) {
    // FirstOrDefault, not First: a project with no TargetFramework/TargetFrameworks/TargetFrameworkVersion
    // (a .vcxproj carrying PackageReferences, say) threw here from inside Parallel.ForEachAsync, which
    // cancelled every project not yet scanned while the run carried on with the partial result.
    public string TargetFramework => TargetFrameworks.FirstOrDefault() ?? string.Empty;
}
internal record class ProjectPackage(string PackageId, string? Version);

internal sealed class ProjParser(IConsoleOutput Output, ErrorSink ErrorSink, CleaningOptions Options) {


    private Dictionary<string, string> _globalProperties = default!;

    private Dictionary<string, string> GlobalProperties => _globalProperties ??=
        Options.VSToolsPath is null ?
        new Dictionary<string, string>()
        : Init(Options);

    private static Dictionary<string, string> Init(CleaningOptions Options) {
        var dict = new Dictionary<string, string>(2);
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

        using (var projectCollection = new ProjectCollection()) {
            var project = default(Project);

            var properties = new Dictionary<string, string>(GlobalProperties);
            if (!string.IsNullOrEmpty(configuration)) {
                properties["Configuration"] = configuration;
            }
            // Platform matters for .vcxproj, whose output path is <Platform>\<Configuration>\. Without
            // it every platform evaluated identically and only the default one was ever cleaned.
            if (!string.IsNullOrEmpty(proj.Platform)) {
                properties["Platform"] = proj.Platform;
            }
            try {
                project = new Project(projectPath, properties, null, projectCollection);
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

                var retVal = new ProjectPackageReferenceInfo(proj,
                    project.TfmOrTfmsSafe(),
                    usesCpm,
                    cpmFile,
                    packageReferences,
                    versions
                );
                return retVal;
            }
            catch (Exception xcptn) {
                ErrorSink.AddError($"Failed to load project.", exception: xcptn, config: proj);
                Output.WriteError($"{projectPath} could not be parsed: {xcptn.FormatMessage()}");
                return default;
            }

        }
    }

    internal IReadOnlyList<string> GetProjectReferences(string projectPath, string? configuration = null, string? platform = null) {
        using var projectCollection = new ProjectCollection();
        try {
            var properties = new Dictionary<string, string>(GlobalProperties);
            if (!string.IsNullOrEmpty(configuration)) properties["Configuration"] = configuration;
            if (!string.IsNullOrEmpty(platform)) properties["Platform"] = platform;
            var project = new Project(projectPath, properties, null, projectCollection);
            var projDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
            // Path.Combine returns rel unchanged when rooted; GetFullPath normalizes either way
            // so paths containing '..' dedupe correctly via OrdinalIgnoreCase.
            return project.GetItems("ProjectReference")
                .Select(pr => pr.EvaluatedInclude)
                .Where(rel => !string.IsNullOrWhiteSpace(rel))
                .Select(rel => Path.GetFullPath(Path.Combine(projDir, rel)))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
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

        using (var projectCollection = new ProjectCollection()) {
            var project = default(Project);

            var properties = new Dictionary<string, string>(GlobalProperties);
            if (!string.IsNullOrEmpty(configuration)) {
                properties["Configuration"] = configuration;
            }
            // Platform matters for .vcxproj, whose output path is <Platform>\<Configuration>\. Without
            // it every platform evaluated identically and only the default one was ever cleaned.
            if (!string.IsNullOrEmpty(proj.Platform)) {
                properties["Platform"] = proj.Platform;
            }
            try {
                project = new Project(projectPath, properties, null, projectCollection);
            }
            catch (Exception xcptn) {
                ErrorSink.AddError($"Failed to load project.", exception: xcptn, config: proj);
                Output.WriteError($"{projectPath} could not be parsed: {xcptn.FormatMessage()}");
                return default;
            }


            var info = new ProjectInfo {
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
            };

            return info;
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
