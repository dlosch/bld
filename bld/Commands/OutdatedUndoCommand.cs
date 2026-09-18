using bld.Infrastructure;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class OutdatedUndoCommand : BaseCommand {

    private readonly Option<bool> _listOption = new Option<bool>("--list") {
        Description = "List the recorded runs for this input, newest first, and exit.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<int> _runOption = new Option<int>("--run") {
        Description = "Which recorded run to revert: 1 is the newest (the default), 2 the one before, as numbered by --list.",
        DefaultValueFactory = _ => 1,
        Validators = {
            v => {
                if (v.GetValueOrDefault<int>() < 1) v.AddError("--run must be at least 1.");
            }
        }
    };

    private readonly Option<string[]> _packageOption = new Option<string[]>("--package", "-p") {
        Description = "Only revert packages whose id matches one of these patterns. Same syntax as outdated --package.",
        AllowMultipleArgumentsPerToken = true
    };

    private readonly Option<string[]> _excludeOption = new Option<string[]>("--exclude") {
        Description = "Leave packages whose id matches one of these patterns as they are. Applied after --package.",
        AllowMultipleArgumentsPerToken = true
    };

    private readonly Option<bool> _interactiveOption = new Option<bool>("--interactive", "-i") {
        Description = "Pick the packages to revert from a grouped list (space toggles, a/n all or none, enter reverts, esc cancels). Needs an interactive terminal.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _yesOption = new Option<bool>("--yes", "-y") {
        Description = "Revert without asking. Required when there is no interactive terminal.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _verifyRestoreOption = new Option<bool>("--verify-restore") {
        Description = "After reverting, run 'dotnet restore' on the input and fail the command if NuGet reports errors.",
        DefaultValueFactory = _ => false
    };

    public OutdatedUndoCommand(IConsoleOutput console) : base("undo", "Revert the package updates a previous 'outdated --apply' run wrote to this input. Only values that still read as that run left them are touched; the rest is reported and left alone.", console) {
        Add(_rootOption);
        Add(_listOption);
        Add(_runOption);
        Add(_packageOption);
        Add(_excludeOption);
        Add(_interactiveOption);
        Add(_yesOption);
        Add(_verifyRestoreOption);
        Add(_logLevelOption);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        base.Output = new SpectreConsoleOutput(parseResult.GetValue(_logLevelOption));

        var service = new UndoService(Output, new UpdateJournal(UpdateJournal.DefaultRoot));
        return await service.RunAsync(
            GetRootPath(parseResult),
            parseResult.GetValue(_listOption),
            parseResult.GetValue(_runOption),
            OutdatedService.SplitPatterns(parseResult.GetValue(_packageOption)),
            OutdatedService.SplitPatterns(parseResult.GetValue(_excludeOption)),
            parseResult.GetValue(_interactiveOption),
            parseResult.GetValue(_yesOption),
            parseResult.GetValue(_verifyRestoreOption),
            cancellationToken);
    }
}
