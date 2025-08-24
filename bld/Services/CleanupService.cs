using bld.Infrastructure;
using bld.Models;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;

internal class CleanupService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public CleanupService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> AnalyzePackageReferencesAsync(string rootPath, bool removeRedundant, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);
        
        _console.WriteInfo("Analyzing package references for redundant dependencies...");

        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

        var projectInfos = new List<ProjectInfo>();

        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            _console.WriteVerbose($"Processing solution: {slnPath}");
            
            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                var projectPath = projCfg.Path;
                var projectInfo = await AnalyzeProjectAsync(projectPath, cancellationToken);
                if (projectInfo != null) {
                    projectInfos.Add(projectInfo);
                }
            }
        }

        if (projectInfos.Count == 0) {
            _console.WriteInfo("No projects found.");
            return 0;
        }

        _console.WriteInfo($"Analyzed {projectInfos.Count} projects");

        var redundantReferences = new List<RedundantReferenceInfo>();

        // Analyze each project for redundant references
        foreach (var projectInfo in projectInfos) {
            var redundant = await FindRedundantReferencesAsync(projectInfo, cancellationToken);
            redundantReferences.AddRange(redundant);
        }

        if (redundantReferences.Count == 0) {
            _console.WriteInfo("No redundant package references found!");
            return 0;
        }

        _console.WriteInfo($"\nFound {redundantReferences.Count} potentially redundant package references:");
        
        var groupedByProject = redundantReferences.GroupBy(r => r.ProjectPath);
        foreach (var projectGroup in groupedByProject.OrderBy(g => g.Key)) {
            _console.WriteWarning($"\n{Path.GetFileName(projectGroup.Key)}:");
            foreach (var redundant in projectGroup.OrderBy(r => r.PackageId)) {
                _console.WriteInfo($"  {redundant.PackageId} v{redundant.Version}");
                _console.WriteVerbose($"    Reason: {redundant.Reason}");
                if (redundant.TransitiveFrom.Any()) {
                    _console.WriteVerbose($"    Transitive from: {string.Join(", ", redundant.TransitiveFrom)}");
                }
            }
        }

        if (removeRedundant) {
            _console.WriteInfo("\nRemoving redundant package references...");
            var removedCount = 0;
            
            foreach (var projectGroup in groupedByProject) {
                var removed = await RemoveRedundantReferencesAsync(projectGroup.Key, projectGroup.ToList(), cancellationToken);
                removedCount += removed;
                if (removed > 0) {
                    _console.WriteInfo($"Removed {removed} redundant references from {Path.GetFileName(projectGroup.Key)}");
                }
            }
            
            _console.WriteInfo($"Removed {removedCount} redundant package references total");
        } else {
            _console.WriteInfo("\nUse --update to apply these changes.");
        }

        return 0;
    }

    private async Task<ProjectInfo?> AnalyzeProjectAsync(string projectPath, CancellationToken cancellationToken) {
        try {
            _console.WriteVerbose($"Analyzing project: {Path.GetFileName(projectPath)}");
            
            var doc = await XDocument.LoadAsync(File.OpenRead(projectPath), LoadOptions.None, cancellationToken);
            var targetFramework = doc.Descendants("TargetFramework").FirstOrDefault()?.Value ??
                                 doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value?.Split(';')[0];

            if (string.IsNullOrEmpty(targetFramework)) {
                _console.WriteWarning($"Could not determine target framework for {Path.GetFileName(projectPath)}");
                return null;
            }

            var packageReferences = new List<PackageReferenceInfo>();
            var packageRefElements = doc.Descendants("PackageReference");

            foreach (var element in packageRefElements) {
                var include = element.Attribute("Include")?.Value;
                var version = element.Attribute("Version")?.Value ?? 
                             element.Element("Version")?.Value;

                if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version)) {
                    packageReferences.Add(new PackageReferenceInfo {
                        Id = include,
                        Version = version
                    });
                }
            }

            return new ProjectInfo {
                Path = projectPath,
                TargetFramework = targetFramework,
                PackageReferences = packageReferences
            };
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to analyze {projectPath}: {ex.Message}");
            return null;
        }
    }

    private async Task<List<RedundantReferenceInfo>> FindRedundantReferencesAsync(ProjectInfo projectInfo, CancellationToken cancellationToken) {
        var redundantReferences = new List<RedundantReferenceInfo>();

        try {
            // Simple heuristic-based analysis for now
            // In a full implementation, this would use NuGet dependency resolution
            
            var packageIds = projectInfo.PackageReferences.Select(p => p.Id.ToLowerInvariant()).ToHashSet();
            
            // Check for some common transitive dependencies that are often explicitly referenced
            var commonTransitives = new Dictionary<string, string[]> {
                ["system.text.json"] = new[] { "microsoft.aspnetcore.app", "microsoft.aspnetcore.all" },
                ["newtonsoft.json"] = new[] { "microsoft.aspnetcore.mvc" },
                ["system.memory"] = new[] { "system.text.json", "microsoft.extensions.logging" },
                ["system.runtime.compilerservices.unsafe"] = new[] { "system.text.json", "system.memory" },
                ["microsoft.extensions.dependencyinjection.abstractions"] = new[] { "microsoft.extensions.dependencyinjection", "microsoft.aspnetcore.app" },
                ["microsoft.extensions.logging.abstractions"] = new[] { "microsoft.extensions.logging", "microsoft.aspnetcore.app" },
                ["microsoft.extensions.options"] = new[] { "microsoft.extensions.dependencyinjection", "microsoft.aspnetcore.app" },
                ["microsoft.extensions.configuration.abstractions"] = new[] { "microsoft.extensions.configuration", "microsoft.aspnetcore.app" }
            };

            foreach (var packageRef in projectInfo.PackageReferences) {
                var packageIdLower = packageRef.Id.ToLowerInvariant();
                
                if (commonTransitives.TryGetValue(packageIdLower, out var transitiveProviders)) {
                    var providingPackages = transitiveProviders.Where(provider => packageIds.Contains(provider.ToLowerInvariant())).ToList();
                    
                    if (providingPackages.Any()) {
                        redundantReferences.Add(new RedundantReferenceInfo {
                            ProjectPath = projectInfo.Path,
                            PackageId = packageRef.Id,
                            Version = packageRef.Version,
                            Reason = "Likely transitive dependency",
                            TransitiveFrom = providingPackages
                        });
                    }
                }
            }

            // Check for duplicate/similar packages
            var duplicateGroups = projectInfo.PackageReferences
                .GroupBy(p => GetPackageBaseName(p.Id))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicateGroups) {
                var packages = group.OrderBy(p => p.Id).ToList();
                // Mark all but the first as potentially redundant
                for (int i = 1; i < packages.Count; i++) {
                    redundantReferences.Add(new RedundantReferenceInfo {
                        ProjectPath = projectInfo.Path,
                        PackageId = packages[i].Id,
                        Version = packages[i].Version,
                        Reason = $"Potential duplicate of {packages[0].Id}",
                        TransitiveFrom = new[] { packages[0].Id }
                    });
                }
            }
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to analyze dependencies for {Path.GetFileName(projectInfo.Path)}: {ex.Message}");
        }

        return redundantReferences;
    }

    private string GetPackageBaseName(string packageId) {
        // Simple heuristic to group related packages
        var lower = packageId.ToLowerInvariant();
        
        if (lower.StartsWith("microsoft.extensions.")) {
            var parts = lower.Split('.');
            if (parts.Length > 2) {
                return string.Join(".", parts.Take(3)); // e.g., microsoft.extensions.logging
            }
        }
        
        if (lower.StartsWith("system.")) {
            var parts = lower.Split('.');
            if (parts.Length > 1) {
                return string.Join(".", parts.Take(2)); // e.g., system.text
            }
        }

        return lower;
    }

    private async Task<int> RemoveRedundantReferencesAsync(string projectPath, List<RedundantReferenceInfo> redundantRefs, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }
            var removedCount = 0;

            foreach (var redundantRef in redundantRefs) {
                var packageRefElements = doc.Descendants("PackageReference")
                    .Where(e => e.Attribute("Include")?.Value == redundantRef.PackageId)
                    .ToList();

                foreach (var element in packageRefElements) {
                    element.Remove();
                    removedCount++;
                    _console.WriteVerbose($"Removed {redundantRef.PackageId} from {Path.GetFileName(projectPath)}");
                }
            }

            if (removedCount > 0) {
                await using var stream = File.Create(projectPath);
                using var writer = XmlWriter.Create(stream, new XmlWriterSettings {
                    Indent = true,
                    OmitXmlDeclaration = true,
                    Encoding = System.Text.Encoding.UTF8,
                    Async = true
                });
                await doc.SaveAsync(writer, cancellationToken);
            }

            return removedCount;
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to remove redundant references from {projectPath}: {ex.Message}");
            return 0;
        }
    }

    private class ProjectInfo {
        public string Path { get; set; } = string.Empty;
        public string TargetFramework { get; set; } = string.Empty;
        public List<PackageReferenceInfo> PackageReferences { get; set; } = new();
    }

    private class PackageReferenceInfo {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    private class RedundantReferenceInfo {
        public string ProjectPath { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public IEnumerable<string> TransitiveFrom { get; set; } = Enumerable.Empty<string>();
    }
}