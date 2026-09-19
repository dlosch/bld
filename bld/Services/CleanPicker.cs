using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace bld.Services;

/// <summary>The keys the clean picker understands; see <see cref="PickerKey"/> for why they are not ConsoleKeys.</summary>
internal enum CleanPickerKey {
    None,
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End,
    Toggle,
    SelectAll,
    SelectNone,
    /// <summary>Flip the bin directories in scope: every project on the top line, one project elsewhere.</summary>
    Bin,
    Obj,
    Publish,
    Package,
    TestResults,
    Confirm,
    Cancel,
}

/// <summary>
/// One directory the run would delete, with what it is and what it weighs. <see cref="Path"/> is
/// the fully qualified path that gets deleted and the only path the picker ever shows: a row that
/// displayed something shorter than what it stands for would make the checkbox a guess.
/// </summary>
internal sealed record CleanPickerRow(string Path, CleanCategory Category, long Bytes, int Files);

/// <summary>The directories of one project (or of the projects sharing them).</summary>
internal sealed record CleanPickerGroup(string Name, IReadOnlyList<CleanPickerRow> Rows);

/// <param name="Preselected">The categories that start out checked, from the command's flags.</param>
internal sealed record CleanPickerModel(IReadOnlyList<CleanPickerGroup> Groups, IReadOnlySet<CleanCategory> Preselected) {

    /// <summary>
    /// One group per owning project, rows sorted by path, sizes measured now so the picker can show
    /// what a choice is worth. One row per path, always the fully qualified one.
    /// </summary>
    internal static CleanPickerModel From(MarkDeleteResult result, CleaningOptions options) {
        var rows = new Dictionary<string, List<CleanPickerRow>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(DirExt.PathComparer);
        void Add(string path, IReadOnlyList<Dir> references, CleanCategory category, long bytes, int files) {
            // A row is a checkbox on a path, and the caller maps the checked ones back by path. A
            // relative path would resolve against whatever the process' current directory happens to
            // be, and a second row on the same path would give one path two contradicting answers.
            if (!System.IO.Path.IsPathFullyQualified(path)) {
                throw new InvalidOperationException($"Refusing to offer '{path}' for deletion: the marked path is not fully qualified.");
            }
            path = CleanSelection.Canonical(path);
            if (!seen.Add(path)) return;

            var projects = references
                .SelectMany(r => r.AbsProjPath)
                .Select(kv => kv.Value ?? System.IO.Path.GetFileNameWithoutExtension(kv.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var owner = projects.Count switch { 0 => "(unknown project)", 1 => projects[0], _ => string.Join(" + ", projects) };
            if (!rows.TryGetValue(owner, out var list)) rows[owner] = list = new List<CleanPickerRow>();
            list.Add(new CleanPickerRow(path, category, bytes, files));
        }
        foreach (var entry in result.Directories) {
            var (bytes, files) = Measure(entry.Directory);
            Add(entry.Directory.FullName, entry.References, entry.Category, bytes, files);
        }
        // A package file is its own row: only that file goes, not the directory around it.
        foreach (var entry in result.Files) {
            Add(entry.File.FullName, entry.References, entry.Category, entry.File.Exists ? entry.File.Length : 0, 1);
        }

        var groups = rows
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new CleanPickerGroup(kv.Key, kv.Value.OrderBy(r => r.Path, DirExt.PathComparer).ToList()))
            .ToList();

        var preselected = new HashSet<CleanCategory> { CleanCategory.Bin };
        if (options.CleanObjDirectory) preselected.Add(CleanCategory.Obj);
        if (options.CleanPublishDirectory) {
            preselected.Add(CleanCategory.Publish);
            preselected.Add(CleanCategory.Package);
        }
        if (options.CleanTestResults) preselected.Add(CleanCategory.TestResults);
        return new CleanPickerModel(groups, preselected);
    }

    private static (long Bytes, int Files) Measure(DirectoryInfo directory) {
        try {
            var files = directory.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false, MatchType = MatchType.Simple }).ToList();
            return (files.Sum(f => f.Length), files.Count);
        }
        catch (Exception) {
            return (0, 0);
        }
    }

}

