using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Collections.Concurrent;

namespace bld.Services;

record class Err(string Message, Exception? Exception = default, Sln? Sln = default, Proj? Proj = default, ProjCfg? Config = default) {
    public string SlnName => Path.GetFileName(Sln?.Path ?? Proj?.Parent?.Path ?? Config?.Proj?.Parent?.Path ?? "(No Solution)");
}

internal class ErrorSink(IConsoleOutput console) {

    private readonly ConcurrentBag<Err> _errors = new();

    internal void AddError(string message, Exception? exception = default, Sln? sln = default, Proj? proj = default, ProjCfg? config = default) {
        var error = new Err(message, exception, sln, proj, config);
        _errors.Add(error);
    }

    internal void WriteTo() {
        if (_errors.IsEmpty) {
            console.WriteInfo("No errors.");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("Solution").LeftAligned());
        table.AddColumn(new TableColumn("Project").LeftAligned());
        table.AddColumn(new TableColumn("Configuration").LeftAligned());
        table.AddColumn(new TableColumn("Message").LeftAligned());

        // Order by Sln.Path, then group by Proj.Path or ProjCfg.Path
        var ordered = _errors
            .OrderBy(e => e.SlnName)
            .ThenBy(e => e.Config?.Path ?? e.Proj?.Path ?? "")
            .ToList();

        var grouped = ordered
            .GroupBy(e => new {
                SlnPath = e.SlnName,
                ProjPath = e.Config?.Path ?? e.Proj?.Path ?? "(No Project)"
            });

        foreach (var group in grouped) {
            var slnPath = group.Key.SlnPath;
            var projPath = group.Key.ProjPath;
            foreach (var error in group) {
                table.AddRow(
                    Markup.Escape(slnPath),
                    Markup.Escape(projPath),
                    Markup.Escape(error.Config?.Configuration ?? ""),
                    Markup.Escape(error.Message)
                );
            }
        }

        console.WriteError($"Found {_errors.Count} error(s):");
        console.WriteTable(table);
    }
}
