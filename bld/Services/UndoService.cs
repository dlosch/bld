using bld.Infrastructure;
using NuGet.Versioning;
using Spectre.Console;
using System.Xml.Linq;

namespace bld.Services;

/// <summary>
/// <c>bld outdated undo</c>: takes back what a recorded run wrote, edit by edit. An edit is only
/// reverted when the file still holds the value the run put there; anything changed since is
/// skipped and named, never overwritten.
/// </summary>
internal sealed class UndoService {
    private readonly IConsoleOutput _console;
    private readonly UpdateJournal _journal;

    public UndoService(IConsoleOutput console, UpdateJournal journal) {
        _console = console;
        _journal = journal;
    }

    /// <summary>What the file holds today for one journal edit, and whether the edit can be taken back.</summary>
    internal sealed record Inspection(JournalEdit Edit, bool Revertable, string? Actual, string? Reason);

    public async Task<int> RunAsync(string root, bool list, int run, IReadOnlyList<string> include, IReadOnlyList<string> exclude, bool interactive, bool yes, bool verifyRestore, CancellationToken cancellationToken) {
        _console.WriteRule("[bold blue]bld outdated undo (BETA)[/]");

        var runs = _journal.Load(root, _console);
        if (runs.Count == 0) {
            _console.WriteLine($"No recorded run for {root}.");
            return 0;
        }

        if (list) {
            for (var i = 0; i < runs.Count; i++) {
                var entry = runs[i].Entry;
                _console.WriteLine($"{i + 1,3}  {Local(entry.WrittenAt)}  {entry.Command}  {Describe(entry)}");
            }
            return 0;
        }

        if (run < 1 || run > runs.Count) {
            _console.WriteError($"--run {run}: {runs.Count} run(s) recorded for {root}; see --list.");
            return 1;
        }
        var chosenRun = runs[run - 1];
        var selectedIds = OutdatedService.SelectByFilter(chosenRun.Entry.Edits.Select(e => e.Package), include, exclude);
        var edits = chosenRun.Entry.Edits.Where(e => selectedIds.Contains(e.Package)).ToList();
        _console.WriteLine($"Run {run}: {Local(chosenRun.Entry.WrittenAt)} {chosenRun.Entry.Command}, {Describe(chosenRun.Entry)}");
        if (edits.Count == 0) {
            _console.WriteWarning("No edit in the run matched the filter; nothing to revert.");
            return 0;
        }

        // Newer runs come first in the list, so everything before the chosen one may have overwritten it.
        var newer = runs.Take(run - 1).Select(r => r.Entry).ToList();
        var inspections = Inspect(edits, newer);
        ReportTable(root, inspections);
        foreach (var skipped in inspections.Where(i => !i.Revertable)) {
            var where = $"{skipped.Edit.Package} in {Display(root, skipped.Edit.File)}";
            _console.WriteWarning(skipped.Actual is null
                ? $"Skipped: {where} has no entry to revert ({skipped.Reason})."
                : $"Skipped: {where} is at {skipped.Actual}, expected {skipped.Edit.To} ({skipped.Reason}).");
        }

        var revertable = inspections.Where(i => i.Revertable).Select(i => i.Edit).ToList();
        if (revertable.Count == 0) {
            _console.WriteLine("Nothing to revert.");
            return 0;
        }

        List<JournalEdit> chosen;
        if (interactive) {
            if (!_console.CanPrompt) {
                _console.WriteError("--interactive needs an interactive terminal. Use -p/--exclude with --yes instead.");
                return 1;
            }
            var model = BuildPickerModel(inspections);
            var outcome = _console.RunPicker(model, PickerTitle(chosenRun.Entry, inspections));
            if (outcome.Cancelled) {
                _console.WriteLine("Nothing written.");
                return 0;
            }
            var picked = new HashSet<string>(outcome.Selected.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
            chosen = revertable.Where(e => picked.Contains(e.Package)).ToList();
            if (chosen.Count == 0) {
                _console.WriteLine("Nothing selected; nothing written.");
                return 0;
            }
        }
        else {
            if (!yes) {
                if (!_console.CanPrompt) {
                    _console.WriteError("undo needs --yes without an interactive terminal.");
                    return 1;
                }
                var files = revertable.Select(e => e.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (!_console.Confirm($"Revert {revertable.Count} change(s) in {files} file(s)?")) {
                    _console.WriteLine("Nothing written.");
                    return 0;
                }
            }
            chosen = revertable;
        }

        var (reverted, failed) = await RevertAsync(root, chosen, cancellationToken);

        // The journal keeps what was not taken back, so the next undo pops the rest.
        var remaining = chosenRun.Entry.Edits.Where(e => !reverted.Contains(e)).ToList();
        try {
            if (remaining.Count == 0) UpdateJournal.Delete(chosenRun.Path);
            else UpdateJournal.Rewrite(chosenRun.Path, chosenRun.Entry with { Edits = remaining });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _console.WriteWarning($"Could not update the undo journal {chosenRun.Path}: {ex.FormatMessage()}");
        }
        if (reverted.Count > 0 && remaining.Count > 0) {
            _console.WriteLine($"{remaining.Count} edit(s) left in the run; run `bld outdated undo` again to revert them.");
        }

        if (interactive && reverted.Count > 0) {
            _console.WriteLine("To repeat without prompts:");
            _console.WriteLine("  " + BuildRepeatCommand(root, chosen, revertable));
        }

        if (verifyRestore && reverted.Count > 0) {
            _console.WriteInfo("\nVerifying the revert with dotnet restore...");
            var restoreErrors = await OutdatedService.RunRestoreAsync(_console, root, cancellationToken);
            if (restoreErrors.Count == 0) {
                _console.WriteLine("Restore succeeded after revert.");
            }
            else {
                foreach (var line in restoreErrors) _console.WriteError(line);
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// Loads every file once and checks each edit against what it holds. A skipped edit names the
    /// newer run that wrote the value it found, when there is one: that is the run to undo first.
    /// </summary>
    internal static List<Inspection> Inspect(IReadOnlyList<JournalEdit> edits, IReadOnlyList<JournalEntry> newerRuns) {
        var result = new List<Inspection>();
        foreach (var file in edits.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase)) {
            XDocument doc;
            try {
                if (!File.Exists(file.Key)) {
                    result.AddRange(file.Select(e => new Inspection(e, false, null, "file not found")));
                    continue;
                }
                doc = XDocument.Load(file.Key, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException) {
                result.AddRange(file.Select(e => new Inspection(e, false, null, $"could not read the file: {ex.FormatMessage()}")));
                continue;
            }

            foreach (var edit in file) {
                var slots = Slots(doc, edit);
                if (slots.Any(s => s.Get() == edit.To)) {
                    result.Add(new Inspection(edit, true, edit.To, null));
                    continue;
                }
                var actual = slots.Count > 0 ? slots[0].Get() : null;
                var blame = actual is null
                    ? null
                    : newerRuns.FirstOrDefault(r => r.Edits.Any(e =>
                        e.File.Equals(edit.File, StringComparison.OrdinalIgnoreCase)
                        && e.Package.Equals(edit.Package, StringComparison.OrdinalIgnoreCase)
                        && e.To == actual));
                var reason = blame is null ? "changed since" : $"run {Local(blame.WrittenAt)} wrote it; undo that run first";
                result.Add(new Inspection(edit, false, actual, reason));
            }
        }
        return result;
    }

    private async Task<(HashSet<JournalEdit> Reverted, bool Failed)> RevertAsync(string root, IReadOnlyList<JournalEdit> chosen, CancellationToken cancellationToken) {
        var reverted = new HashSet<JournalEdit>();
        var failed = false;
        foreach (var file in chosen.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase)) {
            var done = new List<JournalEdit>();
            try {
                await XmlProjectFile.EditAsync(file.Key, doc => {
                    foreach (var edit in file) {
                        var hit = false;
                        foreach (var slot in Slots(doc, edit)) {
                            if (slot.Get() != edit.To) continue;
                            slot.Set(edit.From);
                            hit = true;
                        }
                        if (hit) done.Add(edit);
                    }
                    return done.Count > 0;
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                _console.WriteError($"Failed to revert {file.Key}: {ex.FormatMessage()}", ex);
                failed = true;
                continue;
            }
            foreach (var edit in done) reverted.Add(edit);
            var name = Display(root, file.Key);
            if (done.Count == 0) _console.WriteWarning($"Nothing reverted in {name}: the entries changed while the command was running.");
            else if (done.Count == 1) _console.WriteLine($"Reverted {done[0].Package} to {BackTo(done[0])} in {name}");
            else _console.WriteLine($"Reverted {done.Count} package(s) in {name}");
        }
        return (reverted, failed);
    }

    /// <summary>A place in the document an edit wrote to: read the value it holds, or put another one there.</summary>
    private sealed record Slot(Func<string?> Get, Action<string> Set);

    /// <summary>
    /// The slots an edit may have written, mirroring what the apply helpers touch. Conditioned items
    /// are never written by apply, so they are not candidates here either.
    /// </summary>
    private static List<Slot> Slots(XDocument doc, JournalEdit edit) {
        var slots = new List<Slot>();
        switch (edit.Kind) {
            case EditKind.PackageVersion:
                foreach (var element in doc.ElementsNamed("PackageVersion").Concat(doc.ElementsNamed("GlobalPackageReference")).Where(e => IsPackage(e, edit.Package))) {
                    AddVersionSlot(slots, element, "Version");
                }
                break;
            case EditKind.PackageReference:
            case EditKind.VersionOverride:
                foreach (var element in doc.ElementsNamed("PackageReference").Where(e => IsPackage(e, edit.Package) && !e.IsConditioned())) {
                    AddVersionSlot(slots, element, edit.Kind == EditKind.VersionOverride ? "VersionOverride" : "Version");
                }
                break;
            case EditKind.PackageDownload:
                foreach (var element in doc.ElementsNamed("PackageDownload").Where(e => IsPackage(e, edit.Package) && !e.IsConditioned())) {
                    var attr = element.Attribute("Version");
                    var child = element.ChildNamed("Version");
                    if (attr is null && child is null) continue;
                    // The slot is the one list entry the run rewrote; the other entries stay as they are.
                    slots.Add(new Slot(
                        () => {
                            var parts = Parts(attr?.Value ?? child?.Value);
                            return parts.FirstOrDefault(p => p == edit.To) ?? attr?.Value ?? child?.Value;
                        },
                        value => {
                            var parts = Parts(attr?.Value ?? child?.Value);
                            var index = parts.FindIndex(p => p == edit.To);
                            if (index < 0) return;
                            parts[index] = value;
                            var joined = string.Join(";", parts);
                            if (attr is { }) attr.Value = joined;
                            else child!.Value = joined;
                        }));
                }
                break;
            case EditKind.OrphanComment:
                var wanted = edit.From.Replace("--", "- -").Trim();
                foreach (var comment in doc.DescendantNodes().OfType<XComment>().Where(c => c.Value.Trim() == wanted).ToList()) {
                    slots.Add(new Slot(() => null, _ => RestoreElement(comment, edit.From)));
                }
                break;
        }
        return slots;
    }

    private static bool IsPackage(XElement element, string package) =>
        string.Equals(element.Attribute("Include")?.Value, package, StringComparison.OrdinalIgnoreCase);

    private static void AddVersionSlot(List<Slot> slots, XElement element, string name) {
        var attr = element.Attribute(name);
        var child = element.ChildNamed(name);
        if (attr is null && child is null) return;
        slots.Add(new Slot(
            () => attr?.Value ?? child?.Value,
            value => {
                if (attr is { }) attr.Value = value;
                else child!.Value = value;
            }));
    }

    private static List<string> Parts(string? value) =>
        (value ?? string.Empty).Split(';').Select(p => p.Trim()).ToList();

    /// <summary>Puts the commented-out element back where the comment sits.</summary>
    private static void RestoreElement(XComment comment, string serialized) {
        var element = XElement.Parse(serialized, LoadOptions.PreserveWhitespace);
        // Serialising the element alone wrote its default namespace onto it; back inside the parent
        // that declaration is redundant and would be written out a second time.
        if (comment.Parent is { } parent) {
            element.Attributes()
                .Where(a => a.IsNamespaceDeclaration && a.Name.LocalName == "xmlns" && a.Value == parent.GetDefaultNamespace().NamespaceName)
                .Remove();
        }
        comment.ReplaceWith(element);
    }

    /// <summary>One row per package, grouped by prefix like the update picker; rows nothing can be done for are locked.</summary>
    internal static PickerModel BuildPickerModel(IReadOnlyList<Inspection> inspections) {
        var rows = new Dictionary<string, PickerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in inspections.GroupBy(i => i.Edit.Package, StringComparer.OrdinalIgnoreCase)) {
            var all = package.ToList();
            var ok = all.Where(i => i.Revertable).ToList();
            var files = all.Select(i => i.Edit.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var sample = (ok.Count > 0 ? ok : all)[0].Edit;

            var back = ParseVersion(sample.Kind == EditKind.OrphanComment ? OrphanVersion(sample.From) : sample.From) ?? new NuGetVersion(0, 0, 0);
            var now = ParseVersion(sample.To) ?? back;
            string? nowLabel = sample.Kind == EditKind.OrphanComment ? "(commented out)" : null;
            var bump = PackageGrouper.Classify(now, back);

            string? note;
            if (ok.Count == 0) note = all[0].Reason;
            else if (ok.Count < all.Count) note = $"({ok.Count} of {all.Count} files; rest changed since)";
            else note = files > 1 ? $"({files} files)" : null;
            if (nowLabel is not null) note = note is null ? "orphan" : $"orphan {note}";

            rows[package.Key] = new PickerRow(package.Key, now, new[] { new PickerTarget(back, bump) }, 0, Preselected: ok.Count > 0, Note: note, Locked: ok.Count == 0, NowLabel: nowLabel);
        }

        var groups = PackageGrouper.GroupByPrefix(rows.Keys)
            .Select(g => new PickerGroup(g.Name, g.Ids.Select(id => rows[id]).ToList()))
            .ToList();
        return new PickerModel(groups, PickerMode.Revert);
    }

    internal static string PickerTitle(JournalEntry entry, IReadOnlyList<Inspection> inspections) {
        var files = inspections.Select(i => i.Edit.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var skipped = inspections.GroupBy(i => i.Edit.Package, StringComparer.OrdinalIgnoreCase).Count(g => g.All(i => !i.Revertable));
        var tail = skipped > 0 ? $"; {skipped} skipped, changed since" : "";
        return $"Revert changes from {Markup.Escape(Local(entry.WrittenAt))} {Markup.Escape(entry.Command)} ({inspections.Count} edits in {files} files{tail})";
    }

    internal static string BuildRepeatCommand(string root, IReadOnlyList<JournalEdit> chosen, IReadOnlyList<JournalEdit> revertable) {
        var parts = new List<string> { "bld", "outdated", "undo", OutdatedService.QuoteArgument(root) };
        var ids = chosen.Select(e => e.Package).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        var allIds = revertable.Select(e => e.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (ids.Count < allIds) {
            parts.Add("-p");
            parts.Add(OutdatedService.QuoteArgument(string.Join(";", ids)));
        }
        parts.Add("--yes");
        return string.Join(" ", parts);
    }

    private void ReportTable(string root, IReadOnlyList<Inspection> inspections) {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Package");
        table.AddColumn("File");
        table.AddColumn("Now");
        table.AddColumn("Back to");
        table.AddColumn("Kind");
        foreach (var i in inspections.OrderBy(i => i.Edit.Package, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Edit.File, StringComparer.OrdinalIgnoreCase)) {
            var now = i.Edit.Kind == EditKind.OrphanComment ? "(commented out)" : i.Actual ?? "(missing)";
            table.AddRow(
                Markup.Escape(i.Edit.Package),
                Markup.Escape(Display(root, i.Edit.File)),
                i.Revertable ? Markup.Escape(now) : $"[grey]{Markup.Escape(now)}[/]",
                i.Revertable ? Markup.Escape(BackTo(i.Edit)) : $"[grey]{Markup.Escape(i.Reason ?? "skipped")}[/]",
                Markup.Escape(Kind(i.Edit.Kind)));
        }
        _console.WriteTable(table);
    }

    private static string BackTo(JournalEdit edit) =>
        edit.Kind == EditKind.OrphanComment ? $"restore entry ({OrphanVersion(edit.From) ?? "?"})" : edit.From;

    private static string Kind(EditKind kind) => kind switch {
        EditKind.PackageVersion => "PackageVersion",
        EditKind.PackageReference => "PackageReference",
        EditKind.VersionOverride => "VersionOverride",
        EditKind.PackageDownload => "PackageDownload",
        _ => "orphan"
    };

    /// <summary>The Version attribute of a serialized PackageVersion element, or null if unreadable.</summary>
    private static string? OrphanVersion(string serialized) {
        try {
            var element = XElement.Parse(serialized);
            return element.Attribute("Version")?.Value ?? element.ChildNamed("Version")?.Value;
        }
        catch (System.Xml.XmlException) {
            return null;
        }
    }

    /// <summary>A literal version, or the lower bound of an exact range like the one PackageDownload uses.</summary>
    private static NuGetVersion? ParseVersion(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (NuGetVersion.TryParse(text, out var version)) return version;
        return VersionRange.TryParse(text, out var range) ? range.MinVersion : null;
    }

    private static string Describe(JournalEntry entry) {
        var files = entry.Edits.Select(e => e.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"{entry.Edits.Count} edit(s) in {files} file(s)";
    }

    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>The file relative to the input's directory when it lies below it; the full path otherwise.</summary>
    private static string Display(string root, string file) {
        var baseDirectory = Directory.Exists(root) ? root : Path.GetDirectoryName(root);
        if (string.IsNullOrEmpty(baseDirectory)) return file;
        var relative = Path.GetRelativePath(baseDirectory, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? file : relative;
    }
}
