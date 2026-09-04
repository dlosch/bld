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
    private readonly ConcurrentDictionary<string, Dir> _dirs = new ConcurrentDictionary<string, Dir>(PathComparer);

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
                results.Add(new DirResult(dirInfo, kvp.Value.ToList()));
            }
        }
        return new MarkDeleteResult(results);
    }

    private async ValueTask AddDir(ProjectInfo info, ProjCfg cfg) {
        var absProjPath = cfg.Path;
        var projName = info.ProjectName;
        var tfms = new HashSet<string>(DefaultComparer);

        // TargetFramework != null > OutDir ok
        // TargetFrameworks != null > OutDir null

        if (info.TargetFramework != null) tfms.Add(info.TargetFramework);
        if (info.TargetFrameworks != null) tfms.UnionWith(info.TargetFrameworks);

        // Add OutDir
        if (!string.IsNullOrEmpty(info.OutDir)) {
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

    internal Task ProcessDirs() {
        if (_dirs.Any()) {
            foreach (var dir in _dirs.Values) {
                foreach ((string path, DirType type) item in dir.AbsPath.Distinct()) {

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
                        }
                        else {
                            if (exists) deleteCandidates = new DirectoryInfo[] { dirInfo };
                        }

                        if (deleteCandidates is { }) {
                            foreach (var d in deleteCandidates) {
                                _deleteDirs.GetOrAdd(d.FullName, _ => new ConcurrentBag<Dir>()).Add(dir);
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
                        if (!_options.CleanObjDirectory) return default;
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
                                _deleteDirs.GetOrAdd(subDir.FullName, _ => new ConcurrentBag<Dir>()).Add(dir);
                            }
                        }
                        else {
                            _deleteDirs.GetOrAdd(absPath, _ => new ConcurrentBag<Dir>()).Add(dir);
                        }
                        return default;
                    }

                    Stats VcxDir(string absPath, DirType dirType, Dir dir) {
                        if (Directory.Exists(absPath)) {
                            if (ContainsProjectOrSolution(absPath)) {
                                _console.WriteWarning($"Skipping {absPath}: it contains project or solution files.");
                                return default;
                            }
                            _deleteDirs.GetOrAdd(absPath, _ => new ConcurrentBag<Dir>()).Add(dir);
                        }
                        return default;
                    }

                    var deleteTask = (dir.ProjType, item.type) switch {
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

    private bool HasValidateBasicOutDirStructureFlag() => true; // Default to true
    private bool HasCleanOnlyNoncurrentTfmsFlag() => _options.CleanOnlyNonCurrentTfms; // Default to false for now

    /// <summary>
    /// Get the directories marked for deletion
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Dir>> GetMarkedDirectories() => 
        _deleteDirs.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<Dir>)kvp.Value.ToList(), DefaultComparer);
}


internal static class PathUtils {
    public static string SafeCombine(params string?[] parts) {
        if (parts is null) throw new ArgumentNullException(nameof(parts));
        
        var partsRes = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToArray();
        if (!partsRes.Any()) throw new ArgumentException(nameof(parts));

        return Path.Combine(partsRes);
    }
}
