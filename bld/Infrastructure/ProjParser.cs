

using bld.Models;
using bld.Services;
using Microsoft.Build.Evaluation;

namespace bld.Infrastructure;

internal record class Pkg(string Id, string? Version, string? VersionOverride = default, string? CpmVersion = default) {
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
    public string TargetFramework => TargetFrameworks.First();
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

    internal class Wrapper(ProjectCollection projectCollection, Project project) : IDisposable {
        public ProjectCollection ProjectCollection { get; } = projectCollection;
        public Project Project { get; } = project;
        public void Dispose() {
            ProjectCollection.UnloadProject(Project);
            ProjectCollection.Dispose();
        }

        internal static Wrapper Create(string projectPath, Dictionary<string, string> globalProperties) {
            var pc = new ProjectCollection();
            var proj = new Project(projectPath, globalProperties, null, pc);
            return new Wrapper(pc, proj);
        }
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
            try {
                project = new Project(projectPath, properties, null, projectCollection);
                var usesCpm = SafeBool(project.GetPropertyValue("ManagePackageVersionsCentrally"));

                var packageVersionItems = usesCpm ?? false
                    ? project.GetItems("PackageVersion")
                    : null;

                var versions = packageVersionItems is null
                    ? null
                    : packageVersionItems
                        .DistinctBy(pr => pr.Xml.Include, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                                pr => pr.Xml.Include
                                , pr => new PackageVersionEntry(
                                    pr.Metadata?.FirstOrDefault(meta => meta.Name == "Version")?.EvaluatedValue,
                                    pr.Xml?.ContainingProject?.FullPath)
                                , StringComparer.OrdinalIgnoreCase);

                // Determine the CPM file path. Prefer the actual file that declares the
                // PackageVersion items (works for non-standard CPM filenames or files imported
                // outside the standard auto-import chain). Fall back to the "Directory.Packages.props"
                // import lookup, then to null when no source can be determined.
                string? cpmFile = null;
                if (usesCpm ?? false) {
                    if (packageVersionItems is not null) {
                        cpmFile = packageVersionItems
                            .Select(pv => pv.Xml?.ContainingProject?.FullPath)
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

                var retVal = new ProjectPackageReferenceInfo(proj,
                    project.TfmOrTfmsSafe(),
                    usesCpm,
                    cpmFile,
                        // todo this pukes if a single package reference include is included more than once
                        // dotnet build picks the first not the highest or lowest and warns only
                        project.GetItems("PackageReference")
                                .DistinctBy(pr => pr.Xml.Include, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(pr => pr.Xml.Include, pr =>

                                    new Pkg(pr.Xml.Include
                                        , pr.Metadata?.FirstOrDefault(meta => meta.Name == "Version")?.EvaluatedValue
                                        , pr.Metadata?.FirstOrDefault(meta => meta.Name == "VersionOverride")?.EvaluatedValue
                                        , versions?.GetValueOrDefault(pr.Xml.Include)?.Version
                                        )
                                    , StringComparer.OrdinalIgnoreCase)
                                ,
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

    internal IReadOnlyList<string> GetProjectReferences(string projectPath) {
        using var projectCollection = new ProjectCollection();
        try {
            var project = new Project(projectPath, GlobalProperties, null, projectCollection);
            var projDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
            return project.GetItems("ProjectReference")
                .Select(pr => pr.EvaluatedInclude)
                .Where(rel => !string.IsNullOrWhiteSpace(rel))
                .Select(rel => Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(projDir, rel)))
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
                PackageOutputPath = Safe(project.GetPropertyValue("PackageOutputPath")),
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
