using bld.Infrastructure;
using bld.Models;
using System.Collections.Concurrent;

namespace bld.Services;

internal sealed class MarkDeleteProcessor : IProjectProcessor {
    private readonly IConsoleOutput _console;
    private readonly IFileSystem _fileSystem;
    private readonly CleaningOptions _options;
    private readonly ErrorSink _errorSink;
    // TFM and configuration names are case-insensitive identifiers regardless of platform.
    private static readonly StringComparer DefaultComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly StringComparison DefaultComparison = StringComparison.OrdinalIgnoreCase;
    // Paths must follow the filesystem: on Linux /src/Foo and /src/foo are different directories and
    // collapsing them into one key left one of them undeleted.
    private static readonly StringComparer PathComparer = DirExt.PathComparer;

    // Track directories for deletion - using ConcurrentBag for thread-safe value collection
    private readonly ConcurrentDictionary<string, ConcurrentBag<Dir>> _deleteDirs = new ConcurrentDictionary<string, ConcurrentBag<Dir>>(PathComparer);
    // What each marked directory is (bin, obj, publish, ...): the first mark decides, so a package
    // output that is also the build output stays "bin".
    private readonly ConcurrentDictionary<string, DirType> _deleteTypes = new ConcurrentDictionary<string, DirType>(PathComparer);
    private readonly ConcurrentDictionary<string, Dir> _dirs = new ConcurrentDictionary<string, Dir>(PathComparer);
    // Single files marked for deletion: only a project's own packages in a package output directory.
    private readonly ConcurrentDictionary<string, ConcurrentBag<Dir>> _deleteFiles = new ConcurrentDictionary<string, ConcurrentBag<Dir>>(PathComparer);
    // Per project file: the package id it packs under and whether it packs at all, for the file match.
    private readonly ConcurrentDictionary<string, (string? PackageId, bool Packable)> _packages = new ConcurrentDictionary<string, (string?, bool)>(PathComparer);

    // In interactive mode every category is marked and the flags only pick what starts out checked.
    private bool MarkObj => _options.CleanObjDirectory || _options.Interactive;
    private bool MarkPublish => _options.CleanPublishDirectory || _options.Interactive;
    private bool MarkTestResults => _options.CleanTestResults || _options.Interactive;

    private void Mark(string path, DirType type, Dir dir) {
        _deleteDirs.GetOrAdd(path, _ => new ConcurrentBag<Dir>()).Add(dir);
        _deleteTypes.TryAdd(path, type);
    }

    private void MarkFile(string path, Dir dir) =>
        _deleteFiles.GetOrAdd(path, _ => new ConcurrentBag<Dir>()).Add(dir);

    public MarkDeleteProcessor(IConsoleOutput console, IFileSystem fileSystem, CleaningOptions options, ErrorSink errorSink) {
        _console = console;
        _fileSystem = fileSystem;
        _options = options;
        _errorSink = errorSink;

        _enumerateFiles = new EnumerationOptions { MatchType = MatchType.Simple, MaxRecursionDepth = 10 /*options.Depth*/, RecurseSubdirectories = true, ReturnSpecialDirectories = false, IgnoreInaccessible = true };
    }

    public async Task ProcessAsync(ProjCfg cfg, ProjectInfo info) {
        if (info == null) return;

        // Build directory tracking structures similar to old processor
        await AddDir(info, cfg);
    }

    private readonly EnumerationOptions _enumerateFiles;

