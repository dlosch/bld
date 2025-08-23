using bld.Infrastructure;
using bld.Models;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace bld.Services;

internal class CpmService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public CpmService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task ConvertToCentralPackageManagementAsync(string rootPath, bool applyChanges, bool overwrite, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);
        
        _console.WriteInfo("Starting Central Package Management conversion...");

        // Find solution file(s) to determine the root
        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

        var solutionData = new List<SolutionData>();

        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            _console.WriteVerbose($"Processing solution: {slnPath}");
            var solutionDir = Path.GetDirectoryName(slnPath)!;
            
            var allPackageReferences = new Dictionary<string, string>(); // PackageId -> Version
            var projectFiles = new List<string>();

            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                var projectPath = projCfg.Path;
                projectFiles.Add(projectPath);
                
                var packageRefs = await ExtractPackageReferencesAsync(projectPath, cancellationToken);
                foreach (var (packageId, version) in packageRefs) {
                    if (allPackageReferences.TryGetValue(packageId, out var existingVersion)) {
                        // Handle version conflicts - use higher version
                        if (CompareVersions(version, existingVersion) > 0) {
                            allPackageReferences[packageId] = version;
                            _console.WriteVerbose($"Updated {packageId} from {existingVersion} to {version}");
                        } else if (!version.Equals(existingVersion, StringComparison.OrdinalIgnoreCase)) {
                            _console.WriteWarning($"Version conflict for {packageId}: {existingVersion} vs {version}. Using {allPackageReferences[packageId]}");
                        }
                    } else {
                        allPackageReferences[packageId] = version;
                    }
                }
            }

            solutionData.Add(new SolutionData {
                SolutionPath = slnPath,
                SolutionDirectory = solutionDir,
                PackageReferences = allPackageReferences,
                ProjectFiles = projectFiles
            });
        }

        if (solutionData.Count == 0) {
            _console.WriteError("No solution files found. Central Package Management requires a solution file.");
            return;
        }

        _console.WriteInfo($"Found {solutionData.Count} solution(s) to process");

        foreach (var solution in solutionData) {
            _console.WriteInfo($"\nProcessing solution: {Path.GetFileName(solution.SolutionPath)}");
            _console.WriteInfo($"Found {solution.PackageReferences.Count} unique package references across {solution.ProjectFiles.Count} projects");

            var directoryPackagesPath = Path.Combine(solution.SolutionDirectory, "Directory.Packages.props");

            // Check if Directory.Packages.props already exists
            if (File.Exists(directoryPackagesPath) && !overwrite) {
                _console.WriteError($"Directory.Packages.props already exists at {directoryPackagesPath}. Use --overwrite to replace it.");
                continue;
            }

            if (applyChanges) {
                // Create Directory.Packages.props
                await CreateDirectoryPackagesPropsAsync(directoryPackagesPath, solution.PackageReferences, cancellationToken);
                _console.WriteInfo($"Created Directory.Packages.props with {solution.PackageReferences.Count} package versions");

                // Update all project files to remove versions
                foreach (var projectPath in solution.ProjectFiles) {
                    await UpdateProjectFileAsync(projectPath, cancellationToken);
                    _console.WriteVerbose($"Updated project file: {Path.GetFileName(projectPath)}");
                }

                _console.WriteInfo($"Updated {solution.ProjectFiles.Count} project files to use central package management");
            } else {
                _console.WriteInfo("Dry run - showing what would be created:");
                _console.WriteInfo($"Directory.Packages.props would be created at: {directoryPackagesPath}");
                _console.WriteInfo("Package versions that would be centralized:");
                
                foreach (var (packageId, version) in solution.PackageReferences.OrderBy(x => x.Key)) {
                    _console.WriteInfo($"  {packageId} = {version}");
                }
                
                _console.WriteInfo($"\n{solution.ProjectFiles.Count} project files would be updated to remove version attributes");
            }
        }
    }

    private async Task<List<(string PackageId, string Version)>> ExtractPackageReferencesAsync(string projectPath, CancellationToken cancellationToken) {
        var packageReferences = new List<(string, string)>();

        try {
            using var stream = File.OpenRead(projectPath);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            var packageRefElements = doc.Descendants("PackageReference");

            foreach (var element in packageRefElements) {
                var includeAttr = element.Attribute("Include");
                var versionAttr = element.Attribute("Version");

                if (includeAttr?.Value != null && versionAttr?.Value != null) {
                    packageReferences.Add((includeAttr.Value, versionAttr.Value));
                }
            }
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to parse project file {projectPath}: {ex.Message}");
        }

        return packageReferences;
    }

    private async Task CreateDirectoryPackagesPropsAsync(string filePath, Dictionary<string, string> packageVersions, CancellationToken cancellationToken) {
        var doc = new XDocument(
            new XElement("Project",
                new XElement("PropertyGroup",
                    new XElement("ManagePackageVersionsCentrally", "true")
                ),
                new XElement("ItemGroup",
                    packageVersions
                        .OrderBy(x => x.Key)
                        .Select(x => new XElement("PackageVersion",
                            new XAttribute("Include", x.Key),
                            new XAttribute("Version", x.Value)
                        ))
                )
            )
        );

        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        await doc.SaveAsync(stream, SaveOptions.None, cancellationToken);
    }

    private async Task UpdateProjectFileAsync(string projectPath, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.None, cancellationToken);
            }
            
            var packageRefElements = doc.Descendants("PackageReference");
            var modified = false;

            foreach (var element in packageRefElements) {
                var versionAttr = element.Attribute("Version");
                if (versionAttr != null) {
                    versionAttr.Remove();
                    modified = true;
                }
            }

            if (modified) {
                using var writeStream = new FileStream(projectPath, FileMode.Create, FileAccess.Write);
                await doc.SaveAsync(writeStream, SaveOptions.None, cancellationToken);
            }
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update project file {projectPath}: {ex.Message}");
        }
    }

    private static int CompareVersions(string version1, string version2) {
        try {
            var v1 = Version.Parse(NormalizeVersionString(version1));
            var v2 = Version.Parse(NormalizeVersionString(version2));
            return v1.CompareTo(v2);
        }
        catch {
            // Fallback to string comparison if version parsing fails
            return string.Compare(version1, version2, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeVersionString(string version) {
        // Remove pre-release suffixes for comparison
        var hyphenIndex = version.IndexOf('-');
        if (hyphenIndex > 0) {
            version = version.Substring(0, hyphenIndex);
        }

        // Ensure at least 3 parts (Major.Minor.Build)
        var parts = version.Split('.');
        if (parts.Length < 3) {
            var normalizedParts = new string[3];
            Array.Copy(parts, normalizedParts, parts.Length);
            for (int i = parts.Length; i < 3; i++) {
                normalizedParts[i] = "0";
            }
            version = string.Join(".", normalizedParts);
        }

        return version;
    }

    private class SolutionData {
        public string SolutionPath { get; set; } = string.Empty;
        public string SolutionDirectory { get; set; } = string.Empty;
        public Dictionary<string, string> PackageReferences { get; set; } = new();
        public List<string> ProjectFiles { get; set; } = new();
    }
}