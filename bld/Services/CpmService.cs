using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;

internal class CpmService {
    internal readonly record struct PackageReferenceVersion(string PackageId, string Version, bool IsVersionOverride);

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
        var cache = new ProjCfgCache(_console);

        var solutionData = new List<SolutionData>();

        var allSlns = new ConcurrentBag<string>();
        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            allSlns.Add(slnPath);
        }

        var parallelOptions = new ParallelOptions {
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        foreach (var slnPath in allSlns) {
            var solutionDir = Path.GetDirectoryName(slnPath)!;

            var allPackageReferences = new ConcurrentDictionary<string, string>(); // PackageId -> Version
            var projectFiles = new ConcurrentBag<string>();
            var solutionProjCfgs = new List<ProjCfg>();

            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                if (cache.Add(projCfg)) {
                    solutionProjCfgs.Add(projCfg);
                }
            }

            await _console.StartStatusAsync($"Analyzing {solutionProjCfgs.Count} project configurations in {Path.GetFileName(slnPath)}...", async ctx => {
                var count = 0;
                var total = solutionProjCfgs.Count;

                await Parallel.ForEachAsync(solutionProjCfgs, parallelOptions, async (projCfg, ct) => {
                    var current = Interlocked.Increment(ref count);
                    ctx.Status($"Analyzing projects: {current}/{total} ([bold]{Path.GetFileName(projCfg.Path)}[/])");
                    
                    var projectPath = projCfg.Path;
                    projectFiles.Add(projectPath);

                    var packageRefs = await ExtractPackageReferencesAsync(projectPath, cancellationToken);
                    foreach (var packageRef in packageRefs) {
                        if (packageRef.IsVersionOverride) {
                            _console.WriteVerbose($"Skipping centralization for {packageRef.PackageId} in {Path.GetFileName(projectPath)} because VersionOverride is project-specific.");
                            continue;
                        }

                        var packageId = packageRef.PackageId;
                        var version = packageRef.Version;
                        allPackageReferences.AddOrUpdate(packageId, version, (id, existingVersion) => {
                            if (CompareVersions(version, existingVersion) > 0) {
                                _console.WriteVerbose($"Updated {id} from {existingVersion} to {version}");
                                return version;
                            }
                            else if (!version.Equals(existingVersion, StringComparison.OrdinalIgnoreCase)) {
                                _console.WriteWarning($"Version conflict for {id}: {existingVersion} vs {version}. Using highest version.");
                            }
                            return existingVersion;
                        });
                    }
                });
            });

            solutionData.Add(new SolutionData {
                SolutionPath = slnPath,
                SolutionDirectory = solutionDir,
                PackageReferences = allPackageReferences.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
                ProjectFiles = projectFiles.ToList()
            });
        }

        if (solutionData.Count == 0) {
            _console.WriteError("No solution files found. Central Package Management requires a solution file.");
            return;
        }

        _console.WriteLine($"Found {solutionData.Count} solution(s) to process");

        foreach (var solution in solutionData) {
            _console.WriteInfo($"\nProcessing solution: {Path.GetFileName(solution.SolutionPath)}");
            _console.WriteLine($"Found {solution.PackageReferences.Count} unique package references across {solution.ProjectFiles.Count} projects");

            var cpmPropsFiles = FindDirectoryPackagesPropsPaths(solution.ProjectFiles, solution.SolutionDirectory);
            if (solution.PackageReferences.Count == 0) {
                if (cpmPropsFiles.Count > 0) {
                    _console.WriteInfo("Projects already use Central Package Management.");
                    _console.WriteLine("Detected Directory.Packages.props files:");
                    foreach (var propsPath in cpmPropsFiles) {
                        _console.WriteLine($"  {propsPath}");
                    }
                }
                else {
                    _console.WriteInfo("No versioned PackageReference entries found; nothing to centralize.");
                }
                continue;
            }

            var directoryPackagesPath = Path.Combine(solution.SolutionDirectory, "Directory.Packages.props");

            // Check if Directory.Packages.props already exists
            if (File.Exists(directoryPackagesPath) && !overwrite) {
                _console.WriteError($"Directory.Packages.props already exists at {directoryPackagesPath}. Use --overwrite to replace it.");
                continue;
            }

            if (applyChanges) {
                // Create Directory.Packages.props
                await CreateDirectoryPackagesPropsAsync(directoryPackagesPath, solution.PackageReferences, cancellationToken);
                _console.WriteLine($"Created Directory.Packages.props with {solution.PackageReferences.Count} package versions");

                // Update all project files to remove versions
                foreach (var projectPath in solution.ProjectFiles) {
                    await UpdateProjectFileAsync(projectPath, cancellationToken);
                    _console.WriteVerbose($"Updated project file: {Path.GetFileName(projectPath)}");
                }

                _console.WriteLine($"Updated {solution.ProjectFiles.Count} project files to use central package management");
            }
            else {
                _console.WriteLine("Dry run - showing what would be created:");
                _console.WriteLine($"Directory.Packages.props would be created at: {directoryPackagesPath}");
                _console.WriteLine("Package versions that would be centralized:");

                foreach (var (packageId, version) in solution.PackageReferences.OrderBy(x => x.Key)) {
                    _console.WriteLine($"  {packageId} = {version}");
                }

                _console.WriteLine($"\n{solution.ProjectFiles.Count} project files would be updated to remove version attributes");
            }
        }
    }

    internal static IReadOnlyList<PackageReferenceVersion> ReadPackageReferences(XDocument doc) {
        var packageReferences = new List<PackageReferenceVersion>();

        foreach (var element in doc.Descendants("PackageReference")) {
            var packageId = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(packageId)) {
                continue;
            }

            var versionOverride = FirstNonEmpty(
                element.Attribute("VersionOverride")?.Value,
                element.Element("VersionOverride")?.Value);
            if (!string.IsNullOrWhiteSpace(versionOverride)) {
                packageReferences.Add(new PackageReferenceVersion(packageId, versionOverride, true));
                continue;
            }

            var version = FirstNonEmpty(
                element.Attribute("Version")?.Value,
                element.Element("Version")?.Value);

            if (!string.IsNullOrWhiteSpace(version)) {
                packageReferences.Add(new PackageReferenceVersion(packageId, version, false));
            }
        }

        return packageReferences;
    }

    internal static bool RemoveCentralizableVersionDeclarations(XDocument doc) {
        var modified = false;

        foreach (var element in doc.Descendants("PackageReference")) {
            var versionAttr = element.Attribute("Version");
            if (versionAttr != null) {
                versionAttr.Remove();
                modified = true;
            }

            var versionElements = element.Elements("Version").ToList();
            foreach (var versionElement in versionElements) {
                // Drop the indentation text node preceding the element so removing it
                // doesn't leave a blank line behind when whitespace is preserved on save.
                if (versionElement.PreviousNode is XText previous && string.IsNullOrWhiteSpace(previous.Value)) {
                    previous.Remove();
                }
                versionElement.Remove();
                modified = true;
            }
        }

        return modified;
    }

    internal static IReadOnlyList<string> FindDirectoryPackagesPropsPaths(IEnumerable<string> projectFiles, string solutionDirectory) {
        var propsFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectFile in projectFiles.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))) {
            var currentDir = Path.GetDirectoryName(projectFile);
            while (!string.IsNullOrWhiteSpace(currentDir)) {
                var candidate = Path.Combine(currentDir, "Directory.Packages.props");
                if (File.Exists(candidate)) {
                    propsFiles.Add(Path.GetFullPath(candidate));
                    break;
                }

                var parent = Path.GetDirectoryName(currentDir);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, currentDir, StringComparison.OrdinalIgnoreCase)) {
                    break;
                }
                currentDir = parent;
            }
        }

        var solutionPropsPath = Path.Combine(solutionDirectory, "Directory.Packages.props");
        if (File.Exists(solutionPropsPath)) {
            propsFiles.Add(Path.GetFullPath(solutionPropsPath));
        }

        return propsFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<PackageReferenceVersion>> ExtractPackageReferencesAsync(string projectPath, CancellationToken cancellationToken) {
        var packageReferences = new List<PackageReferenceVersion>();

        try {
            using var stream = File.OpenRead(projectPath);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            packageReferences.AddRange(ReadPackageReferences(doc));
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to parse project file {projectPath}: {ex.FormatMessage()}");
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
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings {
            Indent = true,
            OmitXmlDeclaration = true,
            Encoding = System.Text.Encoding.UTF8,
            Async = true
        });
        await doc.SaveAsync(writer, cancellationToken);
    }

    internal async Task UpdateProjectFileAsync(string projectPath, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }

            var modified = RemoveCentralizableVersionDeclarations(doc);

            if (modified) {
                using var writeStream = new FileStream(projectPath, FileMode.Create, FileAccess.Write);
                using var writer = XmlWriter.Create(writeStream, new XmlWriterSettings {
                    // Preserve the file's existing layout; only the removed Version
                    // nodes should change. Re-indenting would rewrite the whole file.
                    Indent = false,
                    OmitXmlDeclaration = true,
                    Encoding = System.Text.Encoding.UTF8,
                    Async = true
                });
                await doc.SaveAsync(writer, cancellationToken);
            }
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update project file {projectPath}: {ex.FormatMessage()}");
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

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
