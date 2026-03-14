using Spectre.Console;

namespace bld.Infrastructure;

internal static class ExceptionExtensions {
    /// <summary>
    /// Returns only the first line of an exception message, stripping inner exception
    /// stack traces that some exception types (e.g. SolutionException) embed in Message.
    /// </summary>
    internal static string FormatMessage(this Exception ex) =>
        ex.Message.Split('\n', 2)[0].TrimEnd('\r');
}

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
    T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull;

    void StartProgress(string description, Action<ProgressContext> action);
    Task StartProgressAsync(string description, Func<ProgressContext, Task> action);
    Task StartStatusAsync(string description, Func<StatusContext, Task> action);
    void WriteException(Exception exception);
    void WriteOutput(string caption, string? content = default);
    void WriteHeader(string caption, string? additionaltext = default);
}