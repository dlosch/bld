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
                log?.WriteError($"Deletion of directory {dirInfo.FullName} failed with: {xcptn.FormatMessage()}", xcptn);
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
                    log?.WriteDebug($"Deletion of file {file.FullName} failed with: {xcptn.FormatMessage()}");
                }
            }
            return !hasError;
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

    /// <summary>
    /// Path comparison matching the host filesystem: Windows is case-insensitive, Linux is not.
    /// Using a case-sensitive comparison on Windows made the deletion guards fail *open* whenever a
    /// project's OutDir differed only in case from the project path.
    /// </summary>
    internal static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    internal static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// True when <paramref name="targetRelOrAbs"/> sits strictly below <paramref name="baseDirRooted"/>.
    /// The match is anchored on a directory separator, so "/foo/bar2" is NOT below "/foo/bar" — the
    /// previous length-only test reported sibling directories as nested.
    /// </summary>
    internal static bool IsNestedBelow(string targetRelOrAbs, string baseDirRooted) {
        if (string.IsNullOrWhiteSpace(targetRelOrAbs) || targetRelOrAbs == "." || targetRelOrAbs == "..") return false;
        if (string.IsNullOrWhiteSpace(baseDirRooted)) return false;

        var target = TrimTrailingSeparators(EnsureRooted(targetRelOrAbs, baseDirRooted));
        var baseDir = TrimTrailingSeparators(baseDirRooted);

        if (target.Length <= baseDir.Length) return false;
        if (!target.StartsWith(baseDir, PathComparison)) return false;

        var boundary = target[baseDir.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private static string TrimTrailingSeparators(string path) {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Keep the separator for a root such as "/" or "C:\".
        return trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':') ? path : trimmed;
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