/// <summary>
/// Narrows what the run marked down to what the user checked. The picker may only ever take paths
/// away: everything it hands back has to be a path this run marked, compared the way the host
/// filesystem compares paths. Anything else means the picker and the result drifted apart, and a
/// deletion run must not go ahead on a set it cannot account for - it throws instead, which aborts
/// the run before any processor sees it.
/// </summary>
internal static class CleanSelection {
    /// <summary>
    /// The one spelling of a path everything here compares on. A marked directory reaches this point
    /// as its <see cref="DirectoryInfo.FullName"/>, which keeps whatever trailing separator the
    /// marked path had, so the same directory can arrive spelled two ways; the path is already known
    /// to be fully qualified, so this only normalises it, it never consults the current directory.
    /// </summary>
    internal static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// <see cref="Canonical"/> for a path that has to be absolute already. Going through
    /// <see cref="Path.GetFullPath(string)"/> would root a relative one against the current
    /// directory and hand back a real path to delete; here that has to fail instead.
    /// </summary>
    private static string Key(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            ? Canonical(path)
            : throw new InvalidOperationException($"'{path}' is not a fully qualified path. Nothing was deleted.");

    internal static MarkDeleteResult Apply(MarkDeleteResult marked, IReadOnlyList<string> selected) {
        var chosen = new HashSet<string>(selected.Select(Key), DirExt.PathComparer);

        var kept = new MarkDeleteResult(marked.Directories.Where(d => chosen.Contains(Key(d.Directory.FullName))).ToList()) {
            Files = marked.Files.Where(f => chosen.Contains(Key(f.File.FullName))).ToList()
        };

        // Every checked path has to come back out as an entry of the marked result, so a count that
        // matches means the kept entries and the checked paths are the same set. Two entries can
        // canonicalise to one path - the same directory marked with and without a trailing
        // separator - and that stays one checkbox over one directory.
        var keptPaths = new HashSet<string>(
            kept.Directories.Select(d => Key(d.Directory.FullName)).Concat(kept.Files.Select(f => Key(f.File.FullName))),
            DirExt.PathComparer);
        if (keptPaths.Count != chosen.Count) {
            var unknown = chosen.Where(p => !keptPaths.Contains(p)).OrderBy(p => p, DirExt.PathComparer).ToList();
            throw new InvalidOperationException(
                $"The picker returned {unknown.Count} path(s) this run never marked for deletion: {string.Join(", ", unknown.Take(5))}. Nothing was deleted.");
        }
        return kept;
    }
}

/// <summary>The directories the user kept; the caller filters its result down to them.</summary>
internal sealed record CleanPickerOutcome(bool Cancelled, IReadOnlyList<string> Selected) {
    internal static CleanPickerOutcome CancelledOutcome { get; } = new(true, Array.Empty<string>());
}

/// <summary>How a category stands within a scope: not there, all off, some on, all on.</summary>
internal enum CategoryState {
    Absent,
    Off,
    Mixed,
    On,
}

/// <summary>
/// The clean picker as data, like <see cref="PickerState"/>: a top line for every project, a line
/// per project, a line per directory. A category key flips that category in the scope under the
/// cursor - every project on the top line, one project on its line or on one of its rows - so the
/// global toggle and the per-project override are the same key in a different place. Space flips
/// one directory, or everything in the scope on a header.
/// </summary>
internal sealed class CleanPickerState {
    internal sealed class Line {
        /// <summary>Set on the two kinds of header; null on a directory row.</summary>
        internal string? Name { get; init; }
        /// <summary>The top line, whose scope is every project.</summary>
        internal bool IsAll { get; init; }
        internal CleanPickerRow? Row { get; init; }
        /// <summary>Index of the project line this row belongs to; -1 on headers.</summary>
        internal int GroupIndex { get; init; } = -1;
        internal bool Selected { get; set; }
        internal bool IsHeader => Row is null;
    }

