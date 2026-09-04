using bld.Infrastructure;
using bld.Models;

namespace bld.Services;

internal class MarkDeleteResultDeleteProcessor : IMarkDeleteResultProcessor {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;
    private readonly ErrorSink _errorSink;

    public MarkDeleteResultDeleteProcessor(IConsoleOutput console, ErrorSink errorSink, CleaningOptions options) {
        _console = console;
        _options = options;
        _errorSink = errorSink;
    }

    /// <summary>
    /// Honours --confirm. The option was previously parsed and stored but never read, so every level
    /// behaved like Directory. Sln is treated as "ask once for the run": a marked directory carries its
    /// owning projects but not the solution they came from.
    /// </summary>
    private bool ShouldDelete(DirResult entry, Dictionary<string, bool> answers) {
        if (_options.Force) return true;

        var level = _options.ConfirmLevel ?? ConfirmLevel.Directory;
        if (level == ConfirmLevel.None) return true;

        var path = entry.Directory.FullName;
        var scope = level switch {
            ConfirmLevel.Directory => path,
            ConfirmLevel.Project => entry.References.SelectMany(r => r.AbsProjPath.Keys).OrderBy(p => p).FirstOrDefault() ?? path,
            _ => "*", // Sln: once for the whole run
        };

        if (answers.TryGetValue(scope, out var remembered)) return remembered;

        var prompt = level switch {
            ConfirmLevel.Directory => $"Delete directory {path} and all its contents?",
            ConfirmLevel.Project => $"Delete build output for {scope}?",
            _ => "Delete all marked build output directories?",
        };

        var answer = _console.Confirm(prompt);
        answers[scope] = answer;
        return answer;
    }

    public Task ProcessAsync(MarkDeleteResult result) {
        if (!result.Directories.Any()) {
            _console.WriteLine("No directories marked for deletion.");
            return Task.CompletedTask;
        }

        var answers = new Dictionary<string, bool>(DirExt.PathComparer);

        foreach (var kvp in result.Directories.OrderBy(k => k.Directory.FullName)) {
            var path = kvp.Directory;
            if (path is null) continue;
            // DirectoryInfo caches Exists from when the result set was built; an earlier iteration may
            // have removed this directory as part of a parent. Refresh so we neither prompt for nor
            // report a failure on something that is already gone.
            path.Refresh();
            if (!path.Exists) continue;

            if (ShouldDelete(kvp, answers)) {
                try {
                    path.Delete(true);
                    _console.WriteLine($"Deleted {path.FullName}");
                }
                catch (Exception ex) {
                    // Record it so the run exits non-zero; printing alone let CI treat a failed clean as success.
                    _errorSink.AddError($"Failed to delete directory {path.FullName}.", exception: ex);
                    _console.WriteError($"Failed to delete directory {path.FullName}: {ex.FormatMessage()}");
                }
            }
            else {
                _console.WriteLine($"Skipped deletion of directory {path.FullName}.");
            }
        }

        return Task.CompletedTask;
    }
}
