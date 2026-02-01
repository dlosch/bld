using bld.Infrastructure;
using bld.Models;
using System.Collections.Concurrent;

namespace bld.Services;

internal sealed class MarkDeleteProcessor : IProjectProcessor {
    private readonly IConsoleOutput _console;
    private readonly IFileSystem _fileSystem;
    private readonly CleaningOptions _options;
    private readonly ErrorSink _errorSink;
    private static readonly StringComparer DefaultComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly StringComparison DefaultComparison = StringComparison.OrdinalIgnoreCase;

    // Track directories for deletion - using ConcurrentBag for thread-safe value collection
    private readonly ConcurrentDictionary<string, ConcurrentBag<Dir>> _deleteDirs = new ConcurrentDictionary<string, ConcurrentBag<Dir>>(DefaultComparer);
    private readonly ConcurrentDictionary<string, Dir> _dirs = new ConcurrentDictionary<string, Dir>(DefaultComparer);

    public MarkDeleteProcessor(IConsoleOutput console, IFileSystem fileSystem, CleaningOptions options, ErrorSink errorSink) {
        _console = console;
        _fileSystem = fileSystem;
        _options = options;
        _errorSink = errorSink;

        _enumerateFiles = new EnumerationOptions { MatchType = MatchType.Simple, MaxRecursionDepth = 10 /*options.Depth*/, RecurseSubdirectories = true, ReturnSpecialDirectories = false, IgnoreInaccessible = false };
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
                        // no proj or sln may be below target / inexact science
                        if (dir.AbsProjPath.Any(absProjPath => DirExt.IsNestedBelow(absProjPath.Key, item.path))) {
                            _console.WriteWarning("");
                            return true;
                        }

                        if (dir.AbsParentPath.Any(absProjPath => DirExt.IsNestedBelow(absProjPath, item.path))) {
                            return true;
                        }

                        return false;
                    }

                    if (NotSafeToDelete(dir)) {
                        _console.WriteVerbose($"{dir} is not safe to delete, skipping.");
                        return Task.CompletedTask;
                    }

                    Stats OutDirDelete(string absPath, DirType dirType, Dir dir) {
                        var dirInfo = new DirectoryInfo(absPath);
                        var exists = dirInfo.Exists;

                        //if (!Directory.Exists(absPath)) return default;
                        //if (!dirInfo.Exists) return default;

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

                                IEnumerable<DirectoryInfo> GetCfgNestedAffected(DirectoryInfo cfgDir2, Dir dir2, bool onlyNonCurrent2) => cfgDir2.EnumerateDirectories()
                                        .Where(tfmDir => NetUtil.Instance.IsTfmName(tfmDir.Name, DefaultComparison)
                                        && (!onlyNonCurrent2 || !dir2.Tfms.Contains(tfmDir.Name)));

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

                                            //_console.WriteVerbose($"{absPath} #2");

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
                        if (Directory.Exists(absPath)) {
                            _deleteDirs.GetOrAdd(absPath, _ => new ConcurrentBag<Dir>()).Add(dir);
                        }
                        return default;
                    }

                    Stats VcxDir(string absPath, DirType dirType, Dir dir) {
                        if (Directory.Exists(absPath)) {
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