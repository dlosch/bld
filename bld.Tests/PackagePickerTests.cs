using bld.Services;
using bld.Services.NuGet;
using NuGet.Versioning;
using Spectre.Console;

namespace bld.Tests;

public class PackagePickerTests {

    private static PickerTarget Target(string version, string bump) =>
        new(NuGetVersion.Parse(version), Enum.Parse<BumpKind>(bump));

    private static PickerRow Row(string id, string current, bool preselected, int defaultTarget, params PickerTarget[] targets) =>
        new(id, NuGetVersion.Parse(current), targets, defaultTarget, preselected);

    /// <summary>
    /// Two families: Microsoft.Extensions.* with a single target each, and (other) with Npgsql, which
    /// offers a patch and a major.
    /// </summary>
    private static PickerModel SampleModel() => new(new[] {
        new PickerGroup("Microsoft.Extensions.*", new[] {
            Row("Microsoft.Extensions.Hosting", "9.0.8", true, 0, Target("9.0.9", "Patch")),
            Row("Microsoft.Extensions.Logging", "9.0.8", true, 0, Target("9.0.9", "Patch")),
        }),
        new PickerGroup("(other)", new[] {
            Row("Npgsql", "8.0.5", false, 0, Target("8.0.7", "Patch"), Target("9.0.2", "Major")),
        })
    });

    private static PickerState Drive(PickerModel model, params PickerKey[] keys) {
        var state = new PickerState(model);
        foreach (var key in keys) state.Handle(key);
        return state;
    }

    [Fact]
    public void Cursor_StartsOnTheFirstPackageRowNotOnAGroupHeader() {
        var state = new PickerState(SampleModel());

        Assert.Equal(1, state.Cursor);
        Assert.Equal("Microsoft.Extensions.Hosting", state.Lines[state.Cursor].Row?.Id);
    }

    [Fact]
    public void Movement_IsClampedToTheListAndReachesGroupHeaders() {
        var model = SampleModel();

        Assert.Equal(0, Drive(model, PickerKey.Up, PickerKey.Up, PickerKey.Up).Cursor);
        Assert.Equal(model.Groups.Sum(g => g.Rows.Count) + 2 - 1, Drive(model, PickerKey.End).Cursor);
        Assert.Equal(0, Drive(model, PickerKey.End, PickerKey.Home).Cursor);
    }

    [Fact]
    public void Toggle_OnARowFlipsOnlyThatRow() {
        var state = Drive(SampleModel(), PickerKey.Toggle);

        Assert.False(state.Lines[1].Selected);
        Assert.True(state.Lines[2].Selected);
    }

    [Fact]
    public void Toggle_OnAFullySelectedGroupHeaderTurnsTheWholeFamilyOff() {
        var state = Drive(SampleModel(), PickerKey.Home, PickerKey.Toggle);

        Assert.False(state.Lines[1].Selected);
        Assert.False(state.Lines[2].Selected);
        Assert.Equal(false, state.GroupSelection(0));
    }

    [Fact]
    public void Toggle_OnAPartiallySelectedGroupHeaderTurnsTheWholeFamilyOn() {
        // Drop one member, then hit the header: partially selected counts as "not all", so it fills up.
        var state = Drive(SampleModel(), PickerKey.Toggle, PickerKey.Home);
        Assert.Null(state.GroupSelection(0));

        state.Handle(PickerKey.Toggle);

        Assert.True(state.Lines[1].Selected);
        Assert.True(state.Lines[2].Selected);
    }

    [Fact]
    public void TargetUp_MovesOneRowThroughItsCandidatesAndStopsAtTheEnd() {
        var state = Drive(SampleModel(), PickerKey.End);
        Assert.Equal("Npgsql", state.Lines[state.Cursor].Row?.Id);

        state.Handle(PickerKey.TargetUp);
        Assert.Equal(1, state.Lines[state.Cursor].TargetIndex);

        state.Handle(PickerKey.TargetUp);
        Assert.Equal(1, state.Lines[state.Cursor].TargetIndex);

        state.Handle(PickerKey.TargetDown);
        state.Handle(PickerKey.TargetDown);
        Assert.Equal(0, state.Lines[state.Cursor].TargetIndex);
    }

