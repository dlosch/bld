using bld.Infrastructure;
using bld.Models;
using NuGet.Versioning;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;

internal class CpmService {
    internal readonly record struct PackageReferenceVersion(string PackageId, string Version, bool IsVersionOverride, bool IsConditional = false);

    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public CpmService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    /// <summary>Returns true when the conversion completed without an error worth failing on.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<bool> ConvertToCentralPackageManagementAsync(string rootPath, bool applyChanges, bool overwrite, CancellationToken cancellationToken) {
        var succeeded = true;
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);

        _console.WriteInfo("Starting Central Package Management conversion...");

        // Find solution file(s) to determine the root
        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

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

            // Fresh cache per solution: dedup is only valid within one solution. A shared cache would
            // exclude projects already seen in another solution, producing an incomplete
            // Directory.Packages.props for this solution while still stripping those projects' versions.
            var cache = new ProjCfgCache(_console);

            var allPackageReferences = new ConcurrentDictionary<string, string>(); // PackageId -> Version
            var projectFiles = new ConcurrentBag<string>();
            var solutionProjCfgs = new List<ProjCfg>();
            // Packages declared inside a conditional ItemGroup anywhere in the solution; they cannot be
            // safely centralized because the per-condition versions differ by design.
            var conditionalPackages = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                if (cache.Add(projCfg)) {
                    solutionProjCfgs.Add(projCfg);
                }
            }

            // One ProjCfg per configuration means the same project appears several times; collapse by
            // path so counts are real and each file is rewritten once.
            solutionProjCfgs = solutionProjCfgs
                .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            await _console.StartStatusAsync($"Analyzing {solutionProjCfgs.Count} project configurations in {Markup.Escape(Path.GetFileName(slnPath))}...", async ctx => {
                var count = 0;
                var total = solutionProjCfgs.Count;

                await Parallel.ForEachAsync(solutionProjCfgs, parallelOptions, async (projCfg, ct) => {
                    var current = Interlocked.Increment(ref count);
                    ctx.Status($"Analyzing projects: {current}/{total} ([bold]{Markup.Escape(Path.GetFileName(projCfg.Path))}[/])");
                    
                    var projectPath = projCfg.Path;

                    // A project outside the solution directory never imports the props file we create,
                    // yet its Version attributes were stripped anyway - leaving it with no version at
                    // all (NU1604).
                    if (!DirExt.IsNestedBelow(projectPath, solutionDir)) {
                        _console.WriteWarning($"Skipping {projectPath}: it is outside {solutionDir} and would not import the generated Directory.Packages.props.");
                        return;
                    }

                    projectFiles.Add(projectPath);

                    var packageRefs = await ExtractPackageReferencesAsync(projectPath, cancellationToken);
                    foreach (var packageRef in packageRefs) {
                        if (packageRef.IsConditional) {
                            // Per-TFM pins collapse to one central version, so the framework that needed
                            // the lower version silently starts resolving the higher one.
                            _console.WriteWarning($"Skipping centralization for {packageRef.PackageId} in {Path.GetFileName(projectPath)}: it is declared inside a conditional ItemGroup.");
                            conditionalPackages.TryAdd(packageRef.PackageId, 0);
                            continue;
                        }
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
                ProjectFiles = projectFiles.ToList(),
                ConditionalPackages = new HashSet<string>(conditionalPackages.Keys, StringComparer.OrdinalIgnoreCase)
            });
        }

        if (solutionData.Count == 0) {
            _console.WriteError("No solution files found. Central Package Management requires a solution file.");
            return false;
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
                _console.WriteError($"Directory.Packages.props already exists at {directoryPackagesPath}. Use --overwrite to merge into it.");
                succeeded = false;
                continue;
            }

            if (applyChanges) {
                // Create Directory.Packages.props
                await CreateDirectoryPackagesPropsAsync(directoryPackagesPath, solution.PackageReferences, cancellationToken);

                // Update all project files to remove versions
                var updated = 0;
                foreach (var projectPath in solution.ProjectFiles) {
                    if (await UpdateProjectFileAsync(projectPath, solution.ConditionalPackages, cancellationToken)) {
                        updated++;
                        _console.WriteVerbose($"Updated project file: {Path.GetFileName(projectPath)}");
                    }
                }

                _console.WriteLine($"Updated {updated} project file(s) to use central package management");
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

        return succeeded;
    }

    internal static IReadOnlyList<PackageReferenceVersion> ReadPackageReferences(XDocument doc) {
        var packageReferences = new List<PackageReferenceVersion>();

        foreach (var element in doc.ElementsNamed("PackageReference")) {
            var packageId = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(packageId)) {
                continue;
            }

            var isConditional = element.IsConditioned();

            var versionOverride = FirstNonEmpty(
                element.Attribute("VersionOverride")?.Value,
                element.ChildNamed("VersionOverride")?.Value);
            if (!string.IsNullOrWhiteSpace(versionOverride)) {
                packageReferences.Add(new PackageReferenceVersion(packageId, versionOverride, true, isConditional));
                continue;
            }

            var version = FirstNonEmpty(
                element.Attribute("Version")?.Value,
                element.ChildNamed("Version")?.Value);

            if (!string.IsNullOrWhiteSpace(version)) {
                packageReferences.Add(new PackageReferenceVersion(packageId, version, false, isConditional));
            }
        }

        return packageReferences;
    }

    internal static bool RemoveCentralizableVersionDeclarations(XDocument doc) =>
        RemoveCentralizableVersionDeclarations(doc, skipPackageIds: null);

    /// <summary>
    /// Strips inline versions so the central ones apply. Packages in <paramref name="skipPackageIds"/>
    /// keep theirs: a reference inside a conditional ItemGroup has a per-framework version on purpose,
    /// and centralizing it would silently resolve one framework against another's version.
    /// </summary>
    internal static bool RemoveCentralizableVersionDeclarations(XDocument doc, ISet<string>? skipPackageIds) {
        var modified = false;

        foreach (var element in doc.ElementsNamed("PackageReference")) {
            var packageId = element.Attribute("Include")?.Value;
            if (element.IsConditioned()) continue;
            if (packageId is { } && skipPackageIds is { } && skipPackageIds.Contains(packageId)) continue;

            var versionAttr = element.Attribute("Version");
            if (versionAttr != null) {
                versionAttr.Remove();
                modified = true;
            }

            var versionElements = element.Elements().Where(e => e.Name.LocalName == "Version").ToList();
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
        // Merge into an existing file instead of replacing it. Building the document from scratch and
        // opening with FileMode.Create discarded every PackageVersion already centralized (projects
        // already on CPM have no inline version, so they contribute nothing here) along with any
        // GlobalPackageReference, conditions and comments - breaking restore for the whole solution.
        if (File.Exists(filePath)) {
            var merged = 0;
            var written = await XmlProjectFile.EditAsync(filePath, doc => {
                var itemGroup = doc.ElementsNamed("ItemGroup").FirstOrDefault(g => g.ElementsNamed("PackageVersion").Any())
                    ?? doc.ElementsNamed("ItemGroup").FirstOrDefault();

                if (itemGroup is null) {
                    itemGroup = new XElement("ItemGroup");
                    (doc.ElementsNamed("Project").FirstOrDefault() ?? doc.Root)?.Add(itemGroup);
                }

                var existing = doc.ElementsNamed("PackageVersion")
                    .Where(e => e.Attribute("Include")?.Value is { })
                    .ToDictionary(e => e.Attribute("Include")!.Value, e => e, StringComparer.OrdinalIgnoreCase);

                foreach (var (id, version) in packageVersions.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) {
                    if (existing.TryGetValue(id, out var element)) {
                        var current = element.Attribute("Version")?.Value ?? element.ChildNamed("Version")?.Value;
                        if (string.Equals(current, version, StringComparison.OrdinalIgnoreCase)) continue;
                        _console.WriteWarning($"{id} is already centrally managed at {current}; leaving it unchanged (found {version} inline).");
                        continue;
                    }

                    itemGroup.Add(new XElement("PackageVersion",
                        new XAttribute("Include", id),
                        new XAttribute("Version", version)));
                    merged++;
                }
                return merged > 0;
            }, cancellationToken);

            _console.WriteLine(written
                ? $"Added {merged} package version(s) to {filePath}"
                : $"No new package versions to add to {filePath}");
            return;
        }

        var newDoc = new XDocument(
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

        // Write via a temp file and move, so an interrupted write cannot leave a truncated props file.
        var tempPath = filePath + ".bldtmp";
        try {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write)) {
                using var writer = XmlWriter.Create(stream, new XmlWriterSettings {
                    Indent = true,
                    OmitXmlDeclaration = true,
                    Encoding = System.Text.Encoding.UTF8,
                    Async = true
                });
                await newDoc.SaveAsync(writer, cancellationToken);
            }
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch {
            if (File.Exists(tempPath)) {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            throw;
        }
    }

    internal Task<bool> UpdateProjectFileAsync(string projectPath, CancellationToken cancellationToken) =>
        UpdateProjectFileAsync(projectPath, null, cancellationToken);

    internal async Task<bool> UpdateProjectFileAsync(string projectPath, ISet<string>? skipPackageIds, CancellationToken cancellationToken) {
        try {
            return await XmlProjectFile.EditAsync(projectPath, doc => RemoveCentralizableVersionDeclarations(doc, skipPackageIds), cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update project file {projectPath}: {ex.FormatMessage()}", ex);
            return false;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // Compares two package versions using NuGet SemVer2 semantics, so a stable release outranks its
    // prereleases (2.0.0 > 2.0.0-beta) and prereleases order correctly. Version ranges ("[1.2.3]") and
    // floating versions ("1.*") are not single versions; rather than invent an ordering we fall back to
    // a deterministic ordinal comparison.
    internal static int CompareVersions(string version1, string version2) {
        if (NuGetVersion.TryParse(version1, out var v1) && NuGetVersion.TryParse(version2, out var v2)) {
            return v1.CompareTo(v2);
        }
        return string.Compare(version1, version2, StringComparison.OrdinalIgnoreCase);
    }

    private class SolutionData {
        public string SolutionPath { get; set; } = string.Empty;
        public string SolutionDirectory { get; set; } = string.Empty;
        public Dictionary<string, string> PackageReferences { get; set; } = new();
        public List<string> ProjectFiles { get; set; } = new();
        /// <summary>Packages declared under a Condition; their inline versions must be left in place.</summary>
        public HashSet<string> ConditionalPackages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