    private readonly List<Line> _lines = new();

    internal CleanPickerState(CleanPickerModel model) {
        _lines.Add(new Line { IsAll = true, Name = "All projects" });
        foreach (var group in model.Groups) {
            _lines.Add(new Line { Name = group.Name });
            var groupIndex = _lines.Count - 1;
            foreach (var row in group.Rows) {
                _lines.Add(new Line { Row = row, GroupIndex = groupIndex, Selected = model.Preselected.Contains(row.Category) });
            }
        }
        Categories = model.Groups.SelectMany(g => g.Rows).Select(r => r.Category).Distinct().OrderBy(c => c).ToList();
    }

    internal IReadOnlyList<Line> Lines => _lines;
    /// <summary>The categories any row has, in enum order; the columns the headers show.</summary>
    internal IReadOnlyList<CleanCategory> Categories { get; }
    /// <summary>Starts on the top line, so the first category key acts on every project.</summary>
    internal int Cursor { get; private set; }
    internal bool Done { get; private set; }
    internal bool Cancelled { get; private set; }

    internal void Handle(CleanPickerKey key) {
        switch (key) {
            case CleanPickerKey.Up: Move(-1); break;
            case CleanPickerKey.Down: Move(1); break;
            case CleanPickerKey.PageUp: Move(-10); break;
            case CleanPickerKey.PageDown: Move(10); break;
            case CleanPickerKey.Home: Cursor = 0; break;
            case CleanPickerKey.End: Cursor = Math.Max(0, _lines.Count - 1); break;
            case CleanPickerKey.Toggle: Toggle(); break;
            case CleanPickerKey.SelectAll: SetAll(true); break;
            case CleanPickerKey.SelectNone: SetAll(false); break;
            case CleanPickerKey.Bin: ToggleCategory(CleanCategory.Bin); break;
            case CleanPickerKey.Obj: ToggleCategory(CleanCategory.Obj); break;
            case CleanPickerKey.Publish: ToggleCategory(CleanCategory.Publish); break;
            case CleanPickerKey.Package: ToggleCategory(CleanCategory.Package); break;
            case CleanPickerKey.TestResults: ToggleCategory(CleanCategory.TestResults); break;
            case CleanPickerKey.Confirm: Done = true; break;
            case CleanPickerKey.Cancel: Done = Cancelled = true; break;
        }
    }

    private void Move(int delta) {
        if (_lines.Count == 0) return;
        Cursor = Math.Clamp(Cursor + delta, 0, _lines.Count - 1);
    }

    /// <summary>The rows a category key or a header toggle acts on.</summary>
    private IEnumerable<Line> Scope() {
        var current = _lines[Cursor];
        if (current.IsAll) return _lines.Where(l => l.Row is not null);
        return RowsOf(current.Row is null ? Cursor : current.GroupIndex);
    }

    private IEnumerable<Line> RowsOf(int headerIndex) {
        for (var i = headerIndex + 1; i < _lines.Count && _lines[i].GroupIndex == headerIndex; i++) {
            yield return _lines[i];
        }
    }

    private void Toggle() {
        var current = _lines[Cursor];
        var affected = (current.Row is null ? Scope() : new[] { current }).ToList();
        if (affected.Count == 0) return;
        // A header turns its scope off only when everything in it is on; anything else turns it on.
        var target = !affected.All(l => l.Selected);
        foreach (var line in affected) line.Selected = target;
    }

    private void ToggleCategory(CleanCategory category) {
        var affected = Scope().Where(l => l.Row!.Category == category).ToList();
        if (affected.Count == 0) return;
        var target = !affected.All(l => l.Selected);
        foreach (var line in affected) line.Selected = target;
    }

    private void SetAll(bool selected) {
        foreach (var line in _lines) {
            if (line.Row is not null) line.Selected = selected;
        }
    }

