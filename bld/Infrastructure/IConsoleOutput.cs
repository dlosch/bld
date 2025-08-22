using Spectre.Console;

namespace bld.Infrastructure;

/// <summary>
/// Abstraction for console output using Spectre.Console
/// </summary>
internal interface IConsoleOutput {
    void WriteInfo(string message);
    void WriteWarning(string message);
    void WriteError(string message, Exception? exception = default);
    void WriteDebug(string message);
    void WriteVerbose(string message);

    void WriteTable(Table table);
    void WriteRule(string title);

    bool Confirm(string message, bool defaultValue = false);

    void StartProgress(string description, Action<ProgressContext> action);
    Task StartProgressAsync(string description, Func<ProgressContext, Task> action);
    Task StartStatusAsync(string description, Func<StatusContext, Task> action);
    void WriteException(Exception exception);
    void WriteOutput(string caption, string content);
}