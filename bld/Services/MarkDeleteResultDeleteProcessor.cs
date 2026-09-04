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

    public Task ProcessAsync(MarkDeleteResult result) {
        if (!result.Directories.Any()) {
            _console.WriteLine("No directories marked for deletion.");
            return Task.CompletedTask;
        }

        foreach (var kvp in result.Directories.OrderBy(k => k.Directory.FullName)) {
            var path = kvp.Directory;
            if (path is null) continue;
            // DirectoryInfo caches Exists from when the result set was built; an earlier iteration may
            // have removed this directory as part of a parent. Refresh so we neither prompt for nor
            // report a failure on something that is already gone.
            path.Refresh();
            if (!path.Exists) continue;

            if (_options.Force || _console.Confirm($"Delete directory {path.FullName} and all its contents?")) {
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
