using bld.Models;
using bld.Services;
using System.Text;

namespace bld.Tests;

/// <summary>
/// The undo journal and <c>bld outdated undo</c>: every apply helper records what it wrote, and undo
/// only takes back values that still read as the run left them.
/// </summary>
public class UndoServiceTests : IDisposable {
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bld-undo-{Guid.NewGuid():N}");
    private readonly UpdateJournal _journal;
    private readonly string _props;
    private readonly string _proj;
    private readonly string _input;
    private readonly byte[] _propsBytes;
    private readonly byte[] _projBytes;

    private const string PropsText =
        "<Project>\r\n" +
        "  <ItemGroup>\r\n" +
        "    <PackageVersion Include=\"MassTransit\" Version=\"8.4.1\" />\r\n" +
        "    <PackageVersion Include=\"MassTransit.RabbitMQ\" Version=\"8.4.1\" />\r\n" +
        "    <PackageVersion Include=\"Polly\" Version=\"8.4.1\" />\r\n" +
        "    <GlobalPackageReference Include=\"Nerdbank.GitVersioning\" Version=\"3.6.0\" />\r\n" +
        "  </ItemGroup>\r\n" +
        "</Project>\r\n";

    private const string ProjText =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        "  <ItemGroup>\n" +
        "    <PackageReference Include=\"Serilog\" VersionOverride=\"4.1.0\" />\n" +
        "    <PackageReference Include=\"xunit\" Version=\"2.9.0\" />\n" +
        "    <PackageDownload Include=\"Microsoft.CodeAnalysis\" Version=\"[4.10.0];[4.8.0]\" />\n" +
        "  </ItemGroup>\n" +
        "</Project>\n";

    public UndoServiceTests() {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        _journal = new UpdateJournal(Path.Combine(_dir, "history"));
        _props = Path.Combine(_dir, "Directory.Packages.props");
        _proj = Path.Combine(_dir, "src", "App.csproj");
        _input = Path.Combine(_dir, "App.slnx");
        // CRLF plus BOM on the props file, LF without BOM on the project: undo must give both back unchanged.
        File.WriteAllText(_props, PropsText, new UTF8Encoding(true));
        File.WriteAllText(_proj, ProjText, new UTF8Encoding(false));
        File.WriteAllText(_input, "<Solution />");
        _propsBytes = File.ReadAllBytes(_props);
        _projBytes = File.ReadAllBytes(_proj);
    }

    public void Dispose() {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private OutdatedService Apply(TestConsole? console = null) =>
        new(console ?? new TestConsole(), new CleaningOptions()) { Journal = _journal };

    /// <summary>The run every test starts from: two CPM bumps, an orphan, and three project edits.</summary>
    private async Task RecordFullRunAsync() {
        var service = Apply();
        service.BeginJournal();
        await service.UpdatePropsFileAsync(_props, new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase) {
            ["MassTransit"] = ("9.0.0", "8.4.1"),
            ["MassTransit.RabbitMQ"] = ("9.0.0", "8.4.1"),
            ["Nerdbank.GitVersioning"] = ("3.7.0", "3.6.0"),
        }, new[] { "Polly" }, default);
        await service.UpdatePackageVersionAsync(_proj, "Serilog", ("4.2.0", "4.1.0", VersionReason.VersionOverrideProj), default);
        await service.UpdatePackageVersionAsync(_proj, "xunit", ("2.9.3", "2.9.0", VersionReason.PackageReferenceProj), default);
        await service.UpdatePackageVersionAsync(_proj, "Microsoft.CodeAnalysis", ("4.12.0", "4.10.0", VersionReason.PackageDownloadProj), default);
        service.RecordJournal(_input, "outdated --apply");
    }

    private Task<int> UndoAsync(TestConsole console, bool list = false, int run = 1, string[]? include = null, string[]? exclude = null, bool interactive = false, bool yes = true) =>
        new UndoService(console, _journal).RunAsync(_input, list, run, include ?? Array.Empty<string>(), exclude ?? Array.Empty<string>(), interactive, yes, verifyRestore: false, default);

