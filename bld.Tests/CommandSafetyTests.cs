using bld.Infrastructure;
using bld.Commands;
using Spectre.Console;
using System.Reflection;

namespace bld.Tests;

/// <summary>
/// Tests to validate that beta commands have proper dry-run/safe behavior.
/// All commands that modify files should require explicit --apply or --delete flags.
/// </summary>
public class CommandSafetyTests {
    private class TestConsole : IConsoleOutput {
        public List<string> InfoMessages { get; } = new();
        public List<string> WarningMessages { get; } = new();
        public List<string> ErrorMessages { get; } = new();

        public void WriteInfo(string message) => InfoMessages.Add(message);
        public void WriteWarning(string message) => WarningMessages.Add(message);
        public void WriteError(string message, Exception? exception = default) => ErrorMessages.Add(message);
        public void WriteDebug(string message) { }
        public void WriteVerbose(string message) { }
        public void WriteTable(Table table) { }
        public void WriteRule(string title) { }
        public bool Confirm(string message, bool defaultValue = false) => defaultValue;
        public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull => default!;
        public void StartProgress(string description, Action<ProgressContext> action) { }
        public Task StartProgressAsync(string description, Func<ProgressContext, Task> action) => Task.CompletedTask;
        public Task StartStatusAsync(string description, Func<StatusContext, Task> action) => Task.CompletedTask;
        public void WriteException(Exception exception) { }
        public void WriteOutput(string caption, string? content = default) { }
        public void WriteHeader(string caption, string? additionaltext = default) { }
    }

    #region TFM Command Safety

    [Fact]
    public void TfmCommand_HasApplyOption() {
        var console = new TestConsole();
        var command = new TfmCommand(console);

        var applyOptionField = typeof(TfmCommand).GetField("_applyOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(applyOptionField);
        var applyOption = applyOptionField.GetValue(command);
        Assert.NotNull(applyOption);
    }

    [Fact]
    public void TfmCommand_ApplyDefaultIsFalse() {
        var console = new TestConsole();
        var command = new TfmCommand(console);

        var applyOptionField = typeof(TfmCommand).GetField("_applyOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(applyOptionField);
        var applyOption = applyOptionField.GetValue(command);
        Assert.NotNull(applyOption);

        // Check that the default is false (dry-run mode)
        var defaultFactoryProperty = applyOption.GetType().GetProperty("DefaultValueFactory");
        var defaultFactory = defaultFactoryProperty?.GetValue(applyOption) as Func<object?, bool>;
        if (defaultFactory != null) {
            var defaultValue = defaultFactory(null);
            Assert.False(defaultValue);
        }
    }

    #endregion

    #region CPM Command Safety

    [Fact]
    public void CpmCommand_HasApplyOption() {
        var console = new TestConsole();
        var command = new CpmCommand(console);

        var applyOptionField = typeof(CpmCommand).GetField("_applyOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(applyOptionField);
        var applyOption = applyOptionField.GetValue(command);
        Assert.NotNull(applyOption);
    }

    [Fact]
    public void CpmCommand_HasOverwriteOption() {
        var console = new TestConsole();
        var command = new CpmCommand(console);

        var overwriteOptionField = typeof(CpmCommand).GetField("_overwriteOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(overwriteOptionField);
        var overwriteOption = overwriteOptionField.GetValue(command);
        Assert.NotNull(overwriteOption);
    }

    #endregion

    #region Outdated Command Safety

    [Fact]
    public void OutdatedCommand_HasApplyOption() {
        var console = new TestConsole();
        var command = new OutdatedCommand(console);

        var applyOptionField = typeof(OutdatedCommand).GetField("_applyOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(applyOptionField);
        var applyOption = applyOptionField.GetValue(command);
        Assert.NotNull(applyOption);
    }

    #endregion

    #region Clean Command Safety

    [Fact]
    public void CleanCommand_HasDeleteOption() {
        var console = new TestConsole();
        var command = new CleanCommand(console);

        var deleteOptionField = typeof(CleanCommand).GetField("_deleteOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(deleteOptionField);
        var deleteOption = deleteOptionField.GetValue(command);
        Assert.NotNull(deleteOption);
    }

    [Fact]
    public void CleanCommand_HasForceOption() {
        var console = new TestConsole();
        var command = new CleanCommand(console);

        var forceOptionField = typeof(CleanCommand).GetField("_forceOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(forceOptionField);
        var forceOption = forceOptionField.GetValue(command);
        Assert.NotNull(forceOption);
    }

    #endregion

    #region Stats Command Safety (Read-Only)

    [Fact]
    public void StatsCommand_HasNoDeleteOption() {
        // Stats command should be read-only
        var console = new TestConsole();
        var command = new StatsCommand(console);

        var deleteOptionField = typeof(StatsCommand).GetField("_deleteOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // StatsCommand should NOT have a delete option
        Assert.Null(deleteOptionField);
    }

    #endregion

    #region NuGet Command Safety (Read-Only)

    [Fact]
    public void NugetCommand_HasNoApplyOption() {
        // NuGet command (analysis only) should be read-only
        var console = new TestConsole();
        var command = new NugetCommand(console);

        var applyOptionField = typeof(NugetCommand).GetField("_applyOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // NugetCommand should NOT have an apply option since it's read-only
        Assert.Null(applyOptionField);
    }

    #endregion

    #region Containerize Command Safety (Read-Only)

    [Fact]
    public void ContainerizeCommand_IsReadOnly() {
        // Containerize command should be read-only (just scanning)
        var console = new TestConsole();
        var command = new ContainerizeCommand(console);

        var applyOptionField = typeof(ContainerizeCommand).GetField("_applyOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // ContainerizeCommand should NOT have an apply option since it's read-only
        Assert.Null(applyOptionField);
    }

    #endregion
}
