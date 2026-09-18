using bld.Services;
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
    /// <summary>Always-visible output line — use for command results and user-facing data.</summary>
    void WriteLine(string message);
    /// <summary>Informational log message — only shown when log level is Info or lower.</summary>
    void WriteInfo(string message);
    void WriteWarning(string message);
    void WriteError(string message, Exception? exception = default);
    void WriteDebug(string message);
    void WriteVerbose(string message);

    void WriteTable(Table table);
    void WriteRule(string title);

    /// <summary>
    /// Whether <see cref="Prompt{T}"/> and <see cref="MultiPrompt{T}"/> can actually read input.
    /// False for redirected stdin and non-interactive terminals, where both throw.
    /// </summary>
    bool CanPrompt { get; }

    bool Confirm(string message, bool defaultValue = false);
    T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull;
    List<T> MultiPrompt<T>(MultiSelectionPrompt<T> prompt) where T : notnull;

    /// <summary>
    /// Drives the package picker until the user confirms or cancels. The caller supplies the model;
    /// the implementation owns only the key loop and the drawing, so the decisions stay in
    /// <see cref="PickerState"/>.
    /// </summary>
    PickerOutcome RunPicker(PickerModel model, string title);

    void StartProgress(string description, Action<ProgressContext> action);
    Task StartProgressAsync(string description, Func<ProgressContext, Task> action);
    Task StartStatusAsync(string description, Func<StatusContext, Task> action);
    void WriteException(Exception exception);
    void WriteOutput(string caption, string? content = default);
    void WriteHeader(string caption, string? additionaltext = default);
}