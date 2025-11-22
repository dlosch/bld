

using bld.Models;
using bld.Services;

namespace bld.Infrastructure;

internal class SlnScanner(CleaningOptions Options, ErrorSink ErrorSink) {

    public async IAsyncEnumerable<string> Enumerate(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            yield break;
        }

        if (File.Exists(path) && Options.FileNameFilter(path)) {
            yield return path;
            yield break;
        }

        await foreach (var slnFile in EnumerateSlnFiles(path)) {
            yield return slnFile;
        }
    }

    public async IAsyncEnumerable<string> EnumerateSlnFiles(string path) {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Directory cannot be null or empty.", nameof(path));

        var pathRooted = DirExt.EnsureRooted(path, Environment.CurrentDirectory);
        if (!Directory.Exists(pathRooted)) {
            ErrorSink.AddError($"Input path {path} (translated to {pathRooted}) not found.");
            yield break;
        }

        var fileSearcher = Directory.EnumerateFiles(pathRooted, Options!.Filter, new EnumerationOptions {
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            MatchType = MatchType.Win32,
            MaxRecursionDepth = Options.Depth,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        });

        foreach (var file in fileSearcher) {
            if (Options.FileNameFilter is null || Options.FileNameFilter(file)) {
                yield return file;
            }
        }
    }
}