    [Fact]
    public async Task Apply_RecordsEveryKindOfEditWithTheSpellingFromTheFile() {
        await RecordFullRunAsync();

        var runs = _journal.Load(_input, new TestConsole());
        var entry = Assert.Single(runs).Entry;

        Assert.Equal("outdated --apply", entry.Command);
        Assert.Equal(Path.GetFullPath(_input), entry.Input);
        Assert.Contains(entry.Edits, e => e.Kind == EditKind.PackageVersion && e.Package == "MassTransit" && e.From == "8.4.1" && e.To == "9.0.0");
        Assert.Contains(entry.Edits, e => e.Kind == EditKind.PackageVersion && e.Package == "Nerdbank.GitVersioning" && e.To == "3.7.0");
        Assert.Contains(entry.Edits, e => e.Kind == EditKind.OrphanComment && e.Package == "Polly" && e.To is null && e.From.Contains("Version=\"8.4.1\""));
        Assert.Contains(entry.Edits, e => e.Kind == EditKind.VersionOverride && e.Package == "Serilog" && e.From == "4.1.0" && e.To == "4.2.0");
        Assert.Contains(entry.Edits, e => e.Kind == EditKind.PackageReference && e.Package == "xunit" && e.To == "2.9.3");
        // The download slot is the one list entry, not the whole list.
        Assert.Contains(entry.Edits, e => e.Kind == EditKind.PackageDownload && e.Package == "Microsoft.CodeAnalysis" && e.From == "[4.10.0]" && e.To == "[4.12.0]");
        Assert.Equal(7, entry.Edits.Count);
    }

    [Fact]
    public async Task Apply_ThatChangesNothingRecordsNoRun() {
        var service = Apply();
        service.BeginJournal();
        await service.UpdatePropsFileAsync(_props, new Dictionary<string, (string, string?)> { ["MassTransit"] = ("8.4.1", "8.4.1") }, Array.Empty<string>(), default);
        service.RecordJournal(_input, "outdated --apply");

        Assert.Empty(_journal.Load(_input, new TestConsole()));
        Assert.False(Directory.Exists(_journal.DirectoryFor(_input)));
    }

    [Fact]
    public async Task Undo_RestoresTheFilesByteForByteAndDropsTheRun() {
        await RecordFullRunAsync();
        Assert.NotEqual(_propsBytes, await File.ReadAllBytesAsync(_props));

        var console = new TestConsole();
        var exit = await UndoAsync(console);

        Assert.Equal(0, exit);
        Assert.Equal(_propsBytes, await File.ReadAllBytesAsync(_props));
        Assert.Equal(_projBytes, await File.ReadAllBytesAsync(_proj));
        Assert.Empty(_journal.Load(_input, console));
        Assert.DoesNotContain(console.Messages, m => m.Level == "Warning");
    }

    [Fact]
    public async Task Undo_SkipsAValueChangedByHandAndKeepsThatEditInTheJournal() {
        await RecordFullRunAsync();
        var edited = (await File.ReadAllTextAsync(_props)).Replace("Include=\"MassTransit\" Version=\"9.0.0\"", "Include=\"MassTransit\" Version=\"9.0.1\"");
        await File.WriteAllTextAsync(_props, edited);

        var console = new TestConsole();
        var exit = await UndoAsync(console);

        Assert.Equal(0, exit);
        var text = await File.ReadAllTextAsync(_props);
        Assert.Contains("Include=\"MassTransit\" Version=\"9.0.1\"", text);
        Assert.Contains("Include=\"MassTransit.RabbitMQ\" Version=\"8.4.1\"", text);
        Assert.Contains(console.Messages, m => m.Level == "Warning" && m.Message.Contains("MassTransit") && m.Message.Contains("9.0.1") && m.Message.Contains("changed since"));

        var left = Assert.Single(_journal.Load(_input, console)).Entry.Edits;
        var remaining = Assert.Single(left);
        Assert.Equal("MassTransit", remaining.Package);
    }

    [Fact]
    public async Task Undo_OfAnOlderRunNamesTheNewerRunThatOverwroteIt() {
        await RecordFullRunAsync();
        var second = Apply();
        second.BeginJournal();
        await second.UpdatePropsFileAsync(_props, new Dictionary<string, (string, string?)> { ["MassTransit"] = ("10.0.0", "9.0.0") }, Array.Empty<string>(), default);
        second.RecordJournal(_input, "outdated --apply");
        Assert.Equal(2, _journal.Load(_input, new TestConsole()).Count);

        var console = new TestConsole();
        var exit = await UndoAsync(console, run: 2);

        Assert.Equal(0, exit);
        Assert.Contains("Include=\"MassTransit\" Version=\"10.0.0\"", await File.ReadAllTextAsync(_props));
        Assert.Contains(console.Messages, m => m.Level == "Warning" && m.Message.Contains("MassTransit") && m.Message.Contains("undo that run first"));

        // Popping the newer run first makes the older one fully revertable.
        Assert.Equal(0, await UndoAsync(new TestConsole(), run: 1));
        Assert.Equal(0, await UndoAsync(new TestConsole(), run: 1));
        Assert.Equal(_propsBytes, await File.ReadAllBytesAsync(_props));
        Assert.Empty(_journal.Load(_input, console));
    }