    /// <summary>The category's state within a header's scope.</summary>
    internal CategoryState StateOf(int headerIndex, CleanCategory category) {
        var rows = (_lines[headerIndex].IsAll ? _lines.Where(l => l.Row is not null) : RowsOf(headerIndex)).Where(l => l.Row!.Category == category).ToList();
        if (rows.Count == 0) return CategoryState.Absent;
        var on = rows.Count(l => l.Selected);
        return on == 0 ? CategoryState.Off : on == rows.Count ? CategoryState.On : CategoryState.Mixed;
    }

    /// <summary>Tri-state for a header: all, none, or some of its rows selected.</summary>
    internal bool? Selection(int headerIndex) {
        var rows = (_lines[headerIndex].IsAll ? _lines.Where(l => l.Row is not null) : RowsOf(headerIndex)).ToList();
        if (rows.Count == 0 || rows.All(l => !l.Selected)) return false;
        return rows.All(l => l.Selected) ? true : null;
    }

    internal (long Selected, long Total) BytesOf(int headerIndex) {
        var rows = (_lines[headerIndex].IsAll ? _lines.Where(l => l.Row is not null) : RowsOf(headerIndex)).ToList();
        return (rows.Where(l => l.Selected).Sum(l => l.Row!.Bytes), rows.Sum(l => l.Row!.Bytes));
    }

    internal CleanPickerOutcome Result() {
        if (Cancelled) return CleanPickerOutcome.CancelledOutcome;
        return new CleanPickerOutcome(false, _lines.Where(l => l.Selected && l.Row is not null).Select(l => l.Row!.Path).ToList());
    }
}

/// <summary>Draws a <see cref="CleanPickerState"/>; pure, like <see cref="PackagePickerRenderer"/>.</summary>
internal static class CleanPickerRenderer {
    internal const string Instructions =
        "[grey]up/down: move   space: toggle   b/o/p/g/t: bin/obj/publish/package/test results - every project on the top line, one project below it   a/n: all/none   enter: confirm   esc: cancel[/]";

    internal static int PageSize(string title, int height, int width) =>
        PackagePickerRenderer.PageSize(title, Instructions, height, width);

    internal static IRenderable Render(CleanPickerState state, string title, int pageSize, int width) {
        var lines = new List<IRenderable> { new Markup(title), new Markup(Instructions), Text.Empty };
        var nameWidth = NameWidth(state);

        var (first, last) = PackagePickerRenderer.Viewport(state.Cursor, state.Lines.Count, pageSize);
        if (first > 0) lines.Add(new Markup("[grey]  ... more above ...[/]"));
        for (var i = first; i <= last; i++) {
            // A line longer than the terminal would wrap onto a second one, push the frame past the
            // height the page size was computed for, and leave the live region redrawing over itself.
            lines.Add(new Markup(RenderLine(state, i, nameWidth, width)).Overflow(Overflow.Ellipsis));
        }
        if (last < state.Lines.Count - 1) lines.Add(new Markup("[grey]  ... more below ...[/]"));

        return new Rows(lines);
    }

    /// <summary>The name column, wide enough for every header including the indent of project lines.</summary>
    internal static int NameWidth(CleanPickerState state) =>
        state.Lines.Where(l => l.IsHeader).Select(l => l.Name!.Length + (l.IsAll ? 0 : 2)).DefaultIfEmpty(0).Max();

