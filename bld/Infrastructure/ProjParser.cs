

using bld.Models;
using bld.Services;
using Microsoft.Build.Evaluation;
using System;
using System.Configuration;

namespace bld.Infrastructure;
internal record class ProjectPackageReferenceInfo(ProjCfg Proj, string? TargetFramework, bool? UseCpm, string? CpmFile, Dictionary<string, string?> PackageReferences, Dictionary<string, string?>? PackageVersions);
//internal record class ProjectPackageReferenceInfo(ProjCfg Proj, string? TargetFramework, bool? UseCpm, string? CpmFile, IEnumerable<ProjectPackage> PackageReferences, IEnumerable<ProjectPackage>? PackageVersions);
internal record class ProjectPackage(string PackageId, string? Version);
//internal class ProjectPackageVersion(string PackageId, string Version);

internal sealed class ProjParser(IConsoleOutput Console, ErrorSink ErrorSink, CleaningOptions Options) {

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



    internal void SetPackageReferences(ProjCfg proj, ProjectPackageReferenceInfo info) {
        string projectPath = proj.Path;
        string? configuration = proj.Configuration;

        using (var projectCollection = new ProjectCollection()) {
            var properties = new Dictionary<string, string>(GlobalProperties);
            if (!string.IsNullOrEmpty(configuration)) {
                properties["Configuration"] = configuration;
            }
            
            try {
                var project = new Project(projectPath, properties, null, projectCollection);
                
                // Remove existing PackageReference items that we're updating
                var existingRefs = project.GetItems("PackageReference")
                    .Where(item => info.PackageReferences.ContainsKey(item.EvaluatedInclude))
                    .ToList();
                
                foreach (var existingRef in existingRefs) {
                    project.RemoveItem(existingRef);
                }
                
                // Add updated PackageReference items
                foreach (var pr in info.PackageReferences) {
                    var item = project.AddItem("PackageReference", pr.Key);
                    if (!string.IsNullOrEmpty(pr.Value)) {
                        item[0].SetMetadataValue("Version", pr.Value);
                    }
                }
                
                // Save the modified project file
                project.Save();
                Console.WriteInfo($"Updated {info.PackageReferences.Count} package reference(s) in {Path.GetFileName(projectPath)}");
            }
            catch (Exception ex) {
                Console.WriteError($"Failed to update project {projectPath}: {ex.Message}");
            }
        }
    }

    internal ProjectPackageReferenceInfo GetPackageReferences(ProjCfg proj) {
        Console.WriteDebug($"Loading project {proj.Path} [{proj.Configuration}]...");
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
                var retVal = new ProjectPackageReferenceInfo(proj,
                    Safe(project.GetPropertyValue("TargetFramework")),
                    usesCpm,
                    (usesCpm ?? false) 
                        ? project.Imports.FirstOrDefault(imp => string.Equals(Path.GetFileName(imp.ImportedProject.FullPath), "Directory.Packages.props", StringComparison.OrdinalIgnoreCase)).ImportedProject?.FullPath
                        : default,
                    // todo this pukes if a single package reference include is included more than once 
                    // dotnet build picks the first not the highest or lowest and warns only
                    project.GetItems("PackageReference").ToDictionary(pr => pr.Xml.Include, pr => pr.Metadata?.FirstOrDefault(meta => meta.Name == "Version")?.EvaluatedValue, StringComparer.OrdinalIgnoreCase),
                    usesCpm ?? false ? 
                        project.GetItems("PackageVersion")?.ToDictionary(pr => pr.Xml.Include, pr => pr.Metadata?.FirstOrDefault(meta => meta.Name == "Version")?.EvaluatedValue, StringComparer.OrdinalIgnoreCase)
                        : default
                //project.GetItems("PackageReference").Select(pr => new ProjectPackage(pr.Xml.Include, pr.Metadata?.FirstOrDefault(meta => meta.Name == "Version")?.EvaluatedValue)),
                //project.GetItems("PackageVersion")?.Select(pr => new ProjectPackage(pr.Xml.Include, pr.Metadata?.FirstOrDefault(meta => meta.Name == "Version")?.EvaluatedValue))
                );
                return retVal;
                //tfm = ;
                //var useCpm = ;
                //if (useCpm ?? false) {
                //    string directoryPackagesPropsPath = null;
                //    foreach (var import in project.Imports) {
                //        if (import.ImportedProject.FullPath.EndsWith("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)) {
                //            Console.WriteInfo($"Directory.Packages.props {import.ImportedProject.FullPath}");
                //            directoryPackagesPropsPath = import.ImportedProject.FullPath;
                //            //break;
                //        }
                //    }
                //}
                
                //var vers = project.GetItems("PackageVersion").ToList();
                //return project.GetItems("PackageReference").ToList();
            }
            catch (Exception xcptn) {
                ErrorSink.AddError($"Failed to load project.", exception: xcptn, config: proj);
                Console.WriteError($"{projectPath} could not be parsed: {xcptn.Message}.");
                return default;
            }

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
                Console.WriteError($"{projectPath} could not be parsed: {xcptn.Message}.");
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
                BaseOutputPath = Safe(project.GetPropertyValue("BaseOutputPath")),
                IntermediateOutputPath = Safe(project.GetPropertyValue("BaseIntermediateOutputPath")),
                PackageOutputPath = Safe(project.GetPropertyValue("PackageOutputPath")),
                PackageId = Safe(project.GetPropertyValue("PackageId")),
                Properties = propertyNames.ToDictionary(p => p, p => project.GetPropertyValue(p)),
            };

            return info;
        }
    }
}
