using bld.Models;
using bld.Services;
using Spectre.Console;

namespace bld.Tests;

/// <summary>
/// The clean picker as a state machine: category keys act on every project from the top line and
/// on one project below it, space acts on a directory or a whole scope, and the result is the
/// directories left checked - as the fully qualified paths the deletion runs on.
/// </summary>
public class CleanPickerTests {

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "bld_picker_repo");

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    private static CleanPickerRow Row(string project, string name, CleanCategory category, long bytes = 1024 * 1024) =>
        new(P(project, name), category, bytes, 3);

    /// <summary>
    /// Lines: 0 all, 1 App, 2 App/bin, 3 App/obj, 4 Tests, 5 Tests/bin, 6 Tests/obj, 7 Tests/publish, 8 Tests/TestResults.
    /// </summary>
    private static CleanPickerModel SampleModel(params CleanCategory[] preselected) => new(new[] {
        new CleanPickerGroup("App", new[] { Row("App", "bin", CleanCategory.Bin), Row("App", "obj", CleanCategory.Obj) }),
        new CleanPickerGroup("Tests", new[] {
            Row("Tests", "bin", CleanCategory.Bin),
            Row("Tests", "obj", CleanCategory.Obj),
            Row("Tests", "publish", CleanCategory.Publish),
            Row("Tests", "TestResults", CleanCategory.TestResults),
        }),
    }, new HashSet<CleanCategory>(preselected.Length == 0 ? new[] { CleanCategory.Bin } : preselected));

    private static CleanPickerState Drive(CleanPickerModel model, params CleanPickerKey[] keys) {
        var state = new CleanPickerState(model);
        foreach (var key in keys) state.Handle(key);
        return state;
    }

    private static IEnumerable<string> Selected(CleanPickerState state) =>
        state.Lines.Where(l => l.Selected && l.Row is not null).Select(l => l.Row!.Path);

    [Fact]
    public void Start_OnTheTopLineWithThePreselectedCategoriesChecked() {
        var state = new CleanPickerState(SampleModel());

        Assert.Equal(0, state.Cursor);
        Assert.True(state.Lines[0].IsAll);
        Assert.Equal(new[] { P("App", "bin"), P("Tests", "bin") }, Selected(state));
        Assert.Equal(new[] { CleanCategory.Bin, CleanCategory.Obj, CleanCategory.Publish, CleanCategory.TestResults }, state.Categories);
        Assert.Equal(CategoryState.On, state.StateOf(0, CleanCategory.Bin));
        Assert.Equal(CategoryState.Off, state.StateOf(0, CleanCategory.Obj));
        Assert.Equal(CategoryState.Absent, state.StateOf(1, CleanCategory.Publish));
    }

    [Fact]
    public void CategoryKey_OnTheTopLineFlipsTheCategoryInEveryProject() {
        var state = Drive(SampleModel(), CleanPickerKey.Obj);
        Assert.Equal(new[] { P("App", "bin"), P("App", "obj"), P("Tests", "bin"), P("Tests", "obj") }, Selected(state));
        Assert.Equal(CategoryState.On, state.StateOf(0, CleanCategory.Obj));

        state.Handle(CleanPickerKey.Obj);
        Assert.Equal(new[] { P("App", "bin"), P("Tests", "bin") }, Selected(state));
    }

    [Fact]
    public void CategoryKey_OnAProjectLineOverridesThatProjectOnly() {
        var state = Drive(SampleModel(), CleanPickerKey.Down, CleanPickerKey.Down, CleanPickerKey.Down, CleanPickerKey.Down, CleanPickerKey.Obj);
        Assert.Equal("Tests", state.Lines[state.Cursor].Name);

        Assert.Equal(new[] { P("App", "bin"), P("Tests", "bin"), P("Tests", "obj") }, Selected(state));
        // The override shows on the top line as a mixed category, and on the project line as on.
        Assert.Equal(CategoryState.Mixed, state.StateOf(0, CleanCategory.Obj));
        Assert.Equal(CategoryState.On, state.StateOf(4, CleanCategory.Obj));
        Assert.Equal(CategoryState.Off, state.StateOf(1, CleanCategory.Obj));
        Assert.Null(state.Selection(0));
        Assert.Null(state.Selection(4));

        // A global toggle after an override sets everything: mixed counts as "not all on".
        state.Handle(CleanPickerKey.Home);
        state.Handle(CleanPickerKey.Obj);
        Assert.Equal(CategoryState.On, state.StateOf(0, CleanCategory.Obj));
    }

    [Fact]
    public void CategoryKey_OnADirectoryRowActsOnItsProject() {
        // Cursor on Tests/bin (line 5): 'p' flips Tests' publish, not App's anything.
        var state = Drive(SampleModel(), CleanPickerKey.PageDown, CleanPickerKey.Up, CleanPickerKey.Up, CleanPickerKey.Up, CleanPickerKey.Publish);
        Assert.Equal(P("Tests", "bin"), state.Lines[state.Cursor].Row?.Path);

        Assert.Contains(P("Tests", "publish"), Selected(state));
        Assert.Equal(2 + 1, Selected(state).Count());
    }

    [Fact]
    public void CategoryKey_WithoutSuchRowsInScopeDoesNothing() {
        var state = Drive(SampleModel(), CleanPickerKey.Down, CleanPickerKey.Publish);
        Assert.Equal("App", state.Lines[state.Cursor].Name);

        Assert.Equal(new[] { P("App", "bin"), P("Tests", "bin") }, Selected(state));
    }

    [Fact]
    public void Space_OnARowFlipsIt_OnAHeaderFlipsItsScope() {
        var state = Drive(SampleModel(), CleanPickerKey.Down, CleanPickerKey.Down, CleanPickerKey.Down, CleanPickerKey.Toggle);
        Assert.Equal(P("App", "obj"), state.Lines[state.Cursor].Row?.Path);
        Assert.Equal(new[] { P("App", "bin"), P("App", "obj"), P("Tests", "bin") }, Selected(state));

        // App is fully on now: space on its line turns the whole project off.
        state.Handle(CleanPickerKey.Up);
        state.Handle(CleanPickerKey.Up);
        state.Handle(CleanPickerKey.Toggle);
        Assert.Equal(new[] { P("Tests", "bin") }, Selected(state));
        Assert.Equal(false, state.Selection(1));

        // The top line: not everything is on, so space turns everything on, then off.
        state.Handle(CleanPickerKey.Home);
        state.Handle(CleanPickerKey.Toggle);
        Assert.Equal(6, Selected(state).Count());
        state.Handle(CleanPickerKey.Toggle);
        Assert.Empty(Selected(state));
    }

    [Fact]
    public void AllAndNone_CoverEveryRow() {
        Assert.Equal(6, Selected(Drive(SampleModel(), CleanPickerKey.SelectAll)).Count());
        Assert.Empty(Selected(Drive(SampleModel(), CleanPickerKey.SelectNone)));
    }

    [Fact]
    public void Result_ReturnsTheCheckedPathsOrCancelled() {
        var confirmed = Drive(SampleModel(CleanCategory.Bin, CleanCategory.TestResults), CleanPickerKey.Confirm).Result();
        Assert.False(confirmed.Cancelled);
        Assert.Equal(new[] { P("App", "bin"), P("Tests", "bin"), P("Tests", "TestResults") }, confirmed.Selected);

        var cancelled = Drive(SampleModel(), CleanPickerKey.SelectAll, CleanPickerKey.Cancel).Result();
        Assert.True(cancelled.Cancelled);
        Assert.Empty(cancelled.Selected);
    }

    [Fact]
    public void Movement_IsClampedAndPagesByTen() {
        var state = Drive(SampleModel(), CleanPickerKey.Up);
        Assert.Equal(0, state.Cursor);
        state.Handle(CleanPickerKey.PageDown);
        Assert.Equal(8, state.Cursor);
        state.Handle(CleanPickerKey.End);
        Assert.Equal(8, state.Cursor);
        state.Handle(CleanPickerKey.PageUp);
        Assert.Equal(0, state.Cursor);
    }

    [Fact]
    public void Render_ShowsMixedCategoriesAndSizesOnHeaders() {
        var state = Drive(SampleModel(), CleanPickerKey.Down, CleanPickerKey.Down, CleanPickerKey.Down, CleanPickerKey.Down, CleanPickerKey.Obj, CleanPickerKey.Home);

        var width = CleanPickerRenderer.NameWidth(state);
        Assert.Equal("All projects".Length, width);

        var top = CleanPickerRenderer.RenderLine(state, 0, width);
        Assert.Contains("[yellow][[-]][/] [bold]All projects", top);
        Assert.Contains("bin[green][[X]][/]", top);
        Assert.Contains("obj[yellow][[-]][/]", top);
        Assert.Contains("publish[[ ]]", top);
        Assert.Contains("  3.0 MiB of   6.0 MiB", top);

        var app = CleanPickerRenderer.RenderLine(state, 1, width);
        Assert.Contains("publish[[·]]", app);
        // The project line is indented by two and its name padded two less, so the category
        // columns start at the same column on both lines.
        Assert.Equal(Markup.Remove(top).IndexOf("bin[", StringComparison.Ordinal), Markup.Remove(app).IndexOf("bin[", StringComparison.Ordinal));

        var row = CleanPickerRenderer.RenderLine(state, 6, width);
        Assert.Contains("[green][[X]][/] obj", row);
        Assert.Contains(P("Tests", "obj"), Markup.Remove(row));
    }

    [Fact]
    public void RenderLine_ShowsTheWholePathWhenItFitsAndCutsTheMiddleWhenItDoesNot() {
        var state = new CleanPickerState(SampleModel());
        var nameWidth = CleanPickerRenderer.NameWidth(state);
        var path = P("App", "bin");

        Assert.Contains(path, Markup.Remove(CleanPickerRenderer.RenderLine(state, 2, nameWidth, 400)));

        // Too narrow: the leaf survives, the root survives, and what is left is marked as cut - so a
        // shortened line can never be read as a path relative to something else.
        var narrow = Markup.Remove(CleanPickerRenderer.RenderLine(state, 2, nameWidth, 45));
        Assert.DoesNotContain(path, narrow);
        Assert.Contains("...", narrow);
        Assert.EndsWith(Path.Combine("App", "bin"), narrow);
        Assert.True(narrow.Length <= 45, narrow);
    }

    [Fact]
    public void Shorten_KeepsTheRootAndTheTail() {
        var path = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "a", "very", "long", "way", "down", "bin");
        var root = Path.GetPathRoot(path)!;

        Assert.Equal(path, CleanPickerRenderer.Shorten(path, path.Length));
        Assert.Equal(path, CleanPickerRenderer.Shorten(path, path.Length + 10));

        var cut = CleanPickerRenderer.Shorten(path, path.Length - 4);
        Assert.Equal(path.Length - 4, cut.Length);
        Assert.StartsWith(root + "...", cut);
        Assert.EndsWith("bin", cut);

        // Down to a handful of characters the leaf is what has to survive, whatever else goes.
        var tiny = CleanPickerRenderer.Shorten(path, 8);
        Assert.Equal(8, tiny.Length);
        Assert.Contains("...", tiny);
        Assert.EndsWith("bin", tiny);
    }

    [Fact]
    public void Model_GroupsByOwningProjectAndPreselectsFromTheFlags() {
        var root = Path.Combine(Path.GetTempPath(), "bld_cp_" + Guid.NewGuid().ToString("N"));
        var bin = Directory.CreateDirectory(Path.Combine(root, "App", "bin", "Debug", "net8.0"));
        var obj = Directory.CreateDirectory(Path.Combine(root, "App", "obj"));
        var tests = Directory.CreateDirectory(Path.Combine(root, "TestResults"));
        File.WriteAllBytes(Path.Combine(bin.FullName, "App.dll"), new byte[2048]);
        try {
            var appDir = AppDir(root);
            var package = new FileInfo(Path.Combine(root, "feed", "App.1.0.0.nupkg"));
            package.Directory!.Create();
            File.WriteAllBytes(package.FullName, new byte[512]);
            File.WriteAllBytes(Path.Combine(package.DirectoryName!, "Other.1.0.0.nupkg"), new byte[100_000]);
            var result = new MarkDeleteResult(new List<DirResult> {
                new(bin, new[] { appDir }, CleanCategory.Bin),
                new(obj, new[] { appDir }, CleanCategory.Obj),
                new(tests, new[] { appDir }, CleanCategory.TestResults),
            }) { Files = new List<FileResult> { new(package, new[] { appDir }, CleanCategory.Package) } };

            var model = CleanPickerModel.From(result, new CleaningOptions { CleanObjDirectory = false, CleanTestResults = true });

            var group = Assert.Single(model.Groups);
            Assert.Equal("App", group.Name);
            Assert.Equal(4, group.Rows.Count);
            var binRow = group.Rows.Single(r => r.Category == CleanCategory.Bin);
            Assert.Equal(2048, binRow.Bytes);
            Assert.Equal(1, binRow.Files);
            // Every row carries the fully qualified path, which is the one the deletion runs on.
            Assert.Equal(bin.FullName, binRow.Path);
            Assert.All(group.Rows, r => Assert.True(Path.IsPathFullyQualified(r.Path), r.Path));
            // The package row is the file alone: its size, its path, not the feed directory around it.
            var packageRow = group.Rows.Single(r => r.Category == CleanCategory.Package);
            Assert.Equal(512, packageRow.Bytes);
            Assert.Equal(package.FullName, packageRow.Path);
            Assert.Equal(new HashSet<CleanCategory> { CleanCategory.Bin, CleanCategory.TestResults }, model.Preselected);
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Model_RefusesARelativePathAndOffersOneRowPerPath() {
        var root = Path.Combine(Path.GetTempPath(), "bld_cp_" + Guid.NewGuid().ToString("N"));
        var bin = Directory.CreateDirectory(Path.Combine(root, "App", "bin"));
        try {
            var appDir = AppDir(root);

            // A trailing separator on the marked key normalises away on DirectoryInfo.FullName: the
            // same directory must not end up as two rows the user can answer differently.
            var twice = new MarkDeleteResult(new List<DirResult> {
                new(bin, new[] { appDir }, CleanCategory.Bin),
                new(new DirectoryInfo(bin.FullName + Path.DirectorySeparatorChar), new[] { appDir }, CleanCategory.Bin),
            });
            var deduped = CleanPickerModel.From(twice, new CleaningOptions());
            Assert.Single(deduped.Groups.SelectMany(g => g.Rows));

            var relative = new MarkDeleteResult(new List<DirResult> {
                new(new DirectoryInfo(Path.Combine("App", "bin")), new[] { appDir }, CleanCategory.Bin),
            });
            // DirectoryInfo roots a relative path against the current directory; only a path that
            // stays relative after that is a bug, so build the failing case directly.
            Assert.Throws<InvalidOperationException>(() => CleanSelection.Apply(relative, new[] { Path.Combine("App", "bin") }));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Selection_KeepsExactlyTheCheckedEntries() {
        var root = Path.Combine(Path.GetTempPath(), "bld_cp_" + Guid.NewGuid().ToString("N"));
        var bin = Directory.CreateDirectory(Path.Combine(root, "App", "bin"));
        var obj = Directory.CreateDirectory(Path.Combine(root, "App", "obj"));
        try {
            var appDir = AppDir(root);
            var package = new FileInfo(Path.Combine(root, "feed", "App.1.0.0.nupkg"));
            package.Directory!.Create();
            File.WriteAllBytes(package.FullName, new byte[16]);
            var result = new MarkDeleteResult(new List<DirResult> {
                new(bin, new[] { appDir }, CleanCategory.Bin),
                new(obj, new[] { appDir }, CleanCategory.Obj),
            }) { Files = new List<FileResult> { new(package, new[] { appDir }, CleanCategory.Package) } };

            var kept = CleanSelection.Apply(result, new[] { bin.FullName, package.FullName });

            Assert.Equal(new[] { bin.FullName }, kept.Directories.Select(d => d.Directory.FullName));
            Assert.Equal(new[] { package.FullName }, kept.Files.Select(f => f.File.FullName));
            Assert.True(CleanSelection.Apply(result, Array.Empty<string>()).IsEmpty);
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Selection_RefusesAPathTheRunNeverMarked() {
        var root = Path.Combine(Path.GetTempPath(), "bld_cp_" + Guid.NewGuid().ToString("N"));
        var bin = Directory.CreateDirectory(Path.Combine(root, "App", "bin"));
        try {
            var result = new MarkDeleteResult(new List<DirResult> { new(bin, new[] { AppDir(root) }, CleanCategory.Bin) });
            var elsewhere = Path.Combine(root, "..", "somewhere", "else");

            var ex = Assert.Throws<InvalidOperationException>(() => CleanSelection.Apply(result, new[] { bin.FullName, Path.GetFullPath(elsewhere) }));
            Assert.Contains("never marked", ex.Message);
            Assert.Contains("Nothing was deleted", ex.Message);

            Assert.Throws<InvalidOperationException>(() => CleanSelection.Apply(result, new[] { "" }));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static Dir AppDir(string root) =>
        new(new List<(string, DirType)>(), new Dictionary<string, string?> { [Path.Combine(root, "App", "App.csproj")] = "App" }, new HashSet<string>(), new HashSet<string>(), new HashSet<string>());
}