    [Fact]
    public void TargetUp_OnAGroupHeaderMovesEveryMemberAsFarAsItCanGo() {
        var model = new PickerModel(new[] {
            new PickerGroup("Serilog.*", new[] {
                Row("Serilog", "4.0.1", true, 0, Target("4.1.0", "Minor"), Target("5.0.0", "Major")),
                Row("Serilog.Sinks.File", "5.0.0", true, 0, Target("5.0.2", "Patch")),
            })
        });

        var state = Drive(model, PickerKey.Home, PickerKey.TargetUp);

        Assert.Equal(1, state.Lines[1].TargetIndex);
        // Only one candidate: it stays put instead of throwing or wrapping around.
        Assert.Equal(0, state.Lines[2].TargetIndex);
    }

    /// <summary>A family whose rows offer different classes: one has a patch, the other starts at a minor.</summary>
    private static PickerModel MixedFamily() => new(new[] {
        new PickerGroup("Serilog.*", new[] {
            Row("Serilog", "4.0.1", true, 0, Target("4.0.2", "Patch"), Target("4.1.0", "Minor"), Target("5.0.0", "Major")),
            Row("Serilog.Sinks.File", "5.0.0", true, 0, Target("5.1.0", "Minor"), Target("6.0.0", "Major")),
        })
    });

    [Fact]
    public void TargetUpAndDown_OnAGroupHeaderMoveTheFamilyByBumpClass() {
        var state = Drive(MixedFamily(), PickerKey.Home);
        // The header starts at the highest class any row is at.
        Assert.Equal(BumpKind.Minor, state.Lines[0].GroupBump);

        state.Handle(PickerKey.TargetUp);
        Assert.Equal(BumpKind.Major, state.Lines[0].GroupBump);
        Assert.Equal(2, state.Lines[1].TargetIndex);
        Assert.Equal(1, state.Lines[2].TargetIndex);
        Assert.False(state.GroupCanShift(0, 1));

        state.Handle(PickerKey.TargetDown);
        Assert.Equal(BumpKind.Minor, state.Lines[0].GroupBump);
        Assert.Equal(1, state.Lines[1].TargetIndex);
        Assert.Equal(0, state.Lines[2].TargetIndex);

        // Capped at patch: the row without a patch drops out of the selection instead of keeping a
        // bigger step, and the family shows as partially selected.
        state.Handle(PickerKey.TargetDown);
        Assert.Equal(BumpKind.Patch, state.Lines[0].GroupBump);
        Assert.Equal(0, state.Lines[1].TargetIndex);
        Assert.True(state.Lines[1].Selected);
        Assert.False(state.Lines[2].Selected);
        Assert.Null(state.GroupSelection(0));
        Assert.False(state.GroupCanShift(0, -1));

        // Raising the cap to a class the row has puts it back.
        state.Handle(PickerKey.TargetUp);
        Assert.True(state.Lines[2].Selected);
        Assert.Equal(0, state.Lines[2].TargetIndex);
        Assert.Equal(true, state.GroupSelection(0));
    }

    [Fact]
    public void GroupCap_DoesNotReselectARowTheUserUnchecked() {
        // Uncheck Serilog.Sinks.File by hand, cap the family at patch, raise it again.
        var state = Drive(MixedFamily(), PickerKey.Down, PickerKey.Toggle, PickerKey.Home, PickerKey.TargetDown, PickerKey.TargetUp);

        Assert.False(state.Lines[2].Selected);
        Assert.True(state.Lines[1].Selected);
    }

    [Fact]
    public void Toggle_OnACappedOutRowIsAnExplicitChoiceTheCapNoLongerOverrides() {
        // Cap at patch drops the row; checking it by hand keeps it checked, and the next cap change
        // leaves it alone because it is no longer the cap's doing.
        var state = Drive(MixedFamily(), PickerKey.Home, PickerKey.TargetDown, PickerKey.Down, PickerKey.Down, PickerKey.Toggle);
        Assert.True(state.Lines[2].Selected);

        state.Handle(PickerKey.Home);
        state.Handle(PickerKey.TargetUp);
        Assert.True(state.Lines[2].Selected);
        Assert.Equal(0, state.Lines[2].TargetIndex);
    }