    internal static string RenderLine(CleanPickerState state, int index, int nameWidth, int width = int.MaxValue) {
        var line = state.Lines[index];
        var cursor = index == state.Cursor ? "[blue]>[/] " : "  ";

        if (line.Row is { } row) {
            var box = line.Selected ? "[green][[X]][/]" : "[[ ]]";
            var prefix = $"{cursor}    {box} {Markup.Escape(CleanCategories.Label(row.Category).PadRight(7))}  {Markup.Escape(Size(row.Bytes).PadLeft(9))}  ";
            // Spectre's ellipsis would cut the end, which is the part that tells two bin directories
            // apart. Cut the middle instead, so the line keeps the root and the leaf.
            return prefix + Markup.Escape(Shorten(row.Path, width - Markup.Remove(prefix).Length));
        }

        var selection = state.Selection(index);
        var headerBox = selection switch { true => "[green][[X]][/]", false => "[[ ]]", null => "[yellow][[-]][/]" };
        // Project lines are indented by two; their name column is two narrower so the category
        // columns line up with the top line.
        var indent = line.IsAll ? "" : "  ";
        var padded = line.Name!.PadRight(nameWidth - indent.Length);
        var name = line.IsAll ? $"[bold]{Markup.Escape(padded)}[/]" : Markup.Escape(padded);
        var categories = string.Join("  ", state.Categories.Select(c => Category(c, state.StateOf(index, c))));
        var (selected, total) = state.BytesOf(index);
        return $"{cursor}{indent}{headerBox} {name}  {categories}  [grey]{Markup.Escape($"{Size(selected).PadLeft(9)} of {Size(total).PadLeft(9)}")}[/]";
    }

    private static string Category(CleanCategory category, CategoryState state) {
        var label = Markup.Escape(CleanCategories.Label(category));
        return state switch {
            CategoryState.On => $"{label}[green][[X]][/]",
            CategoryState.Mixed => $"{label}[yellow][[-]][/]",
            CategoryState.Off => $"{label}[[ ]]",
            _ => $"[grey]{label}[[·]][/]",
        };
    }

    /// <summary>
    /// The path a row stands for, cut in the middle when the line cannot hold it: its root and as
    /// much of its tail as fits stay, with "..." where the middle was. A shortened path is still
    /// recognisably the absolute one, so nothing on screen can read as a path relative to somewhere
    /// else. Deletion always uses <see cref="CleanPickerRow.Path"/>, never this.
    /// </summary>
    internal static string Shorten(string path, int max) {
        const string gap = "...";
        if (max <= 0 || path.Length <= max) return path;
        if (max <= gap.Length) return path[^max..];

        var root = System.IO.Path.GetPathRoot(path) ?? string.Empty;
        // No room for root + gap + something of the tail: drop the root and keep the tail.
        if (root.Length + gap.Length >= max) return gap + path[^(max - gap.Length)..];
        return root + gap + path[^(max - root.Length - gap.Length)..];
    }

    /// <summary>At most nine characters up to 9,999.9 MiB, so the size columns stay put.</summary>
    internal static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:N1} MiB" : $"{bytes / 1024d:N0} KiB";

    internal static CleanPickerKey MapKey(ConsoleKeyInfo key) => key.Key switch {
        ConsoleKey.UpArrow or ConsoleKey.K => CleanPickerKey.Up,
        ConsoleKey.DownArrow or ConsoleKey.J => CleanPickerKey.Down,
        ConsoleKey.PageUp => CleanPickerKey.PageUp,
        ConsoleKey.PageDown => CleanPickerKey.PageDown,
        ConsoleKey.Home => CleanPickerKey.Home,
        ConsoleKey.End => CleanPickerKey.End,
        ConsoleKey.Spacebar => CleanPickerKey.Toggle,
        ConsoleKey.A => CleanPickerKey.SelectAll,
        ConsoleKey.N => CleanPickerKey.SelectNone,
        ConsoleKey.B => CleanPickerKey.Bin,
        ConsoleKey.O => CleanPickerKey.Obj,
        ConsoleKey.P => CleanPickerKey.Publish,
        ConsoleKey.G => CleanPickerKey.Package,
        ConsoleKey.T => CleanPickerKey.TestResults,
        ConsoleKey.Enter => CleanPickerKey.Confirm,
        ConsoleKey.Escape or ConsoleKey.Q => CleanPickerKey.Cancel,
        ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control) => CleanPickerKey.Cancel,
        _ => CleanPickerKey.None
    };
}