    [Fact]
    public async Task Undo_WithAFilterRevertsOnlyTheFamilyAndLeavesTheRestInTheJournal() {
        await RecordFullRunAsync();

        var exit = await UndoAsync(new TestConsole(), include: new[] { "MassTransit*" });

        Assert.Equal(0, exit);
        var text = await File.ReadAllTextAsync(_props);
        Assert.Contains("Include=\"MassTransit\" Version=\"8.4.1\"", text);
        Assert.Contains("Include=\"MassTransit.RabbitMQ\" Version=\"8.4.1\"", text);
        Assert.Contains("Include=\"Nerdbank.GitVersioning\" Version=\"3.7.0\"", text);
        Assert.Contains("<!--", text);
        var left = Assert.Single(_journal.Load(_input, new TestConsole())).Entry.Edits;
        Assert.Equal(5, left.Count);
        Assert.DoesNotContain(left, e => e.Package.StartsWith("MassTransit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_RestoresACommentedOutOrphanAsTheOriginalElement() {
        await RecordFullRunAsync();
        Assert.Contains("<!-- <PackageVersion Include=\"Polly\" Version=\"8.4.1\" /> -->", await File.ReadAllTextAsync(_props));

        Assert.Equal(0, await UndoAsync(new TestConsole(), include: new[] { "Polly" }));

        var text = await File.ReadAllTextAsync(_props);
        Assert.Contains("    <PackageVersion Include=\"Polly\" Version=\"8.4.1\" />\r\n", text);
        Assert.DoesNotContain("<!--", text);
    }

    [Fact]
    public async Task Undo_RevertsOnlyTheDownloadEntryTheRunRewrote() {
        await RecordFullRunAsync();
        Assert.Contains("Version=\"[4.12.0];[4.8.0]\"", await File.ReadAllTextAsync(_proj));

        Assert.Equal(0, await UndoAsync(new TestConsole(), include: new[] { "Microsoft.CodeAnalysis" }));

        Assert.Contains("Version=\"[4.10.0];[4.8.0]\"", await File.ReadAllTextAsync(_proj));
    }

    [Fact]
    public async Task Undo_ListPrintsRunsNewestFirstWithoutWriting() {
        await RecordFullRunAsync();
        var second = Apply();
        second.BeginJournal();
        await second.UpdatePropsFileAsync(_props, new Dictionary<string, (string, string?)> { ["MassTransit"] = ("10.0.0", "9.0.0") }, Array.Empty<string>(), default);
        second.RecordJournal(_input, "tfm --update-packages");
        var before = await File.ReadAllTextAsync(_props);

        var console = new TestConsole();
        Assert.Equal(0, await UndoAsync(console, list: true));

        var lines = console.Messages.Where(m => m.Level == "Line").Select(m => m.Message).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("tfm --update-packages", lines[0]);
        Assert.Contains("1 edit(s) in 1 file(s)", lines[0]);
        Assert.Contains("outdated --apply", lines[1]);
        Assert.Contains("7 edit(s) in 2 file(s)", lines[1]);
        Assert.Equal(before, await File.ReadAllTextAsync(_props));
    }

    [Fact]
    public async Task Undo_WithoutARecordedRunIsNotAnError() {
        var console = new TestConsole();

        Assert.Equal(0, await UndoAsync(console));
        Assert.Contains(console.Messages, m => m.Level == "Line" && m.Message.StartsWith("No recorded run for", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_RunOutOfRangeFails() {
        await RecordFullRunAsync();

        Assert.Equal(1, await UndoAsync(new TestConsole(), run: 3));
    }

    [Fact]
    public async Task Undo_DecliningTheConfirmationWritesNothing() {
        await RecordFullRunAsync();
        var before = await File.ReadAllTextAsync(_props);
        var console = new TestConsole();
        console.ConfirmAnswers.Enqueue(false);

        Assert.Equal(0, await UndoAsync(console, yes: false));

        Assert.Equal(before, await File.ReadAllTextAsync(_props));
        Assert.Equal(7, Assert.Single(_journal.Load(_input, console)).Entry.Edits.Count);
        Assert.Contains(console.Messages, m => m.Message == "Nothing written.");
    }

    [Fact]
    public async Task Undo_WithoutATerminalNeedsYes() {
        await RecordFullRunAsync();
        var console = new TestConsole { CanPrompt = false };

        Assert.Equal(1, await UndoAsync(console, yes: false));
        Assert.Equal(1, await UndoAsync(console, interactive: true));
        Assert.Contains("Include=\"MassTransit\" Version=\"9.0.0\"", await File.ReadAllTextAsync(_props));
    }

    [Fact]
    public async Task Undo_InteractiveRevertsWhatWasPickedAndPrintsTheRepeatLine() {
        await RecordFullRunAsync();
        // Lock one package by changing it by hand: it must show up in the picker but not be selectable.
        var edited = (await File.ReadAllTextAsync(_proj)).Replace("VersionOverride=\"4.2.0\"", "VersionOverride=\"4.3.0\"");
        await File.WriteAllTextAsync(_proj, edited);

        var console = new TestConsole();
        // Everything starts checked; leave only the MassTransit family: n, then toggle the family header.
        foreach (var key in new[] { PickerKey.SelectNone, PickerKey.Home, PickerKey.Toggle, PickerKey.Confirm }) console.PickerKeys.Enqueue(key);

        Assert.Equal(0, await UndoAsync(console, interactive: true, yes: false));

        var model = Assert.Single(console.PickerModels);
        Assert.Equal(PickerMode.Revert, model.Mode);
        var serilog = model.Groups.SelectMany(g => g.Rows).Single(r => r.Id == "Serilog");
        Assert.True(serilog.Locked);
        Assert.False(serilog.Preselected);
        Assert.Contains("changed since", serilog.Note);
        Assert.Equal("MassTransit.*", model.Groups[0].Name);

        var text = await File.ReadAllTextAsync(_props);
        Assert.Contains("Include=\"MassTransit\" Version=\"8.4.1\"", text);
        Assert.Contains("Include=\"MassTransit.RabbitMQ\" Version=\"8.4.1\"", text);
        Assert.Contains("Include=\"Nerdbank.GitVersioning\" Version=\"3.7.0\"", text);
        Assert.Contains("VersionOverride=\"4.3.0\"", await File.ReadAllTextAsync(_proj));

        var repeat = console.Messages.Single(m => m.Level == "Line" && m.Message.StartsWith("  bld outdated undo ", StringComparison.Ordinal)).Message;
        Assert.Contains("-p \"MassTransit;MassTransit.RabbitMQ\" --yes", repeat);
        Assert.Equal(5, Assert.Single(_journal.Load(_input, console)).Entry.Edits.Count);
    }

    [Fact]
    public async Task Undo_InteractiveCancelWritesNothing() {
        await RecordFullRunAsync();
        var before = await File.ReadAllTextAsync(_props);
        var console = new TestConsole();
        console.PickerKeys.Enqueue(PickerKey.Cancel);

        Assert.Equal(0, await UndoAsync(console, interactive: true, yes: false));

        Assert.Equal(before, await File.ReadAllTextAsync(_props));
        Assert.Equal(7, Assert.Single(_journal.Load(_input, console)).Entry.Edits.Count);
    }

    [Fact]
    public void Journal_KeepsOnlyTheNewestRunsAndSkipsUnreadableFiles() {
        var stamp = new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
        for (var i = 0; i <= UpdateJournal.Keep; i++) {
            _journal.Write(new JournalEntry(_input, stamp.AddMinutes(i), "test", "outdated --apply",
                new List<JournalEdit> { new(_props, $"P{i}", EditKind.PackageVersion, "1.0.0", "2.0.0") }));
        }
        File.WriteAllText(Path.Combine(_journal.DirectoryFor(_input), "99999999-000000Z.json"), "{ not json");

        var console = new TestConsole();
        var runs = _journal.Load(_input, console);

        Assert.Equal(UpdateJournal.Keep, runs.Count);
        Assert.Equal($"P{UpdateJournal.Keep}", runs[0].Entry.Edits[0].Package); // newest first
        Assert.DoesNotContain(runs, r => r.Entry.Edits[0].Package == "P0");   // the oldest was pruned
        Assert.Contains(console.Messages, m => m.Level == "Warning" && m.Message.Contains("unreadable"));
    }

    [Fact]
    public void Journal_KeyIsTheSameForDifferentSpellingsOfTheInput() {
        // Casing folds only where the file system does; on Linux two casings are two inputs.
        Assert.Equal(UpdateJournal.CaseInsensitiveFileSystem, UpdateJournal.Key(_input) == UpdateJournal.Key(_input.ToUpperInvariant()));
        Assert.Equal(UpdateJournal.Key(_input), UpdateJournal.Key(Path.Combine(_dir, "src", "..", "App.slnx")));
        Assert.Equal(UpdateJournal.Key(_dir), UpdateJournal.Key(_dir + Path.DirectorySeparatorChar));
    }
}
