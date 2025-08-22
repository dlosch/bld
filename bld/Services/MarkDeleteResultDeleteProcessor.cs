using bld.Infrastructure;
using bld.Models;

namespace bld.Services;

internal class MarkDeleteResultDeleteProcessor : IMarkDeleteResultProcessor {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public MarkDeleteResultDeleteProcessor(IConsoleOutput console, ErrorSink errorSink, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    public Task ProcessAsync(MarkDeleteResult result) {
        if (!result.Directories.Any()) {
            _console.WriteInfo("No directories marked for deletion.");
            return Task.CompletedTask;
        }

        foreach (var kvp in result.Directories.OrderBy(k => k.Directory.FullName)) {
            var path = kvp.Directory;
            if (path is null) continue;
            if (!path.Exists) continue;

            if (_console.Confirm($"Delete directory {path.FullName} and all its contents?", _options.Force)) {
                try {
                    path.Delete(true);
                    _console.WriteInfo($"Deleted directory {path.FullName}.");
                }
                catch (Exception ex) {
                    _console.WriteError($"Failed to delete directory {path.FullName}: {ex.Message}");
                }
            }
            else {
                _console.WriteInfo($"Skipped deletion of directory {path.FullName}.");
            }
        }

        return Task.CompletedTask;
    }
}
