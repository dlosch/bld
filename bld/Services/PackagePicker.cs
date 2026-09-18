using Spectre.Console;
using Spectre.Console.Rendering;
using NuGet.Versioning;

namespace bld.Services;

/// <summary>
/// The keys the picker understands, decoupled from <see cref="ConsoleKey"/> so the state machine can
/// be driven from a test without synthesising key events.
/// </summary>
internal enum PickerKey {
    None,
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End,
    /// <summary>Move the row (or every row of a group) to the next lower target.</summary>
    TargetDown,
    /// <summary>Move the row (or every row of a group) to the next higher target.</summary>
    TargetUp,
    Toggle,
    SelectAll,
    SelectNone,
    Confirm,
    Cancel,
}

/// <summary>What the user settled on: the packages they kept, with the target chosen for each.</summary>
internal sealed record PickerOutcome(bool Cancelled, IReadOnlyList<(string Id, NuGetVersion Target)> Selected) {
    internal static PickerOutcome CancelledOutcome { get; } = new(true, Array.Empty<(string, NuGetVersion)>());
}

/// <summary>
/// The picker as data: a flat list of lines with a cursor, a selection and one chosen target per
/// package. Every key is handled here and nowhere else, so the behaviour is testable without a
/// terminal; <see cref="PackagePickerRenderer"/> turns a state into something Spectre can draw.
/// </summary>
internal sealed class PickerState {
    internal sealed class Line {
        /// <summary>Non-null on a group header, null on a package row.</summary>
        internal string? GroupName { get; init; }
        internal PickerRow? Row { get; init; }
        internal int TargetIndex { get; set; }
        internal bool Selected { get; set; }
        /// <summary>Index of the group header this row belongs to, or -1 when ungrouped.</summary>
        internal int GroupIndex { get; init; } = -1;
        /// <summary>On a group header: the bump class the family is capped at. Null without rows.</summary>
        internal BumpKind? GroupBump { get; set; }
        /// <summary>The family cap dropped this row from the selection; a cap that admits it again puts it back.</summary>
        internal bool CappedOut { get; set; }

        internal bool IsGroup => GroupName is not null;
    }

    private readonly List<Line> _lines = new();

    internal PickerState(PickerModel model) {
        foreach (var group in model.Groups) {
            Line? header = null;
            if (group.Name.Length > 0) {
                header = new Line { GroupName = group.Name };
                _lines.Add(header);
            }
            var groupIndex = header is null ? -1 : _lines.Count - 1;
            foreach (var row in group.Rows) {
                _lines.Add(new Line { Row = row, TargetIndex = row.DefaultTarget, Selected = row.Preselected, GroupIndex = groupIndex });
            }
            if (header is not null) header.GroupBump = HighestBump(groupIndex);
        }
        // Landing on a group header would make the first keypress act on a whole family.
        Cursor = _lines.FindIndex(l => !l.IsGroup);
        if (Cursor < 0) Cursor = 0;
    }

    internal IReadOnlyList<Line> Lines => _lines;
    internal int Cursor { get; private set; }
    internal bool Done { get; private set; }
    internal bool Cancelled { get; private set; }

    internal void Handle(PickerKey key) {
        switch (key) {
            case PickerKey.Up: Move(-1); break;
            case PickerKey.Down: Move(1); break;
            case PickerKey.PageUp: Move(-10); break;
            case PickerKey.PageDown: Move(10); break;
            case PickerKey.Home: Cursor = 0; break;
            case PickerKey.End: Cursor = Math.Max(0, _lines.Count - 1); break;
            case PickerKey.TargetUp: ShiftTarget(1); break;
            case PickerKey.TargetDown: ShiftTarget(-1); break;
            case PickerKey.Toggle: Toggle(); break;
            case PickerKey.SelectAll: SetAll(true); break;
            case PickerKey.SelectNone: SetAll(false); break;
            case PickerKey.Confirm: Done = true; break;
            case PickerKey.Cancel: Done = Cancelled = true; break;
        }
    }

    private void Move(int delta) {
        if (_lines.Count == 0) return;
        Cursor = Math.Clamp(Cursor + delta, 0, _lines.Count - 1);
    }

    /// <summary>
    /// Moves the target of the row under the cursor, or the bump class of the whole family when the
    /// cursor sits on a header. A family moves by class, not by list position: its rows offer
    /// different numbers of targets, so stepping each one by one index would leave them at
    /// different classes.
    /// </summary>
    private void ShiftTarget(int delta) {
        if (_lines.Count == 0) return;
        var current = _lines[Cursor];
        if (current.Row is { } row) {
            current.TargetIndex = Math.Clamp(current.TargetIndex + delta, 0, row.Targets.Count - 1);
            // The header follows the highest class in its family, so it never shows a cap a row is above.
            if (current.GroupIndex >= 0) _lines[current.GroupIndex].GroupBump = HighestBump(current.GroupIndex);
            return;
        }

        if (!GroupCanShift(Cursor, delta)) return;
        var choices = GroupBumpChoices(Cursor);
        var bump = choices[choices.IndexOf(current.GroupBump!.Value) + delta];
        current.GroupBump = bump;
        foreach (var line in RowsOf(Cursor)) {
            var targets = line.Row!.Targets;
            if (targets[0].Bump > bump) {
                // Nothing at or below the cap: the row leaves the selection rather than sneaking a
                // bigger step into a "patch only" family. Only rows the cap removed come back when it
                // is raised; one the user unchecked stays unchecked.
                if (line.Selected) {
                    line.Selected = false;
                    line.CappedOut = true;
                }
                continue;
            }
            line.TargetIndex = CapAt(targets, bump);
            if (line.CappedOut) {
                line.Selected = true;
                line.CappedOut = false;
            }
        }
    }

