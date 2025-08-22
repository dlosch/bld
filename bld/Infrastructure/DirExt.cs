using System.Diagnostics.CodeAnalysis;

namespace bld.Infrastructure;

internal static class DirExt {

    internal static bool SafeDelete(this DirectoryInfo dirInfo, bool inclSubdirectories, bool deleteFiles, IConsoleOutput? log, EnumerationOptions? enumerateFiles = default) {
        if (dirInfo is null) return false;
        if (!dirInfo.Exists) return false;

        if (!deleteFiles) {
            try {
                dirInfo.Delete(inclSubdirectories);
                return true;
            }
            catch (Exception xcptn) {
                log?.WriteError($"Deletion of directory {dirInfo.FullName} failed with: {xcptn.Message}.", xcptn);
                return false;
            }
        }
        else {
            if (enumerateFiles is null) return false;

            var hasError = false;
            foreach (var file in dirInfo.EnumerateFiles("*", enumerateFiles)) {
                log?.WriteDebug($"Deleting {file.FullName}");
                try {
                    file.Delete();
                }
                catch (Exception xcptn) {
                    hasError = true;
                    log?.WriteDebug($"Deletion of file {file.FullName} failed with: {xcptn.Message}.");
                }
            }
            return hasError;
        }
    }

    internal static bool IsEmpty(this DirectoryInfo dirInfo) => !dirInfo.IsNotEmpty();
    internal static bool IsNotEmpty(this DirectoryInfo dirInfo) => dirInfo.EnumerateFiles().Any() || dirInfo.EnumerateDirectories().Any();

    static readonly char[] _invalidPathChars = Path.GetInvalidPathChars();

    // todo doesnt filter alternate data streams and all sorts of device prefixes
    internal static bool NormalizePath(string? candidate, string baseDir, [NotNullWhen(true)] out string? candidateNormalized) {
        candidateNormalized = default;

        if (candidate is null) return false;
        if (string.IsNullOrEmpty(candidate)) return false;
        if (candidate == "." || candidate == "..") {
            // todo 
            candidateNormalized = EnsureRooted(candidate, Environment.CurrentDirectory);
            return true;
        }
        if (candidate.TrimStart().StartsWith(@"\\.\")) return false;
        //if (candidate.TrimStart().StartsWith(@"\\?\")) {
        //    //if (PathInternal.IsExtended(path.AsSpan())) {
        //    //    // \\?\ paths are considered normalized by definition. Windows doesn't normalize \\?\
        //    //    // paths and neither should we. Even if we wanted to GetFullPathName does not work
        //    //    // properly with device paths. If one wants to pass a \\?\ path through normalization
        //    //    // one can chop off the prefix, pass it to GetFullPath and add it again.
        //    //    return path;
        //    //}

        //    return false;
        //}
        if (candidate.TrimStart().StartsWith(@"~")) return false;

        if (-1 < candidate.IndexOfAny(_invalidPathChars)) return false;

        // if (candidate[0]== '%' || candidate[0] == '$' && 0 != string.Compare(candidate, Environment.ExpandEnvironmentVariables(candidate), StringComparison.OrdinalIgnoreCase)) return false;

        candidateNormalized = EnsureRooted(candidate, baseDir);

        return candidateNormalized != null;
    }

    internal static bool IsNestedBelow(string targetRelOrAbs, string baseDirRooted) {
        if (string.IsNullOrWhiteSpace(targetRelOrAbs) || 0 == string.Compare(".", targetRelOrAbs, StringComparison.Ordinal) || 0 == string.Compare("..", targetRelOrAbs, StringComparison.Ordinal)) return false;
        targetRelOrAbs = EnsureRooted(targetRelOrAbs, baseDirRooted);

        return targetRelOrAbs.StartsWith(baseDirRooted)
            &&
            (
                (targetRelOrAbs.Length - baseDirRooted.Length >= 2)
                || (Path.EndsInDirectorySeparator(baseDirRooted) == Path.EndsInDirectorySeparator(targetRelOrAbs) && targetRelOrAbs.Length > baseDirRooted.Length)
                || (Path.EndsInDirectorySeparator(baseDirRooted) && targetRelOrAbs.Length > baseDirRooted.Length)
                || ((targetRelOrAbs.Length - 1) > baseDirRooted.Length)
            );
    }

    internal static string EnsureRooted(string absOrRelative, string baseDir) {
        if (Path.IsPathFullyQualified(absOrRelative)) {

            if (absOrRelative.StartsWith(@"\\.\")) throw new ArgumentException("Drive paths prefixed with \\\\.\\ are not supported.");

            if (absOrRelative.StartsWith(@"\\?\")) {
                if (absOrRelative.Length == 4) return baseDir;
                absOrRelative = absOrRelative.Substring(4);
                return EnsureRooted(absOrRelative, baseDir);
            }
            return Path.GetFullPath(absOrRelative);
        }

        var intermediate = Path.Combine(baseDir, absOrRelative);
        return Path.GetFullPath(intermediate);
    }

    internal static bool HasSubDir(this DirectoryInfo dir, string subdir) {
        return dir.GetDirectories().Any(x => 0 == string.Compare("obj", x.Name, StringComparison.OrdinalIgnoreCase));
    }
    internal static bool OnlyHasSubDirsOrSubset(this DirectoryInfo dir, bool checkForFiles = true, params string[] subdirs) {
        if (checkForFiles && dir.GetFiles().Any()) return false;

        // stupid
        var dirs = dir.GetDirectories().Select(sd => sd.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var distinct = subdirs.Distinct(StringComparer.OrdinalIgnoreCase);
        var cNotFound = 0;
        foreach (var subdir in distinct) {
            if (!dirs.Contains(subdir)) {
                cNotFound++;
            }
        }
        return dirs.Count <= (distinct.Count() - cNotFound);
    }

    internal static bool Exists(string dir) => Directory.Exists(dir) || (dir.EndsWith(Path.DirectorySeparatorChar) && dir.Length > 1 && Directory.Exists(dir.TrimEnd(Path.DirectorySeparatorChar)));
}