    internal MarkDeleteResult GetResult() {
        var results = new List<DirResult>();
        foreach (var kvp in _deleteDirs.OrderBy(k => k.Key)) {
            var path = kvp.Key;
            if (!Directory.Exists(path)) continue;
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.Exists) {
                results.Add(new DirResult(dirInfo, kvp.Value.ToList(), CleanCategories.Of(_deleteTypes.GetValueOrDefault(path, DirType.OutDir))));
            }
        }
        var files = new List<FileResult>();
        foreach (var kvp in _deleteFiles.OrderBy(k => k.Key)) {
            var fileInfo = new FileInfo(kvp.Key);
            if (fileInfo.Exists) files.Add(new FileResult(fileInfo, kvp.Value.ToList(), CleanCategory.Package));
        }
        return new MarkDeleteResult(results) { Files = files };
    }

    private async ValueTask AddDir(ProjectInfo info, ProjCfg cfg) {
        var absProjPath = cfg.Path;
        var projName = info.ProjectName;
        var tfms = new HashSet<string>(DefaultComparer);

        // TargetFramework != null > OutDir ok
        // TargetFrameworks != null > OutDir null

        if (info.TargetFramework != null) tfms.Add(info.TargetFramework);
        if (info.TargetFrameworks != null) tfms.UnionWith(info.TargetFrameworks);

        if (info.UseArtifactsOutput && !string.IsNullOrEmpty(info.ArtifactsPath)) {
            // Artifacts layout: OutDir evaluates to artifacts/bin/<project>/<config>/ for the outer build of
            // a multi-targeted project, a directory that does not exist (the real ones are <config>_<tfm>),
            // so the bin/<Configuration>/<tfm> matching below never found anything. Mark the per-project
            // artifacts directory instead and let ArtifactsDirDelete pick the pivot subdirectories.
            var artifactsProjName = info.ArtifactsProjectName ?? Path.GetFileNameWithoutExtension(absProjPath);
            var artifactsBin = DirExt.EnsureRooted(Path.Combine(info.ArtifactsPath, info.ArtifactsBinOutputName ?? "bin", artifactsProjName), cfg.ProjDir);
            _console.WriteDebug($"Artifacts output directory {artifactsBin} for {info.ProjectName}. Exists? {Directory.Exists(artifactsBin)}");
            await AddDirInternal(artifactsBin, DirType.ArtifactsBin, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);

            if (MarkPublish) {
                var artifactsPublish = DirExt.EnsureRooted(Path.Combine(info.ArtifactsPath, info.ArtifactsPublishOutputName ?? "publish", artifactsProjName), cfg.ProjDir);
                await AddDirInternal(artifactsPublish, DirType.ArtifactsPublish, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);
            }
        }
        // Add OutDir
        else if (!string.IsNullOrEmpty(info.OutDir)) {
            var outDir = DirExt.EnsureRooted(info.OutDir, cfg.ProjDir);
            _console.WriteDebug($"Output directory {outDir} for {info.ProjectName}. Exists? {Directory.Exists(outDir)}");
            await AddDirInternal(outDir, DirType.OutDir, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);
        }
        else if (!string.IsNullOrEmpty(info.BaseOutputPath) && (info.TargetFrameworks?.Any() ?? false)) {
            foreach (var item in info.TargetFrameworks) {
                _console.WriteDebug($"Output directory {Path.Combine(info.BaseOutputPath, cfg.ConfigurationOrDefault, item)}->{DirExt.EnsureRooted(PathUtils.SafeCombine(info.BaseOutputPath, cfg.Configuration, item), cfg.ProjDir)}");
                var outDir = DirExt.EnsureRooted(PathUtils.SafeCombine(info.BaseOutputPath, cfg.Configuration, item), cfg.ProjDir);
                await AddDirInternal(outDir, DirType.OutDir, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);
            }
        }
        else {
            _console.WriteVerbose($"No OutDir or BaseOutputPath specified {info.ProjectName} {info.ProjectPath} {info.Configuration} {cfg.ProjDir}");
        }

        // Add IntermediateOutputPath
        if (!string.IsNullOrEmpty(info.IntermediateOutputPath)) {
            var intermediateDir = DirExt.EnsureRooted(info.IntermediateOutputPath, cfg.ProjDir);
            await AddDirInternal(intermediateDir, DirType.BaseIntermediateOutputPath, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);
        }

        if (MarkPublish) {
            // In the artifacts layout PublishDir evaluates to the outer-build pivot (artifacts/publish/<project>/<config>/);
            // the per-project directory added above covers every pivot, so only use the evaluated value elsewhere.
            if (!info.UseArtifactsOutput && !string.IsNullOrEmpty(info.PublishDir)) {
                await AddDirInternal(DirExt.EnsureRooted(info.PublishDir, cfg.ProjDir), DirType.PublishDir, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);
            }
            // PackageOutputPath defaults to OutputPath, i.e. the marked build output; the second pass in
            // ProcessDirs drops it when it is already covered. Anywhere else (a local feed, the shared
            // artifacts/package/<config>/) only this project's own package files are candidates.
            if (!string.IsNullOrEmpty(info.PackageOutputPath)) {
                _packages[absProjPath] = (info.PackageId ?? info.AssemblyName ?? Path.GetFileNameWithoutExtension(absProjPath), info.IsPackable ?? true);
                await AddDirInternal(DirExt.EnsureRooted(info.PackageOutputPath, cfg.ProjDir), DirType.PackageOutputPath, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);
            }
        }

        if (MarkTestResults) {
            // `dotnet test` writes TestResults/ next to the project (VSTestResultsDirectory when set) or,
            // with --results-directory, wherever the caller said; the solution directory is the usual
            // other place. Anything else is not known here.
            var testResults = info.TestResultsDirectory is { } configured
                ? DirExt.EnsureRooted(configured, cfg.ProjDir)
                : Path.Combine(cfg.ProjDir, "TestResults");
            await AddDirInternal(testResults, DirType.TestResults, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);
            if (cfg.Proj.Parent is { } sln && Path.GetDirectoryName(sln.Path) is { Length: > 0 } slnDir) {
                await AddDirInternal(Path.Combine(slnDir, "TestResults"), DirType.TestResults, absProjPath, projName, info.Configuration, tfms, cfg.ProjDir);
            }
        }
    }

    private ValueTask AddDirInternal(string absPath, DirType dirType, string absProjPath, string? projName, string? cfg, HashSet<string> tfms, string? parentPath) {

        HashSet<string> GetHashSetS(string? item) {
            var hs = new HashSet<string>(DefaultComparer);
            if (item is not null) hs.Add(item);
            return hs;
        }

        Dictionary<string, string?> GetDict(string? item, string? val) {
            var hs = new Dictionary<string, string?>(DefaultComparer);
            if (item is not null) hs.Add(item, val);
            return hs;
        }

        _dirs.AddOrUpdate(absProjPath,
            (key) => new Dir(new List<(string, DirType)>() { (absPath, dirType) }, GetDict(absProjPath, projName), GetHashSetS(cfg), tfms, GetHashSetS(parentPath)),
            (key, existDir) => {
                lock (existDir) {
                    existDir.AbsPath.Add((absPath, dirType));
                    existDir.AbsProjPath.TryAdd(absProjPath, projName);
                    if (tfms is not null && tfms.Any()) {
                        existDir.Tfms.UnionWith(tfms);
                    }
                    if (parentPath is not null) {
                        existDir.AbsParentPath.Add(parentPath);
                    }
                    if (cfg is not null) {
                        existDir.Configs.Add(cfg);
                    }
                    return existDir;
                }
            });

        return ValueTask.CompletedTask;
    }

    private static bool IsPublishOrPackage(DirType type) => type is DirType.PublishDir or DirType.PackageOutputPath;

    internal Task ProcessDirs() {
        if (_dirs.Any()) {
            // PublishDir and PackageOutputPath default to a location inside the build output. They are
            // handled in a second pass so the "already covered by a marked directory" check sees every
            // project's marks, not only those processed so far.
            foreach (var secondPass in new[] { false, true })
            foreach (var dir in _dirs.Values) {
                foreach ((string path, DirType type) item in dir.AbsPath.Distinct()) {
                    if (IsPublishOrPackage(item.type) != secondPass) continue;

                    var dirInfo = new DirectoryInfo(item.path);

                    bool NotSafeToDelete(Dir dir) {
                        // No project or solution may live below the target. Checking only the owning
                        // project missed the shared-artifacts layout, where another project's sources
                        // sit under the directory we are about to delete recursively.
                        var offender = AllKnownProjectPaths().FirstOrDefault(p => DirExt.IsNestedBelow(p, item.path));
                        if (offender is { }) {
                            _console.WriteWarning($"Skipping {item.path}: project {offender} is below it.");
                            return true;
                        }

                        if (AllKnownProjectDirs().Any(p => DirExt.IsNestedBelow(p, item.path))) {
                            _console.WriteWarning($"Skipping {item.path}: a project directory is below it.");
                            return true;
                        }

                        return false;
                    }

                    if (NotSafeToDelete(dir)) {
                        _console.WriteVerbose($"{dir} is not safe to delete, skipping.");
                        // Skip only this unsafe candidate path; keep processing the remaining
                        // directories/projects (a `return` here aborted the entire run).
                        continue;
                    }

                    Stats OutDirDelete(string absPath, DirType dirType, Dir dir) {
                        var dirInfo = new DirectoryInfo(absPath);
                        var exists = dirInfo.Exists;

                        if (exists && dirInfo.IsEmpty()) {
                            // todo we dont delete empty dirs.
                            // delete dir - this would be handled by the deletion phase
                        }

                        var deleteCandidates = default(IEnumerable<DirectoryInfo>);

                        if (HasValidateBasicOutDirStructureFlag()) {
                            if (NetUtil.Instance.IsTfmName(dirInfo.Name, DefaultComparison)
                            && dir.Configs.Any(cfg => 0 == string.Compare(cfg, dirInfo.Parent?.Name, DefaultComparison))) {
                                var cfgDir = dirInfo.Parent;

                                if (!cfgDir!.Exists) {
                                    _console.WriteDebug($"{cfgDir.FullName} does not exist.");
                                    return default;
                                }

                                // "Current" means current for *any* project writing here, not just this one.
                                var claimedTfms = TfmsClaimedUnder(cfgDir);

                                IEnumerable<DirectoryInfo> GetCfgNestedAffected(DirectoryInfo cfgDir2, Dir dir2, bool onlyNonCurrent2) => cfgDir2.EnumerateDirectories()
                                        .Where(tfmDir => NetUtil.Instance.IsTfmName(tfmDir.Name, DefaultComparison)
                                        && (!onlyNonCurrent2 || !claimedTfms.Contains(tfmDir.Name)));

                                var onlyNonCurrent = HasCleanOnlyNoncurrentTfmsFlag();

                                if (onlyNonCurrent
                                    || (cfgDir.EnumerateFiles().Any())
                                    || (cfgDir.EnumerateDirectories().Any(tfmDir => !NetUtil.Instance.IsTfmName(tfmDir.Name, DefaultComparison)))) {

                                    _console.WriteVerbose($"{absPath} contains files or directories which don't match tfm format. Selectively adding subdirectories ...");
                                    deleteCandidates = GetCfgNestedAffected(cfgDir, dir, onlyNonCurrent);
                                }
                                else {
                                    if (cfgDir.Parent is { } binDir) {
                                        if (0 == string.Compare(binDir.Name, "bin", DefaultComparison)
                                            || dir.AbsProjPath.Any(kvp => kvp.Value is { } projectName && (0 == string.Compare(binDir.Name, projectName, DefaultComparison)))) {

                                            if (binDir.EnumerateFiles().Any()
                                            || binDir.EnumerateDirectories().Any(cfgDir => !dir.Configs.Contains(cfgDir.Name))) {
                                                _console.WriteVerbose($"{absPath} contains files or directories which don't match configurations format. Selectively adding subdirectories ...");
                                                deleteCandidates = GetCfgNestedAffected(cfgDir, dir, onlyNonCurrent);
                                            }
                                            else {
                                                deleteCandidates = GetCfgNestedAffected(cfgDir, dir, onlyNonCurrent);
                                            }
                                        }
                                    }
                                }
                            }
                            else if (exists && dir.Configs.Any(cfg => 0 == string.Compare(cfg, dirInfo.Name, DefaultComparison))) {
                                // OutDir is the configuration directory itself (bin\Debug\), which is what
                                // MSBuild produces for a multi-targeted outer build and for legacy projects.
                                // The TFM-shaped branch above never matched these, so they were never cleaned.
                                var binDir = dirInfo.Parent;
                                var underBin = binDir is { } && (0 == string.Compare(binDir.Name, "bin", DefaultComparison)
                                    || dir.AbsProjPath.Any(kvp => kvp.Value is { } projectName && 0 == string.Compare(binDir.Name, projectName, DefaultComparison)));
                                if (underBin) {
                                    deleteCandidates = new DirectoryInfo[] { dirInfo };
                                }
                                else {
                                    _console.WriteVerbose($"{absPath} is a configuration directory but its parent is not 'bin'; skipping.");
                                }
                            }
                            else if (exists) {
                                // Neither shape matched. This used to be silent, so a project whose output was
                                // never cleaned looked exactly like a project with no output.
                                var owner = dir.AbsProjPath.Keys.FirstOrDefault() ?? absPath;
                                if (_unrecognizedLayoutWarned.Add(owner)) {
                                    _console.WriteWarning($"Skipping {absPath}: output layout not recognized (expected bin/<Configuration>/<tfm>, bin/<Configuration> or artifacts/bin/<Project>/<config>_<tfm>).");
                                }
                            }
                        }
                        else {
                            if (exists) deleteCandidates = new DirectoryInfo[] { dirInfo };
                        }

                        if (deleteCandidates is { }) {
                            foreach (var d in deleteCandidates) {
                                Mark(d.FullName, dirType, dir);
                                _console.WriteDebug($"{d.FullName} marked for deletion.");
                            }
                        }

                        dir.SetProcessed();
                        return default;
                    }

                    Stats BaseOutDirDelete(string absPath, DirType dirType, Dir dir) {
                        _console.WriteVerbose("Not Implemented :(");
                        return default;
                    }

                    Stats BaseIntermediateOutputDirDelete(string absPath, DirType dirType, Dir dir) {
                        if (!MarkObj) return default;
                        if (!Directory.Exists(absPath)) return default;

                        // This path had no structural validation at all: a project pointing
                        // BaseIntermediateOutputPath at a shared build\ directory would have that whole
                        // tree - including any sources under it - marked for recursive deletion.
                        if (ContainsProjectOrSolution(absPath)) {
                            _console.WriteWarning($"Skipping {absPath}: it contains project or solution files.");
                            return default;
                        }

                        if (_options.KeepRestoreArtifacts) {
                            // Only mark subdirectories (build output like Debug/net8.0),
                            // preserving root-level files (project.assets.json, *.nuget.* etc.)
                            foreach (var subDir in new DirectoryInfo(absPath).EnumerateDirectories()) {
                                Mark(subDir.FullName, dirType, dir);
                            }
                        }
                        else {
                            Mark(absPath, dirType, dir);
                        }
                        return default;
                    }

                    Stats VcxDir(string absPath, DirType dirType, Dir dir) {
                        if (Directory.Exists(absPath)) {
                            if (ContainsProjectOrSolution(absPath)) {
                                _console.WriteWarning($"Skipping {absPath}: it contains project or solution files.");
                                return default;
                            }
                            Mark(absPath, dirType, dir);
                        }
                        return default;
                    }

                    Stats TestResultsDirDelete(string absPath, DirType dirType, Dir dir) {
                        if (!Directory.Exists(absPath)) return default;
                        if (ContainsProjectOrSolution(absPath)) {
                            _console.WriteWarning($"Skipping {absPath}: it contains project or solution files.");
                            return default;
                        }
                        var fullName = new DirectoryInfo(absPath).FullName;
                        Mark(fullName, dirType, dir);
                        _console.WriteDebug($"{fullName} marked for deletion.");
                        return default;
                    }

                    // artifacts/bin/<project>/ or artifacts/publish/<project>/: every subdirectory named
                    // <config>[_<tfm>][_<rid>] is build output of this project; anything else is left alone.
                    Stats ArtifactsDirDelete(string absPath, DirType dirType, Dir dir) {
                        var root = new DirectoryInfo(absPath);
                        if (!root.Exists) {
                            _console.WriteDebug($"{absPath} does not exist.");
                            return default;
                        }
                        if (ContainsProjectOrSolution(absPath)) {
                            _console.WriteWarning($"Skipping {absPath}: it contains project or solution files.");
                            return default;
                        }

                        var onlyNonCurrent = HasCleanOnlyNoncurrentTfmsFlag();
                        var claimedTfms = TfmsClaimedForArtifacts(absPath);

                        foreach (var pivotDir in root.EnumerateDirectories()) {
                            if (!TryParseArtifactsPivots(pivotDir.Name, dir.Configs, out var tfm)) {
                                _console.WriteVerbose($"{pivotDir.FullName} does not match <config>[_<tfm>][_<rid>]; skipping.");
                                continue;
                            }
                            // A pivot without a TFM segment is the single-target output, which is always current.
                            if (onlyNonCurrent && (tfm is null || claimedTfms.Contains(tfm))) continue;

                            Mark(pivotDir.FullName, dirType, dir);
                            _console.WriteDebug($"{pivotDir.FullName} marked for deletion.");
                        }

                        dir.SetProcessed();
                        return default;
                    }

                    Stats PublishOrPackageDirDelete(string absPath, DirType dirType, Dir dir) {
                        if (!Directory.Exists(absPath)) return default;

                        // Default PublishDir is $(OutputPath)publish/ and default PackageOutputPath is
                        // $(OutputPath) itself, so the usual case is already covered by the marked build output.
                        if (_deleteDirs.Keys.Any(marked => SamePath(marked, absPath) || DirExt.IsNestedBelow(absPath, marked))) {
                            _console.WriteDebug($"{absPath} is already covered by a marked directory.");
                            return default;
                        }

                        if (dirType == DirType.PackageOutputPath) {
                            PackageOutputFiles(absPath, dir);
                            return default;
                        }

                        if (ContainsProjectOrSolution(absPath)) {
                            _console.WriteWarning($"Skipping {absPath}: it contains project or solution files.");
                            return default;
                        }

                        var fullName = new DirectoryInfo(absPath).FullName;
                        Mark(fullName, dirType, dir);
                        _console.WriteDebug($"{fullName} marked for deletion.");
                        return default;
                    }

                    var deleteTask = (dir.ProjType, item.type) switch {
                        (_, DirType.ArtifactsBin) or (_, DirType.ArtifactsPublish) => ArtifactsDirDelete(item.path, item.type, dir),
                        (_, DirType.PublishDir) or (_, DirType.PackageOutputPath) => PublishOrPackageDirDelete(item.path, item.type, dir),
                        (_, DirType.TestResults) => TestResultsDirDelete(item.path, item.type, dir),
                        (ProjectType.Vcxproj, _) => VcxDir(item.path, item.type, dir),
                        (_, DirType.OutDir) => OutDirDelete(item.path, item.type, dir),
                        (_, DirType.BaseOutputPath) => BaseOutDirDelete(item.path, item.type, dir),
                        (_, DirType.BaseIntermediateOutputPath) => BaseIntermediateOutputDirDelete(item.path, item.type, dir),
                        _ => default,
                    };
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks the package files of the directory's owning projects, never the directory: a package
    /// output directory is shared by design (a local feed, artifacts/package/<config>/) and holds
    /// packages that are not this run's to delete. A file counts only when it sits directly in the
    /// directory and is named `<PackageId>.<version>[.symbols].nupkg` or `.snupkg` of a project that
    /// packs, so `Foo` never claims `Foo.Bar.1.0.0.nupkg`, and only when the package itself says it
    /// belongs to that project.
    /// </summary>
    private void PackageOutputFiles(string absPath, Dir dir) {
        var ids = dir.AbsProjPath.Keys
            .Select(p => _packages.TryGetValue(p, out var package) ? package : (PackageId: null, Packable: false))
            .Where(p => p.Packable && !string.IsNullOrWhiteSpace(p.PackageId))
            .Select(p => p.PackageId!)
            .Distinct(DefaultComparer)
            .ToList();
        if (ids.Count == 0) {
            _console.WriteVerbose($"{absPath}: no packable project with a package id owns it; nothing marked.");
            return;
        }

        IEnumerable<FileInfo> files;
        try {
            files = new DirectoryInfo(absPath).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, ReturnSpecialDirectories = false, MatchType = MatchType.Simple }).ToList();
        }
        catch (Exception ex) {
            _console.WriteWarning($"Could not list {absPath} ({ex.FormatMessage()}); nothing marked there.");
            return;
        }

        foreach (var file in files) {
            // The name is the cheap filter; the package itself has the last word.
            if (!ids.Any(id => IsPackageFileOf(file.Name, id))) continue;

            var actual = PackageIdOf(file);
            if (actual is null) continue;
            if (!ids.Contains(actual, DefaultComparer)) {
                _console.WriteVerbose($"{file.FullName} is package '{actual}', which no project in this run packs; not marked.");
                continue;
            }

            MarkFile(file.FullName, dir);
            _console.WriteDebug($"{file.FullName} marked for deletion.");
        }
    }

    /// <summary>
    /// The id the package really carries, read from the .nuspec inside it. The file name cannot
    /// settle this on its own: a NuGet id may end in a numeric segment, so `Foo.1.2.0.nupkg` is
    /// `Foo.1` version `2.0` just as plausibly as `Foo` version `1.2.0`, and going by the name alone
    /// let a project delete another project's package. Null when the file does not read as a
    /// package, which leaves it alone - deleting something unreadable is the worse guess.
    /// </summary>
    private string? PackageIdOf(FileInfo file) {
        try {
            using var reader = new global::NuGet.Packaging.PackageArchiveReader(file.FullName);
            var id = reader.NuspecReader.GetId();
            if (!string.IsNullOrWhiteSpace(id)) return id;
            _console.WriteWarning($"{file.FullName} has no package id in its manifest; not marked.");
            return null;
        }
        catch (Exception ex) {
            _console.WriteWarning($"Could not read {file.FullName} as a NuGet package ({ex.FormatMessage()}); not marked.");
            return null;
        }
    }

    /// <summary>`<id>.<version>[.symbols].nupkg` or `.snupkg`, where the version starts with a digit right after the id's dot.</summary>
    internal static bool IsPackageFileOf(string fileName, string packageId) {
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(packageId) + @"\.\d+(\.\d+)*([-+][0-9A-Za-z.+-]+)?(\.symbols)?\.(nupkg|snupkg)$";
        return System.Text.RegularExpressions.Regex.IsMatch(fileName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Every project file discovered in this run, not just the one owning a given directory.</summary>
    private IEnumerable<string> AllKnownProjectPaths() => _dirs.Values.SelectMany(d => d.AbsProjPath.Keys);

    /// <summary>Every project directory discovered in this run.</summary>
    private IEnumerable<string> AllKnownProjectDirs() => _dirs.Values.SelectMany(d => d.AbsParentPath);

    /// <summary>
    /// Filesystem check for project/solution files below a candidate. Used where there is no structural
    /// validation to fall back on (an explicit BaseIntermediateOutputPath, or a .vcxproj output dir),
    /// which is exactly where a shared-artifacts layout can point us at a directory holding sources.
    /// </summary>
    private bool ContainsProjectOrSolution(string absPath) {
        try {
            var options = new EnumerationOptions {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple,
            };
            foreach (var pattern in ProjConstants.ProjectAndSolutionGlobs) {
                if (new DirectoryInfo(absPath).EnumerateFiles(pattern, options).Any()) return true;
            }
        }
        catch (Exception ex) {
            // If we cannot prove the directory is safe, treat it as unsafe.
            _console.WriteWarning($"Could not inspect {absPath} for project files ({ex.FormatMessage()}); skipping it.");
            return true;
        }
        return false;
    }

    /// <summary>
    /// TFMs that any discovered project builds into <paramref name="cfgDir"/>. With a shared output path
    /// several projects write into the same Debug/ directory, and treating only the current project's
    /// TFMs as "current" made --non-current delete another project's live output.
    /// </summary>
    private HashSet<string> TfmsClaimedUnder(DirectoryInfo cfgDir) {
        var claimed = new HashSet<string>(DefaultComparer);
        foreach (var other in _dirs.Values) {
            foreach (var (path, type) in other.AbsPath) {
                if (type != DirType.OutDir) continue;
                var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (parent is null) continue;
                if (!string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(cfgDir.FullName), DirExt.PathComparison)) continue;
                claimed.UnionWith(other.Tfms);
            }
        }
        return claimed;
    }

    /// <summary>
    /// TFMs any discovered project builds into the same artifacts directory. Normally that is one project,
    /// but ArtifactsProjectName can be shared.
    /// </summary>
    private HashSet<string> TfmsClaimedForArtifacts(string artifactsDir) {
        var claimed = new HashSet<string>(DefaultComparer);
        foreach (var other in _dirs.Values) {
            if (other.AbsPath.Any(p => p.Type is DirType.ArtifactsBin or DirType.ArtifactsPublish && SamePath(p.Path, artifactsDir))) {
                claimed.UnionWith(other.Tfms);
            }
        }
        return claimed;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), DirExt.PathComparison);

    // RIDs are <os>[.<version>]-<arch>[-<qualifier>], e.g. win-x64, linux-musl-arm64, osx.12-arm64. The dash is
    // required so an unrelated directory such as "debug_backup" is not mistaken for a pivot.
    private static readonly System.Text.RegularExpressions.Regex _ridPattern =
        new(@"^[a-z][a-z0-9.]*(-[a-z0-9]+)+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses an artifacts pivot directory name, <c>&lt;config&gt;[_&lt;tfm&gt;][_&lt;rid&gt;]</c>, as produced by
    /// the SDK for UseArtifactsOutput. The TFM segment is only present for multi-targeted projects.
    /// </summary>
    internal static bool TryParseArtifactsPivots(string name, IEnumerable<string> configurations, out string? tfm) {
        tfm = null;
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var config in configurations) {
            if (string.IsNullOrEmpty(config)) continue;
            if (string.Equals(name, config, DefaultComparison)) return true;
            if (name.Length <= config.Length + 1 || !name.StartsWith(config, DefaultComparison) || name[config.Length] != '_') continue;

            var rest = name[(config.Length + 1)..];
            var separator = rest.IndexOf('_');
            var first = separator < 0 ? rest : rest[..separator];
            var remainder = separator < 0 ? null : rest[(separator + 1)..];

            if (NetUtil.Instance.IsTfmName(first, DefaultComparison)) {
                if (remainder is null || _ridPattern.IsMatch(remainder)) {
                    tfm = first;
                    return true;
                }
                continue;
            }

            // <config>_<rid>: single-target project published for a specific runtime.
            if (remainder is null && _ridPattern.IsMatch(first)) return true;
        }

        return false;
    }

    private readonly HashSet<string> _unrecognizedLayoutWarned = new(PathComparer);

    private bool HasValidateBasicOutDirStructureFlag() => true; // Default to true
    private bool HasCleanOnlyNoncurrentTfmsFlag() => _options.CleanOnlyNonCurrentTfms; // Default to false for now

    /// <summary>
    /// Get the directories marked for deletion
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Dir>> GetMarkedDirectories() =>
        _deleteDirs.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<Dir>)kvp.Value.ToList(), DefaultComparer);

    /// <summary>The single files marked for deletion (package output).</summary>
    public IReadOnlyList<string> GetMarkedFiles() => _deleteFiles.Keys.OrderBy(k => k, PathComparer).ToList();
}


internal static class PathUtils {
    public static string SafeCombine(params string?[] parts) {
        if (parts is null) throw new ArgumentNullException(nameof(parts));
        
        var partsRes = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToArray();
        if (!partsRes.Any()) throw new ArgumentException(nameof(parts));

        return Path.Combine(partsRes);
    }
}