    /// <summary>The highest target not above the class.</summary>
    private static int CapAt(IReadOnlyList<PickerTarget> targets, BumpKind bump) {
        var index = 0;
        for (var i = 0; i < targets.Count; i++) {
            if (targets[i].Bump <= bump) index = i;
        }
        return index;
    }

    private IEnumerable<Line> RowsOf(int headerIndex) {
        for (var i = headerIndex + 1; i < _lines.Count && _lines[i].GroupIndex == headerIndex; i++) {
            yield return _lines[i];
        }
    }

    private BumpKind? HighestBump(int headerIndex) {
        BumpKind? highest = null;
        foreach (var line in RowsOf(headerIndex)) {
            if (line.CappedOut) continue; // parked above the cap, not part of the family's choice
            var bump = line.Row!.Targets[line.TargetIndex].Bump;
            if (highest is null || bump > highest) highest = bump;
        }
        return highest;
    }

    /// <summary>Every bump class some row of the family offers, ascending.</summary>
    internal List<BumpKind> GroupBumpChoices(int headerIndex) =>
        RowsOf(headerIndex).SelectMany(l => l.Row!.Targets.Select(t => t.Bump)).Distinct().OrderBy(b => b).ToList();

    /// <summary>Whether left (-1) or right (+1) on a header has another class to move the family to.</summary>
    internal bool GroupCanShift(int headerIndex, int delta) {
        if (_lines[headerIndex].GroupBump is not { } current) return false;
        var index = GroupBumpChoices(headerIndex).IndexOf(current) + delta;
        return index >= 0 && index < GroupBumpChoices(headerIndex).Count;
    }

    private void Toggle() {
        var affected = LinesUnderCursor().Where(l => l.Row is not null).ToList();
        if (affected.Count == 0) return;
        // A header toggles its family off only when the family is fully on; anything else turns it on.
        var target = !affected.All(l => l.Selected);
        foreach (var line in affected) Select(line, target);
    }

    private void SetAll(bool selected) {
        foreach (var line in _lines) {
            if (line.Row is not null) Select(line, selected);
        }
    }

    // An explicit choice overrides whatever the family cap did to the row.
    private static void Select(Line line, bool selected) {
        line.Selected = selected;
        line.CappedOut = false;
    }

    private IEnumerable<Line> LinesUnderCursor() {
        if (_lines.Count == 0) yield break;
        var current = _lines[Cursor];
        if (!current.IsGroup) {
            yield return current;
            yield break;
        }
        for (var i = Cursor + 1; i < _lines.Count && _lines[i].GroupIndex == Cursor; i++) {
            yield return _lines[i];
        }
    }

    /// <summary>Tri-state for a header: all, none, or some of its rows selected.</summary>
    internal bool? GroupSelection(int headerIndex) {
        var any = false;
        var all = true;
        for (var i = headerIndex + 1; i < _lines.Count && _lines[i].GroupIndex == headerIndex; i++) {
            if (_lines[i].Selected) any = true;
            else all = false;
        }
        if (!any) return false;
        return all ? true : null;
    }

    internal PickerOutcome Result() {
        if (Cancelled) return PickerOutcome.CancelledOutcome;
        var selected = _lines
            .Where(l => l.Selected && l.Row is not null)
            .Select(l => (l.Row!.Id, l.Row.Targets[l.TargetIndex].Version))
            .ToList();
        return new PickerOutcome(false, selected);
    }

    /// <summary>The chosen target per package, selected or not - the repeat command needs both.</summary>
    internal IReadOnlyDictionary<string, PickerTarget> ChosenTargets() {
        var chosen = new Dictionary<string, PickerTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _lines) {
            if (line.Row is null) continue;
            chosen[line.Row.Id] = line.Row.Targets[line.TargetIndex];
        }
        return chosen;
    }
}

/// <summary>
/// Turns a <see cref="PickerState"/> into a renderable. Pure: the same state always produces the
/// same markup, which is what makes the layout assertable in tests.
/// </summary>
internal static class PackagePickerRenderer {
    internal const string Instructions =
        "[grey]up/down: move   space: toggle   left/right: change target   a/n: all/none   enter: confirm   esc: cancel[/]";

