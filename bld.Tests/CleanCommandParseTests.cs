using bld.Commands;

namespace bld.Tests;

/// <summary>
/// The command layer had no parse tests, and A1.1 (--delete -o deleted instead of writing a script)
/// would have been caught by one. These drive the real <see cref="CleanCommand"/> parser and assert
/// the precedence between picker, direct delete and script, plus the argument-level errors.
/// </summary>
public class CleanCommandParseTests {

    private static (CleanAction Action, string? Error) Resolve(params string[] args) {
        var command = new CleanCommand(new TestConsole());
        return command.ResolveAction(command.Parse(args));
    }

    [Fact]
    public void Bare_CleanShowsThePicker() {
        var (action, error) = Resolve();
        Assert.Equal(CleanAction.Picker, action);
        Assert.Null(error);
    }

    [Fact]
    public void Delete_TurnsThePickerOff() {
        var (action, error) = Resolve("--delete");
        Assert.Equal(CleanAction.Delete, action);
        Assert.Null(error);
    }

    [Fact]
    public void OutputFile_WritesTheScriptWithoutThePicker() {
        var (action, error) = Resolve("-o", "clean.sh");
        Assert.Equal(CleanAction.Script, action);
        Assert.Null(error);
    }

    /// <summary>A1.1: --delete deletes now, --output-file writes a script to run later; together they contradict.</summary>
    [Fact]
    public void DeleteWithOutputFile_IsRejected() {
        var (_, error) = Resolve("--delete", "-o", "clean.sh");
        Assert.NotNull(error);
        Assert.Contains("--output-file", error);
    }

    /// <summary>A1.1: an explicit -i wins the otherwise contradictory pair — the picker writes the script.</summary>
    [Fact]
    public void InteractiveWithDeleteAndOutputFile_IsFine() {
        var (action, error) = Resolve("-i", "--delete", "-o", "clean.sh");
        Assert.Equal(CleanAction.Picker, action);
        Assert.Null(error);
    }

    [Fact]
    public void InteractiveWithDelete_StillShowsThePicker() {
        var (action, error) = Resolve("-i", "--delete");
        Assert.Equal(CleanAction.Picker, action);
        Assert.Null(error);
    }

    [Fact]
    public void ForceWithoutRoot_IsRejected() {
        var (_, error) = Resolve("--force");
        Assert.NotNull(error);
        Assert.Contains("--force", error);
    }

    [Fact]
    public void ForceWithARoot_IsFine() {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bld_parse_" + Guid.NewGuid().ToString("N"))).FullName;
        try {
            var (_, error) = Resolve("--delete", "--force", "-r", root);
            Assert.Null(error);
        }
        finally {
            Directory.Delete(root);
        }
    }
}
