using bld.Infrastructure;
using Spectre.Console;
using Xunit.Abstractions;

namespace bld.Tests;

/// <summary>
/// Shared IConsoleOutput implementation for tests.
/// Optionally writes to xUnit's <see cref="ITestOutputHelper"/> so that
/// test output appears in the test runner, and records all messages for assertions.
/// </summary>
internal sealed class TestConsole : IConsoleOutput {
    private readonly ITestOutputHelper? _output;

    public TestConsole(ITestOutputHelper? output = null) => _output = output;

    public List<(string Level, string Message)> Messages { get; } = new();

    public void WriteLine(string message) { Log("Line", message); }
    public void WriteInfo(string message) { Log("Info", message); }
    public void WriteWarning(string message) { Log("Warning", message); }
    public void WriteError(string message, Exception? exception = default) { Log("Error", message); }
    public void WriteDebug(string message) { Log("Debug", message); }
    public void WriteVerbose(string message) { Log("Verbose", message); }
    public void WriteTable(Table table) { }
    public void WriteRule(string title) { Log("Rule", title); }
    public bool Confirm(string message, bool defaultValue = false) => defaultValue;
    public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull => default!;
    public List<T> MultiPrompt<T>(MultiSelectionPrompt<T> prompt) where T : notnull => new();
    public void StartProgress(string description, Action<ProgressContext> action) => action(null!);
    public Task StartProgressAsync(string description, Func<ProgressContext, Task> action) => action(null!);
    public Task StartStatusAsync(string description, Func<StatusContext, Task> action) => action(null!);
    public void WriteException(Exception exception) { Log("Exception", exception.Message); }
    public void WriteOutput(string caption, string? content = default) { Log("Output", caption); }
    public void WriteHeader(string caption, string? additionaltext = default) { Log("Header", caption); }

    private void Log(string level, string message) {
        Messages.Add((level, message));
        _output?.WriteLine($"[{level}] {message}");
    }
}