    internal static IRenderable Render(PickerState state, string title, int pageSize) {
        var lines = new List<IRenderable> { new Markup(title), new Markup(Instructions), Text.Empty };
        var idWidth = state.Lines.Where(l => l.Row is not null).Select(l => l.Row!.Id.Length).DefaultIfEmpty(0).Max();
        var versionWidth = state.Lines.Where(l => l.Row is not null).Select(l => l.Row!.Current.ToString().Length).DefaultIfEmpty(0).Max();
        // Over every candidate, not just the chosen ones, so moving a row between targets never
        // shifts the columns to its right.
        var targetWidth = state.Lines.Where(l => l.Row is not null).SelectMany(l => l.Row!.Targets).Select(t => t.Version.ToString().Length).DefaultIfEmpty(0).Max();

        var (first, last) = Viewport(state.Cursor, state.Lines.Count, pageSize);
        if (first > 0) lines.Add(new Markup("[grey]  ... more above ...[/]"));
        for (var i = first; i <= last; i++) {
            lines.Add(new Markup(RenderLine(state, i, idWidth, versionWidth, targetWidth)));
        }
        if (last < state.Lines.Count - 1) lines.Add(new Markup("[grey]  ... more below ...[/]"));

        return new Rows(lines);
    }

    /// <summary>The slice of lines to draw, keeping the cursor inside it.</summary>
    internal static (int First, int Last) Viewport(int cursor, int count, int pageSize) {
        if (count <= pageSize) return (0, Math.Max(0, count - 1));
        var first = Math.Clamp(cursor - pageSize / 2, 0, count - pageSize);
        return (first, first + pageSize - 1);
    }

    internal static string RenderLine(PickerState state, int index, int idWidth, int versionWidth, int targetWidth = 0) {
        var line = state.Lines[index];
        var cursor = index == state.Cursor ? "[bold]>[/] " : "  ";

        if (line.IsGroup) {
            var box = state.GroupSelection(index) switch {
                true => "[green][[X]][/]",
                null => "[yellow][[~]][/]",
                _ => "[[ ]]"
            };
            var header = $"{cursor}{box} [bold]{Markup.Escape(line.GroupName!)}[/]";
            // The class toggle only appears where the family has more than one class to choose from.
            if (line.GroupBump is not { } groupBump || state.GroupBumpChoices(index).Count < 2) return header;
            var groupLeft = state.GroupCanShift(index, -1) ? "[blue]<[/]" : " ";
            var groupRight = state.GroupCanShift(index, 1) ? "[blue]>[/]" : " ";
            return $"{header}  {groupLeft} {BumpMarkup(groupBump)} {groupRight}";
        }

        var row = line.Row!;
        var target = row.Targets[line.TargetIndex];
        var indent = line.GroupIndex >= 0 ? "  " : "";
        var rowBox = line.Selected ? "[green][[X]][/]" : "[[ ]]";
        var id = Markup.Escape(row.Id.PadRight(idWidth));
        var current = Markup.Escape(row.Current.ToString().PadRight(versionWidth));
        // The arrows are only drawn where they do something, so a package with a single target does
        // not advertise a choice it does not have.
        var left = line.TargetIndex > 0 ? "[blue]<[/]" : " ";
        var right = line.TargetIndex < row.Targets.Count - 1 ? "[blue]>[/]" : " ";
        var position = row.Targets.Count > 1 ? $" [grey]{Markup.Escape($"({line.TargetIndex + 1}/{row.Targets.Count})")}[/]" : "";

        var targetVersion = Markup.Escape(target.Version.ToString().PadRight(targetWidth));
        return $"{cursor}{indent}{rowBox} {id}  {current} -> {left} {targetVersion} {right}  {BumpMarkup(target.Bump)}{position}";
    }

    internal static string BumpMarkup(BumpKind bump) => bump switch {
        BumpKind.Major => "[red bold]MAJOR[/]",
        BumpKind.Minor => "[yellow]minor[/]",
        _ => "[green]patch[/]"
    };

    internal static PickerKey MapKey(ConsoleKeyInfo key) => key.Key switch {
        ConsoleKey.UpArrow or ConsoleKey.K => PickerKey.Up,
        ConsoleKey.DownArrow or ConsoleKey.J => PickerKey.Down,
        ConsoleKey.LeftArrow or ConsoleKey.H => PickerKey.TargetDown,
        ConsoleKey.RightArrow or ConsoleKey.L => PickerKey.TargetUp,
        ConsoleKey.PageUp => PickerKey.PageUp,
        ConsoleKey.PageDown => PickerKey.PageDown,
        ConsoleKey.Home => PickerKey.Home,
        ConsoleKey.End => PickerKey.End,
        ConsoleKey.Spacebar => PickerKey.Toggle,
        ConsoleKey.A => PickerKey.SelectAll,
        ConsoleKey.N => PickerKey.SelectNone,
        ConsoleKey.Enter => PickerKey.Confirm,
        ConsoleKey.Escape or ConsoleKey.Q => PickerKey.Cancel,
        ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control) => PickerKey.Cancel,
        _ => PickerKey.None
    };
}
