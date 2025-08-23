using bld.Infrastructure;
using bld.Models;
using System.Text.Json;

namespace bld.Services;

internal class SbomService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public SbomService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    public async Task GenerateSbomAsync(string rootPath, string outputPath, string format, bool includeTests, CancellationToken cancellationToken) {
        _console.WriteInfo("Starting SBOM generation...");

        // Ensure output directory exists
        Directory.CreateDirectory(outputPath);

        // Discover solutions and projects
        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

        var projects = new List<ProjectInfo>();

        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            _console.WriteVerbose($"Processing solution: {slnPath}");
            
            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                var projParser = new ProjParser(_console, errorSink, _options);
                var projectInfo = projParser.LoadProject(projCfg, Array.Empty<string>());
                
                if (projectInfo != null) {
                    // Filter out test projects if not included
                    if (!includeTests && IsTestProject(projectInfo)) {
                        continue;
                    }

                    // Only include output projects (executables, libraries, containers, packages, tools)
                    if (IsOutputProject(projectInfo)) {
                        projects.Add(projectInfo);
                    }
                }
            }
        }

        _console.WriteInfo($"Found {projects.Count} projects for SBOM generation");

        // Generate SBOM in requested format(s)
        if (format.Equals("spdx", StringComparison.OrdinalIgnoreCase) || format.Equals("both", StringComparison.OrdinalIgnoreCase)) {
            await GenerateSpdxSbomAsync(projects, outputPath, cancellationToken);
        }

        if (format.Equals("cyclonedx", StringComparison.OrdinalIgnoreCase) || format.Equals("both", StringComparison.OrdinalIgnoreCase)) {
            await GenerateCycloneDxSbomAsync(projects, outputPath, cancellationToken);
        }
    }

    private async Task GenerateSpdxSbomAsync(List<ProjectInfo> projects, string outputPath, CancellationToken cancellationToken) {
        _console.WriteInfo("Generating SPDX SBOM...");

        try {
            // For now, create a simple text-based SBOM listing projects
            var sbomContent = new List<string> {
                "# SPDX Software Bill of Materials",
                $"# Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
                $"# Total projects: {projects.Count}",
                "",
                "## Projects:"
            };

            foreach (var project in projects) {
                sbomContent.Add($"- {project.AssemblyName ?? project.ProjectName ?? Path.GetFileNameWithoutExtension(project.ProjectPath)}");
                sbomContent.Add($"  Path: {project.ProjectPath}");
                sbomContent.Add($"  Target Framework: {project.TargetFramework ?? string.Join(", ", project.TargetFrameworks)}");
                if (!string.IsNullOrEmpty(project.PackageId)) {
                    sbomContent.Add($"  Package ID: {project.PackageId}");
                }
                sbomContent.Add("");
            }

            var spdxPath = Path.Combine(outputPath, "spdx-sbom.txt");
            await File.WriteAllLinesAsync(spdxPath, sbomContent, cancellationToken);

            _console.WriteInfo($"SPDX SBOM generated successfully at: {spdxPath}");
        }
        catch (Exception ex) {
            _console.WriteError($"Error generating SPDX SBOM: {ex.Message}");
        }
    }

    private async Task GenerateCycloneDxSbomAsync(List<ProjectInfo> projects, string outputPath, CancellationToken cancellationToken) {
        _console.WriteInfo("Generating CycloneDX SBOM...");

        try {
            // Create a simple JSON structure for CycloneDX SBOM
            var bom = new {
                bomFormat = "CycloneDX",
                specVersion = "1.5",
                version = 1,
                metadata = new {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    tools = new[] {
                        new {
                            name = "bld",
                            version = "0.1.1"
                        }
                    }
                },
                components = projects.Select(project => new {
                    type = "library",
                    name = project.AssemblyName ?? project.ProjectName ?? Path.GetFileNameWithoutExtension(project.ProjectPath),
                    version = "1.0.0", // Could be extracted from project properties
                    scope = "required",
                    purl = string.IsNullOrEmpty(project.PackageId) 
                        ? null 
                        : $"pkg:nuget/{project.PackageId}@1.0.0"
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(bom, new JsonSerializerOptions {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var cycloneDxPath = Path.Combine(outputPath, "cyclonedx-sbom.json");
            await File.WriteAllTextAsync(cycloneDxPath, json, cancellationToken);

            _console.WriteInfo($"CycloneDX SBOM generated successfully at: {cycloneDxPath}");
        }
        catch (Exception ex) {
            _console.WriteError($"Error generating CycloneDX SBOM: {ex.Message}");
        }
    }

    private static bool IsTestProject(ProjectInfo project) {
        var projectName = project.ProjectName?.ToLowerInvariant() ?? "";
        var assemblyName = project.AssemblyName?.ToLowerInvariant() ?? "";
        var projectPath = project.ProjectPath.ToLowerInvariant();

        return projectName.Contains("test") || 
               assemblyName.Contains("test") || 
               projectPath.Contains("test") ||
               projectPath.Contains("tests");
    }

    private static bool IsOutputProject(ProjectInfo project) {
        // Consider projects that produce output artifacts
        var targetFrameworks = project.TargetFrameworks?.Any() == true ? project.TargetFrameworks : 
                              new[] { project.TargetFramework }.Where(tf => !string.IsNullOrEmpty(tf)).ToArray();
        
        // Skip if no target framework (probably not a valid .NET project)
        if (!targetFrameworks.Any()) {
            return false;
        }

        // Include if it has package properties (likely produces NuGet package)
        if (!string.IsNullOrEmpty(project.PackageId)) {
            return true;
        }

        // Include if it's likely an executable or library
        var projectPath = project.ProjectPath.ToLowerInvariant();
        
        // Exclude certain project types that don't produce artifacts
        if (projectPath.EndsWith(".sqlproj") || projectPath.EndsWith(".wapproj")) {
            return false;
        }

        return true;
    }
}