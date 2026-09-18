using bld.Infrastructure;
using bld.Commands;
using System.CommandLine;
using System.Reflection;

namespace bld.Tests;

/// <summary>
/// Tests to validate that beta commands have proper dry-run/safe behavior.
/// All commands that modify files should require explicit --apply or --delete flags.
///
/// Note: These tests use reflection to verify that commands have the required options.
/// While reflection-based tests are more tightly coupled to implementation, they ensure
/// that the safety mechanisms (--apply, --delete flags) exist and are properly configured.
/// This is important for publishing quality assurance.
/// </summary>
public class CommandSafetyTests {

    #region TFM Command Safety

    /// <summary>
    /// Validates that TfmCommand has an --apply option to prevent accidental file modifications.
    /// </summary>
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

    [Fact]
    public void OutdatedCommand_UndoIsASubcommandThatNeedsYesOrATerminalAndTakesNoApply() {
        var command = new OutdatedCommand(new TestConsole());
        var undo = Assert.IsType<OutdatedUndoCommand>(Assert.Single(command.Subcommands));

        // "undo" after outdated is the subcommand, not the root argument.
        var parsed = new System.CommandLine.RootCommand { command }.Parse("outdated undo --run 2 -p \"MassTransit*\" --yes");
        Assert.Empty(parsed.Errors);
        Assert.Same(undo, parsed.CommandResult.Command);
        Assert.DoesNotContain(undo.Options, o => o.Name == "--apply");
        Assert.Contains(undo.Options, o => o.Name == "--yes");

        var bad = new System.CommandLine.RootCommand { command }.Parse("outdated undo --run 0");
        Assert.NotEmpty(bad.Errors);
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

    [Fact]
    public async Task CleanCommand_ForceWithoutExplicitRoot_FailsFast() {
        var console = new TestConsole();
        var command = new CleanCommand(console);

        var exitCode = await command.Parse(["--force"]).InvokeAsync();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task StatsCommand_ZeroConcurrency_FailsValidation() {
        // --concurrency 0 would otherwise make Parallel.ForEachAsync throw; it must be rejected.
        var console = new TestConsole();
        var command = new StatsCommand(console);

        var exitCode = await command.Parse(["--concurrency", "0"]).InvokeAsync();

        Assert.NotEqual(0, exitCode);
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

    #region Containerize Command Safety

    [Fact]
    public void ContainerizeCommand_MigrateIsDryRunByDefault() {
        // --migrate writes project files, so it needs the same --apply gate as the other commands.
        var console = new TestConsole();
        var command = new ContainerizeCommand(console);

        var applyOptionField = typeof(ContainerizeCommand).GetField("_applyOption",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(applyOptionField);
        var applyOption = Assert.IsType<Option<bool>>(applyOptionField.GetValue(command));
        Assert.NotNull(applyOption.DefaultValueFactory);
        Assert.False(applyOption.DefaultValueFactory(null!));
    }

    #endregion
}
