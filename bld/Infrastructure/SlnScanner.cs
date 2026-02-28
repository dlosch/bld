

using bld.Models;
using bld.Services;

namespace bld.Infrastructure;

internal class SlnScanner(CleaningOptions Options, ErrorSink ErrorSink) {

    public async IAsyncEnumerable<string> Enumerate(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            yield break;
        }

        if (File.Exists(path)) {
            if (Options.FileNameFilter(path) || IsProjectFile(path) || IsNpmProjectFile(path)) {
                yield return path;
            }
            yield break;
        }

        bool foundSln = false;
        await foreach (var slnFile in EnumerateFiles(path, Options!.Filter)) {
            foundSln = true;
            yield return slnFile;
        }

        if (!foundSln) {
            await foreach (var projFile in EnumerateFiles(path, "*.*proj")) {
                if (IsProjectFile(projFile)) {
                    yield return projFile;
                }
            }
        }

        // Also look for npm projects (package.json not inside node_modules)
        await foreach (var npmFile in EnumerateNpmFiles(path)) {
            yield return npmFile;
        }
    }

    public static bool IsProjectFile(string file) {
        var ext = Path.GetExtension(file);
        return ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sqlproj", StringComparison.OrdinalIgnoreCase)
            // || ext.Equals(".proj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNpmProjectFile(string file) =>
        Path.GetFileName(file).Equals("package.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsInsideNodeModules(string filePath) =>
        filePath.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || filePath.Contains($"{Path.AltDirectorySeparatorChar}node_modules{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<string> EnumerateSlnFiles(string path) {
        await foreach (var file in EnumerateFiles(path, Options!.Filter)) {
            yield return file;
        }
    }

    private async IAsyncEnumerable<string> EnumerateFiles(string path, string filter) {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Directory cannot be null or empty.", nameof(path));

        var pathRooted = DirExt.EnsureRooted(path, Environment.CurrentDirectory);
        if (!Directory.Exists(pathRooted)) {
            ErrorSink.AddError($"Input path {path} (translated to {pathRooted}) not found.");
            yield break;
        }

        var fileSearcher = Directory.EnumerateFiles(pathRooted, filter, new EnumerationOptions {
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            MatchType = MatchType.Win32,
            MaxRecursionDepth = Options.Depth,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        });

        foreach (var file in fileSearcher) {
            if (Options.FileNameFilter is null || Options.FileNameFilter(file) || IsProjectFile(file)) {
                yield return file;
            }
        }
    }

    private async IAsyncEnumerable<string> EnumerateNpmFiles(string path) {
        if (string.IsNullOrWhiteSpace(path)) yield break;

        var pathRooted = DirExt.EnsureRooted(path, Environment.CurrentDirectory);
        if (!Directory.Exists(pathRooted)) yield break;

        var fileSearcher = Directory.EnumerateFiles(pathRooted, "package.json", new EnumerationOptions {
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            MatchType = MatchType.Simple,
            MaxRecursionDepth = Options.Depth,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        });

        foreach (var file in fileSearcher) {
            if (IsNpmProjectFile(file) && !IsInsideNodeModules(file)) {
                yield return file;
            }
        }
    }
}