    [Fact]
    public void TargetUp_OnARowLiftsItsHeaderToTheHighestClassInTheFamily() {
        var state = Drive(MixedFamily(), PickerKey.Down, PickerKey.TargetUp);

        Assert.Equal(1, state.Lines[2].TargetIndex);
        Assert.Equal(BumpKind.Major, state.Lines[0].GroupBump);
    }

    [Fact]
    public void RenderLine_ShowsTheClassToggleOnlyOnHeadersWithAChoice() {
        var mixed = PackagePickerRenderer.RenderLine(Drive(MixedFamily(), PickerKey.Home), 0, 30, 6);
        Assert.Contains("[yellow]minor[/]", mixed);
        Assert.Contains("[blue]<[/]", mixed);
        Assert.Contains("[blue]>[/]", mixed);

        // Every Microsoft.Extensions.* row offers a patch and nothing else: nothing to toggle.
        var single = PackagePickerRenderer.RenderLine(new PickerState(SampleModel()), 0, 30, 6);
        Assert.DoesNotContain("patch", single);
        Assert.DoesNotContain("[blue]", single);
    }

    [Fact]
    public void RenderLine_KeepsTheBumpColumnAlignedAcrossTargetVersionsOfDifferentLength() {
        var model = new PickerModel(new[] {
            new PickerGroup(string.Empty, new[] {
                Row("A", "9.0.8", true, 0, Target("9.0.9", "Patch")),
                Row("B", "9.0.8", true, 0, Target("10.0.12", "Major")),
            })
        });
        var state = new PickerState(model);

        var a = Markup.Remove(PackagePickerRenderer.RenderLine(state, 0, 5, 5, targetWidth: 7));
        var b = Markup.Remove(PackagePickerRenderer.RenderLine(state, 1, 5, 5, targetWidth: 7));

        Assert.Equal(a.IndexOf("patch", StringComparison.Ordinal), b.IndexOf("MAJOR", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderLine_IndentsRowsUnderAGroupHeader() {
        var grouped = PackagePickerRenderer.RenderLine(new PickerState(SampleModel()), 2, 30, 6);
        Assert.StartsWith("    [green][[X]][/] Microsoft.Extensions.Logging", grouped);

        var flat = new PickerModel(new[] { new PickerGroup(string.Empty, SampleModel().Groups[0].Rows) });
        Assert.StartsWith("  [green][[X]][/] Microsoft.Extensions.Logging", PackagePickerRenderer.RenderLine(new PickerState(flat), 1, 30, 6));
    }

    [Fact]
    public void SelectAllAndSelectNone_IgnoreTheCursor() {
        var all = Drive(SampleModel(), PickerKey.SelectAll);
        Assert.All(all.Lines.Where(l => l.Row is not null), l => Assert.True(l.Selected));

        var none = Drive(SampleModel(), PickerKey.SelectNone);
        Assert.All(none.Lines.Where(l => l.Row is not null), l => Assert.False(l.Selected));
    }

    [Fact]
    public void Confirm_ReturnsTheSelectedPackagesWithTheTargetChosenForEach() {
        // Move to Npgsql, raise it to the major, select it, confirm.
        var state = Drive(SampleModel(), PickerKey.End, PickerKey.TargetUp, PickerKey.Toggle, PickerKey.Confirm);

        var outcome = state.Result();

        Assert.False(outcome.Cancelled);
        Assert.Equal(
            new[] { "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging", "Npgsql" },
            outcome.Selected.Select(s => s.Id));
        Assert.Equal("9.0.2", outcome.Selected.Single(s => s.Id == "Npgsql").Target.ToString());
    }

    [Fact]
    public void Cancel_DiscardsEverything() {
        var state = Drive(SampleModel(), PickerKey.SelectAll, PickerKey.Cancel);

        Assert.True(state.Done);
        Assert.True(state.Result().Cancelled);
        Assert.Empty(state.Result().Selected);
    }

    [Theory]
    [InlineData(0, 3, 10, 0, 2)]   // everything fits
    [InlineData(0, 40, 10, 0, 9)]  // cursor at the top
    [InlineData(39, 40, 10, 30, 39)] // cursor at the bottom, window pinned to the end
    [InlineData(20, 40, 10, 15, 24)] // cursor centred
    public void Viewport_KeepsTheCursorInsideTheWindow(int cursor, int count, int pageSize, int first, int last) {
        Assert.Equal((first, last), PackagePickerRenderer.Viewport(cursor, count, pageSize));
    }

    [Fact]
    public void RenderLine_ShowsArrowsOnlyWhereTheyDoSomething() {
        var state = Drive(SampleModel(), PickerKey.End);

        var single = PackagePickerRenderer.RenderLine(state, 1, idWidth: 30, versionWidth: 6);
        var multi = PackagePickerRenderer.RenderLine(state, 4, idWidth: 30, versionWidth: 6);

        Assert.DoesNotContain("[blue]<[/]", single);
        Assert.DoesNotContain("[blue]>[/]", single);
        // Npgsql starts on its first of two targets: forward only, and the position is shown.
        Assert.DoesNotContain("[blue]<[/]", multi);
        Assert.Contains("[blue]>[/]", multi);
        Assert.Contains("(1/2)", multi);
    }

    [Fact]
    public void RenderLine_MarksGroupSelectionAsAllPartialOrNone() {
        var state = Drive(SampleModel(), PickerKey.Toggle);

        Assert.Contains("[[~]]", PackagePickerRenderer.RenderLine(state, 0, 30, 6));
        Assert.Contains("[[ ]]", PackagePickerRenderer.RenderLine(state, 3, 30, 6));
        Assert.Contains("[[X]]", PackagePickerRenderer.RenderLine(state, 2, 30, 6));
    }

    [Theory]
    [InlineData(ConsoleKey.LeftArrow, "TargetDown")]
    [InlineData(ConsoleKey.RightArrow, "TargetUp")]
    [InlineData(ConsoleKey.Spacebar, "Toggle")]
    [InlineData(ConsoleKey.Enter, "Confirm")]
    [InlineData(ConsoleKey.Escape, "Cancel")]
    [InlineData(ConsoleKey.F5, "None")]
    public void MapKey_TranslatesTheKeysThePickerDocuments(ConsoleKey key, string expected) {
        Assert.Equal(
            Enum.Parse<PickerKey>(expected),
            PackagePickerRenderer.MapKey(new ConsoleKeyInfo('\0', key, false, false, false)));
    }

    private static PackageVersionCandidates Candidates(string? patch, string? minor, string? major) => new() {
        Patch = patch is null ? null : new VersionCandidate { Version = patch, TargetFrameworkVersions = [] },
        Minor = minor is null ? null : new VersionCandidate { Version = minor, TargetFrameworkVersions = [] },
        Major = major is null ? null : new VersionCandidate { Version = major, TargetFrameworkVersions = [] }
    };

    [Fact]
    public void TargetsFor_DropsNonUpdatesAndOrdersTheRestAscending() {
        var targets = OutdatedService.TargetsFor(
            NuGetVersion.Parse("1.2.3"),
            NuGetVersion.Parse("1.4.0"),
            Candidates("1.2.3", "1.4.0", "2.0.0"));

        // 1.2.3 is the baseline, not an update, so it is not offered.
        Assert.Equal(new[] { "1.4.0", "2.0.0" }, targets.Select(t => t.Version.ToString()));
        Assert.Equal(new[] { BumpKind.Minor, BumpKind.Major }, targets.Select(t => t.Bump));
    }

    [Fact]
    public void TargetsFor_WithoutCandidatesOffersOnlyTheCapsChoice() {
        var targets = OutdatedService.TargetsFor(NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("1.1.0"), null);

        var single = Assert.Single(targets);
        Assert.Equal("1.1.0", single.Version.ToString());
    }

    [Fact]
    public void BuildPickerModel_PointsTheDefaultAtTheTargetTheCapPicked() {
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Npgsql"] = (NuGetVersion.Parse("8.0.5"), NuGetVersion.Parse("8.0.7"))
        };
        var candidates = new Dictionary<string, PackageVersionCandidates>(StringComparer.OrdinalIgnoreCase) {
            ["Npgsql"] = Candidates("8.0.7", "8.0.7", "9.0.2")
        };

        var model = OutdatedService.BuildPickerModel(outdated, GroupingOptions.Default, Preselect.All, candidates);

        var row = model.Groups.SelectMany(g => g.Rows).Single();
        Assert.Equal(new[] { "8.0.7", "9.0.2" }, row.Targets.Select(t => t.Version.ToString()));
        Assert.Equal(0, row.DefaultTarget);
        Assert.Equal("8.0.7", row.Target.ToString());
    }

    [Fact]
    public void BuildPickerModel_RowsOnlyReachableAboveTheCapStayUnchecked() {
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["InCap"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("1.0.1")),
            ["AboveCap"] = (NuGetVersion.Parse("2.0.0"), NuGetVersion.Parse("3.0.0"))
        };

        var model = OutdatedService.BuildPickerModel(
            outdated, GroupingOptions.Default, Preselect.All,
            candidates: null,
            neverPreselect: new HashSet<string>(new[] { "AboveCap" }, StringComparer.OrdinalIgnoreCase));

        var rows = model.Groups.SelectMany(g => g.Rows).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        Assert.True(rows["InCap"].Preselected);
        Assert.False(rows["AboveCap"].Preselected);
    }

    [Fact]
    public void Policy_OnARowSetsANoMajorRuleForThatIdAndCapsTheTarget() {
        // Npgsql sits on its major; the policy moves it down to the patch and records the rule.
        var state = Drive(SampleModel(), PickerKey.End, PickerKey.TargetUp);
        Assert.Equal(1, state.Lines[state.Cursor].TargetIndex);

        state.Handle(PickerKey.Policy);

        var line = state.Lines[state.Cursor];
        Assert.Equal("Npgsql", line.PolicyMatch);
        Assert.Equal(MaxBump.Minor, line.PolicyLevel);
        Assert.Equal(0, line.TargetIndex);
        var change = Assert.Single(state.Result().PolicyChanges);
        Assert.Equal(("Npgsql", (MaxBump?)MaxBump.Minor), change);
    }

    [Fact]
    public void Policy_PressedAgainClearsTheRuleAndLeavesTheTargetAlone() {
        var state = Drive(SampleModel(), PickerKey.End, PickerKey.Policy, PickerKey.TargetUp, PickerKey.Policy);

        var line = state.Lines[state.Cursor];
        Assert.Null(line.PolicyLevel);
        Assert.Equal(1, line.TargetIndex);
        // Set then cleared in one session: the outcome asks to remove a rule that was never saved,
        // which the store treats as a no-op.
        var change = Assert.Single(state.Result().PolicyChanges);
        Assert.Equal(("Npgsql", (MaxBump?)null), change);
    }

    [Fact]
    public void Policy_OnAPrefixGroupHeaderRecordsThePatternOnce() {
        var state = Drive(SampleModel(), PickerKey.Home, PickerKey.Policy);

        Assert.All(new[] { state.Lines[1], state.Lines[2] }, l => {
            Assert.Equal("Microsoft.Extensions.*", l.PolicyMatch);
            Assert.Equal(MaxBump.Minor, l.PolicyLevel);
        });
        var change = Assert.Single(state.Result().PolicyChanges);
        Assert.Equal(("Microsoft.Extensions.*", (MaxBump?)MaxBump.Minor), change);
    }

    [Fact]
    public void Policy_OnAPrefixGroupHeaderGivesTheBarePackageItsOwnRule() {
        // "Serilog.*" needs a dot after the prefix, so it would never cover Serilog itself.
        var model = new PickerModel(new[] {
            new PickerGroup("Serilog.*", new[] {
                Row("Serilog", "4.0.1", true, 0, Target("4.1.0", "Minor")),
                Row("Serilog.Sinks.File", "5.0.0", true, 0, Target("5.0.2", "Patch")),
            })
        });

        var state = Drive(model, PickerKey.Home, PickerKey.Policy);

        Assert.Equal("Serilog", state.Lines[1].PolicyMatch);
        Assert.Equal("Serilog.*", state.Lines[2].PolicyMatch);
        Assert.Equal(new[] {
            ("Serilog.*", (MaxBump?)MaxBump.Minor),
            ("Serilog", (MaxBump?)MaxBump.Minor),
        }, state.Result().PolicyChanges);
    }

    [Fact]
    public void Policy_OnAPrefixGroupHeaderRemovesThePerPackageRulesItReplaces() {
        // Hosting already has its own rule; the family pattern covers it now, so the old rule goes.
        var model = new PickerModel(new[] {
            new PickerGroup("Microsoft.Extensions.*", new[] {
                Row("Microsoft.Extensions.Hosting", "9.0.8", true, 0, Target("9.0.9", "Patch")) with { PolicyMatch = "Microsoft.Extensions.Hosting", PolicyLevel = MaxBump.Minor },
                Row("Microsoft.Extensions.Logging", "9.0.8", true, 0, Target("9.0.9", "Patch")),
            })
        });

        var state = Drive(model, PickerKey.Home, PickerKey.Policy);

        Assert.Equal("Microsoft.Extensions.*", state.Lines[1].PolicyMatch);
        Assert.Equal(new[] {
            ("Microsoft.Extensions.*", (MaxBump?)MaxBump.Minor),
            ("Microsoft.Extensions.Hosting", (MaxBump?)null),
        }, state.Result().PolicyChanges);
    }

    [Fact]
    public void Policy_ClearingAPatternRuleFromOneRowClearsEveryRowItCovered() {
        // Both rows carry the same pattern rule from the file; clearing it on one row removes the
        // rule, so the other row cannot keep claiming it.
        var model = new PickerModel(new[] {
            new PickerGroup("Microsoft.Extensions.*", new[] {
                Row("Microsoft.Extensions.Hosting", "9.0.8", true, 0, Target("9.0.9", "Patch")) with { PolicyMatch = "Microsoft.*", PolicyLevel = MaxBump.Minor },
                Row("Microsoft.Extensions.Logging", "9.0.8", true, 0, Target("9.0.9", "Patch")) with { PolicyMatch = "Microsoft.*", PolicyLevel = MaxBump.Minor },
            })
        });

        var state = Drive(model, PickerKey.Policy);

        Assert.Null(state.Lines[1].PolicyLevel);
        Assert.Null(state.Lines[2].PolicyLevel);
        Assert.Equal(("Microsoft.*", (MaxBump?)null), Assert.Single(state.Result().PolicyChanges));
    }

    [Fact]
    public void Policy_ARowWithOnlyAMajorLeavesTheSelectionUntilTheRuleIsCleared() {
        var model = new PickerModel(new[] {
            new PickerGroup("", new[] { Row("Only.Major", "1.0.0", true, 0, Target("2.0.0", "Major")) })
        });

        var state = Drive(model, PickerKey.Policy);
        Assert.False(state.Lines[0].Selected);
        Assert.Empty(state.Result().Selected);

        state.Handle(PickerKey.Policy);
        // Clearing the rule does not silently re-select a major the user never confirmed.
        Assert.False(state.Lines[0].Selected);
    }

    [Fact]
    public void PageSize_UsesTheWholeTerminalHeightAndAccountsForWrappedHeaderLines() {
        // Title, instructions, blank line, two overflow hints and the cursor line: six lines of chrome
        // when nothing wraps, one more when the instruction line wraps on a narrow terminal.
        Assert.Equal(44, PackagePickerRenderer.PageSize(PickerMode.Update, "Select", height: 50, width: 200));
        Assert.Equal(43, PackagePickerRenderer.PageSize(PickerMode.Update, "Select", height: 50, width: 80));
        Assert.Equal(44, PackagePickerRenderer.PageSize(PickerMode.Revert, "Revert", height: 50, width: 80));
        Assert.Equal(5, PackagePickerRenderer.PageSize(PickerMode.Update, "Select", height: 8, width: 200));
    }

    [Fact]
    public void Render_ShowsThePolicyAndItsPatternWhenItIsNotTheRowsOwnId() {
        var model = new PickerModel(new[] {
            new PickerGroup("", new[] { Row("Npgsql", "8.0.5", true, 0, Target("8.0.7", "Patch")) with { PolicyMatch = "Npg*", PolicyLevel = MaxBump.Minor } })
        });
        var state = new PickerState(model);

        var line = PackagePickerRenderer.RenderLine(state, 0, 6, 5);

        Assert.Contains("policy:minor (Npg*)", line);
    }

    /// <summary>An undo picker: one target per row, one row locked because the file changed since.</summary>
    private static PickerModel RevertModel() => new(new[] {
        new PickerGroup("MassTransit.*", new[] {
            Row("MassTransit", "9.0.0", true, 0, Target("8.4.1", "Major")),
            Row("MassTransit.RabbitMQ", "9.0.0", true, 0, Target("8.4.1", "Major")),
        }),
        new PickerGroup("(other)", new[] {
            Row("Serilog", "4.3.0", false, 0, Target("4.1.0", "Minor")) with { Locked = true, Note = "changed since" },
            Row("Polly", "8.4.1", true, 0, Target("8.4.1", "Patch")) with { NowLabel = "(commented out)", Note = "orphan" },
        })
    }, PickerMode.Revert);

    [Fact]
    public void Revert_IgnoresTargetAndPolicyKeys() {
        var state = Drive(RevertModel(), PickerKey.TargetUp, PickerKey.TargetDown, PickerKey.Policy);

        Assert.All(state.Lines.Where(l => l.Row is not null), l => Assert.Equal(0, l.TargetIndex));
        Assert.Empty(state.Result().PolicyChanges);
        Assert.Null(state.Lines[0].GroupBump);
    }

    [Fact]
    public void Revert_ALockedRowStaysUnselectedAndDoesNotCountAgainstItsGroup() {
        var model = RevertModel();
        var state = new PickerState(model);
        var serilog = state.Lines.Single(l => l.Row?.Id == "Serilog");
        var otherHeader = state.Lines.ToList().FindIndex(l => l.GroupName == "(other)");

        // (other) has Polly checked and Serilog locked: that is "all", not "some".
        Assert.True(state.GroupSelection(otherHeader));

        state.Handle(PickerKey.SelectAll);
        Assert.False(serilog.Selected);

        while (state.Cursor < state.Lines.ToList().IndexOf(serilog)) state.Handle(PickerKey.Down);
        state.Handle(PickerKey.Toggle);
        Assert.False(serilog.Selected);
        Assert.DoesNotContain(state.Result().Selected, s => s.Id == "Serilog");
    }

    [Fact]
    public void Revert_RendersWithoutArrowsOrPolicyKeysAndMarksLockedRows() {
        var state = new PickerState(RevertModel());
        var lines = state.Lines.ToList();
        var serilog = lines.FindIndex(l => l.Row?.Id == "Serilog");
        var polly = lines.FindIndex(l => l.Row?.Id == "Polly");
        var idWidth = 20;
        var massTransit = PackagePickerRenderer.RenderLine(state, 1, idWidth, 15, 5);
        var locked = PackagePickerRenderer.RenderLine(state, serilog, idWidth, 15, 5);
        var orphan = PackagePickerRenderer.RenderLine(state, polly, idWidth, 15, 5);

        Assert.Contains("9.0.0", massTransit);
        Assert.Contains("-> 8.4.1", massTransit);
        Assert.Contains("MAJOR", massTransit);
        Assert.DoesNotContain("<", massTransit);
        Assert.Contains("[[-]]", locked);
        Assert.Contains("changed since", locked);
        Assert.Contains("(commented out) -> 8.4.1", orphan);
        Assert.DoesNotContain("patch", orphan);
        Assert.DoesNotContain("left/right", PackagePickerRenderer.RevertInstructions);
        Assert.DoesNotContain("p:", PackagePickerRenderer.RevertInstructions);
        Assert.Contains("enter: revert", PackagePickerRenderer.RevertInstructions);
    }
}
