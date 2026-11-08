using bld.Commands;

namespace bld.Tests;

/// <summary>
/// `outdated` opens the picker by default, like `clean`: these drive the real parser and assert when
/// the picker is on, when `--apply` or `--markdown` turn it off, and what a missing terminal does.
/// </summary>
public class OutdatedCommandParseTests {

    private static bool Interactive(bool canPrompt, params string[] args) {
        var command = new OutdatedCommand(new TestConsole());
        var parsed = command.Parse(args);
        Assert.Empty(parsed.Errors);
        return command.ResolveInteractive(parsed, canPrompt);
    }

    [Fact]
    public void Bare_OutdatedShowsThePickerInATerminal() => Assert.True(Interactive(canPrompt: true));

    [Fact]
    public void Bare_OutdatedWithoutATerminalReports() => Assert.False(Interactive(canPrompt: false));

    [Theory]
    [InlineData("--apply")]
    [InlineData("--markdown")]
    [InlineData("--apply", "--max-bump", "minor")]
    public void ApplyOrMarkdown_TurnThePickerOff(params string[] args) => Assert.False(Interactive(canPrompt: true, args));

    [Theory]
    [InlineData("-i")]
    [InlineData("--interactive")]
    [InlineData("-i", "--apply")]
    [InlineData("-i", "--markdown")]
    public void ExplicitInteractive_WinsOverApplyAndMarkdown(params string[] args) => Assert.True(Interactive(canPrompt: true, args));

    /// <summary>The service then fails with its hint, as it always did for an explicit -i.</summary>
    [Fact]
    public void ExplicitInteractive_StaysOnWithoutATerminal() => Assert.True(Interactive(canPrompt: false, "-i"));

    [Fact]
    public void ExplicitInteractiveFalse_Reports() => Assert.False(Interactive(canPrompt: true, "--interactive", "false"));
}
