using bld.Infrastructure;
using bld.Services;
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

    /// <summary>Answers handed to <see cref="Confirm"/> in order; the default is used once empty.</summary>
    public Queue<bool> ConfirmAnswers { get; } = new();

    /// <summary>
    /// Picks the entries a <see cref="MultiPrompt{T}"/> returns. Receives the prompt so a test can
    /// answer differently per call; returning null means "nothing selected".
    /// </summary>
    public Func<object, System.Collections.IList?>? MultiPromptAnswer { get; set; }

    public bool CanPrompt { get; set; } = true;

    /// <summary>
    /// Keys fed to the picker, in order. They drive the real <see cref="PickerState"/>, so a test
    /// exercises the same decisions a terminal would. An empty queue confirms immediately, which
    /// means "keep the pre-selection".
    /// </summary>
    public Queue<PickerKey> PickerKeys { get; } = new();

    /// <summary>The picker models this console was asked to show, for assertions on the layout.</summary>
    public List<PickerModel> PickerModels { get; } = new();

    public void WriteLine(string message) { Log("Line", message); }
    public void WriteInfo(string message) { Log("Info", message); }
    public void WriteWarning(string message) { Log("Warning", message); }
    public void WriteError(string message, Exception? exception = default) { Log("Error", message); }
    public void WriteDebug(string message) { Log("Debug", message); }
    public void WriteVerbose(string message) { Log("Verbose", message); }
    public void WriteTable(Table table) { }
    public void WriteRule(string title) { Log("Rule", title); }
    public bool Confirm(string message, bool defaultValue = false) => ConfirmAnswers.Count > 0 ? ConfirmAnswers.Dequeue() : defaultValue;
    public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull => default!;
    public List<T> MultiPrompt<T>(MultiSelectionPrompt<T> prompt) where T : notnull =>
        MultiPromptAnswer?.Invoke(prompt)?.Cast<T>().ToList() ?? new List<T>();

    public PickerOutcome RunPicker(PickerModel model, string title) {
        PickerModels.Add(model);
        Log("Picker", title);
        var state = new PickerState(model);
        while (!state.Done && PickerKeys.Count > 0) state.Handle(PickerKeys.Dequeue());
        return state.Result();
    }
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
