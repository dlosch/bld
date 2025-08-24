using bld.Infrastructure;
using bld.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace bld.Services;

internal class SbomService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public SbomService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task GenerateSbomAsync(string rootPath, string outputPath, string format, bool includeTests, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);
        
        _console.WriteInfo("Starting SBOM generation...");

        // Ensure output directory exists
        Directory.CreateDirectory(outputPath);

        // Discover solutions and projects
        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

        var projects = new List<SbomProjectInfo>();
        var allPackageReferences = new Dictionary<string, SbomPackageInfo>();

        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            _console.WriteVerbose($"Processing solution: {slnPath}");
            
            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                try {
                    var projParser = new ProjParser(_console, errorSink, _options);
                    var projectInfo = projParser.LoadProject(projCfg, Array.Empty<string>());
                    
                    if (projectInfo != null) {
                        // Filter out test projects if not included
                        if (!includeTests && IsTestProject(projectInfo)) {
                            continue;
                        }

                        // Only include output projects (executables, libraries, containers, packages, tools)
                        if (IsOutputProject(projectInfo)) {
                            var sbomProject = new SbomProjectInfo {
                                Name = projectInfo.AssemblyName ?? projectInfo.ProjectName ?? Path.GetFileNameWithoutExtension(projectInfo.ProjectPath),
                                Path = projectInfo.ProjectPath,
                                TargetFramework = projectInfo.TargetFramework ?? string.Join(", ", projectInfo.TargetFrameworks ?? Array.Empty<string>()),
                                PackageId = projectInfo.PackageId,
                                PackageReferences = await ExtractPackageReferencesAsync(projectInfo.ProjectPath, cancellationToken)
                            };
                            
                            projects.Add(sbomProject);
                            
                            // Collect all unique package references
                            foreach (var packageRef in sbomProject.PackageReferences) {
                                if (!allPackageReferences.ContainsKey(packageRef.Id)) {
                                    allPackageReferences[packageRef.Id] = packageRef;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    _console.WriteWarning($"Failed to process project {projCfg.Path}: {ex.Message}");
                }
            }
        }

        _console.WriteInfo($"Found {projects.Count} projects for SBOM generation");
        _console.WriteInfo($"Found {allPackageReferences.Count} unique package references");

        // Generate SBOM in requested format(s)
        if (format.Equals("spdx", StringComparison.OrdinalIgnoreCase) || format.Equals("both", StringComparison.OrdinalIgnoreCase)) {
            await GenerateSpdxSbomAsync(projects, allPackageReferences.Values.ToList(), outputPath, cancellationToken);
        }

        if (format.Equals("cyclonedx", StringComparison.OrdinalIgnoreCase) || format.Equals("both", StringComparison.OrdinalIgnoreCase)) {
            await GenerateCycloneDxSbomAsync(projects, allPackageReferences.Values.ToList(), outputPath, cancellationToken);
        }
    }

    private async Task GenerateSpdxSbomAsync(List<SbomProjectInfo> projects, List<SbomPackageInfo> packages, string outputPath, CancellationToken cancellationToken) {
        _console.WriteInfo("Generating SPDX SBOM...");

        try {
            // Create a comprehensive SPDX SBOM document in JSON format
            var sbom = new {
                spdxVersion = "SPDX-2.3",
                dataLicense = "CC0-1.0",
                SPDXID = "SPDXRef-DOCUMENT",
                name = $"{Path.GetFileName(Path.GetFullPath(outputPath))}-SBOM",
                documentNamespace = $"https://bld.tool/sbom/{Guid.NewGuid()}",
                creationInfo = new {
                    created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    creators = new[] { "Tool: bld-0.1.1" }
                },
                packages = new object[] { }
                    .Concat(projects.Select(project => new {
                        SPDXID = $"SPDXRef-{project.Name.Replace(" ", "-").Replace(".", "-")}",
                        name = project.Name,
                        downloadLocation = "NOASSERTION",
                        filesAnalyzed = false,
                        licenseConcluded = "NOASSERTION",
                        licenseDeclared = "NOASSERTION",
                        copyrightText = "NOASSERTION",
                        comment = $"Project Path: {project.Path}, Target Framework: {project.TargetFramework}" + 
                                 (!string.IsNullOrEmpty(project.PackageId) ? $", Package ID: {project.PackageId}" : "")
                    }))
                    .Concat(packages.OrderBy(p => p.Id).Select(package => new {
                        SPDXID = $"SPDXRef-{package.Id.Replace(".", "-")}",
                        name = package.Id,
                        downloadLocation = $"https://www.nuget.org/packages/{package.Id}/{package.Version}",
                        filesAnalyzed = false,
                        licenseConcluded = "NOASSERTION",
                        licenseDeclared = "NOASSERTION",
                        copyrightText = "NOASSERTION",
                        comment = $"Version: {package.Version}"
                    })).ToArray()
            };

            var json = JsonSerializer.Serialize(sbom, new JsonSerializerOptions {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var spdxPath = Path.Combine(outputPath, "spdx-sbom.json");
            await File.WriteAllTextAsync(spdxPath, json, cancellationToken);

            _console.WriteInfo($"SPDX SBOM generated successfully at: {spdxPath}");
        }
        catch (Exception ex) {
            _console.WriteError($"Error generating SPDX SBOM: {ex.Message}");
        }
    }

    private async Task GenerateCycloneDxSbomAsync(List<SbomProjectInfo> projects, List<SbomPackageInfo> packages, string outputPath, CancellationToken cancellationToken) {
        _console.WriteInfo("Generating CycloneDX SBOM...");

        try {
            // Create a simple CycloneDX-compatible JSON structure
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
                    name = project.Name,
                    version = "1.0.0",
                    scope = "required",
                    purl = string.IsNullOrEmpty(project.PackageId) 
                        ? null 
                        : $"pkg:nuget/{project.PackageId}@1.0.0"
                }).Concat(packages.Select(package => new {
                    type = "library",
                    name = package.Id,
                    version = package.Version,
                    scope = "required",
                    purl = (string?)$"pkg:nuget/{package.Id}@{package.Version}"
                })).ToArray()
            };

            var json = JsonSerializer.Serialize(bom, new JsonSerializerOptions {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            var cycloneDxPath = Path.Combine(outputPath, "cyclonedx-sbom.json");
            await File.WriteAllTextAsync(cycloneDxPath, json, cancellationToken);

            _console.WriteInfo($"CycloneDX SBOM generated successfully at: {cycloneDxPath}");
        }
        catch (Exception ex) {
            _console.WriteError($"Error generating CycloneDX SBOM: {ex.Message}");
        }
    }

    private async Task<List<SbomPackageInfo>> ExtractPackageReferencesAsync(string projectPath, CancellationToken cancellationToken) {
        var packageReferences = new List<SbomPackageInfo>();

        try {
            using var stream = File.OpenRead(projectPath);
            var doc = await System.Xml.Linq.XDocument.LoadAsync(stream, System.Xml.Linq.LoadOptions.None, cancellationToken);
            var packageRefElements = doc.Descendants("PackageReference");

            foreach (var element in packageRefElements) {
                var includeAttr = element.Attribute("Include");
                var versionAttr = element.Attribute("Version");

                if (includeAttr?.Value != null && versionAttr?.Value != null) {
                    packageReferences.Add(new SbomPackageInfo {
                        Id = includeAttr.Value,
                        Version = versionAttr.Value
                    });
                }
            }
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to parse project file {projectPath}: {ex.Message}");
        }

        return packageReferences;
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
                              new[] { project.TargetFramework }.Where(tf => !string.IsNullOrEmpty(tf)).ToList();
        
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

    private class SbomProjectInfo {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string TargetFramework { get; set; } = string.Empty;
        public string? PackageId { get; set; }
        public List<SbomPackageInfo> PackageReferences { get; set; } = new();
    }

    private class SbomPackageInfo {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }
}