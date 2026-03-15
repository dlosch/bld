using bld.Infrastructure;
using bld.Models;
using Spectre.Console;

namespace bld.Services;

/// <summary>
/// Console output implementation using Spectre.Console
/// </summary>
internal class SpectreConsoleOutput : IConsoleOutput {
    private readonly LogLevel _logLevel;

    public SpectreConsoleOutput(LogLevel logLevel = LogLevel.Warning) {
        _logLevel = logLevel;
    }

    public void WriteLine(string message) {
        AnsiConsole.MarkupLine(Markup.Escape(message));
    }

    public void WriteInfo(string message) {
        if (_logLevel <= LogLevel.Info) {
            AnsiConsole.MarkupLine($"[dim]info:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteOutput(string caption, string? message) {
        if (message is { }) AnsiConsole.MarkupLine($"{caption}:\r\n{Markup.Escape(message)}\r\n");
        else AnsiConsole.MarkupLine($"{caption}\r\n");
    }

    public void WriteWarning(string message) {
        if (_logLevel <= LogLevel.Warning) {
            AnsiConsole.MarkupLine($"[yellow]warning:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteError(string message, Exception? exception = default) {
        if (_logLevel <= LogLevel.Error) {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteDebug(string message) {
        if (_logLevel <= LogLevel.Debug) {
            AnsiConsole.MarkupLine($"[dim]debug:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteVerbose(string message) {
        if (_logLevel <= LogLevel.Verbose) {
            AnsiConsole.MarkupLine($"[dim]verbose:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteTable(Table table) {
        AnsiConsole.Write(table);
    }

    public void WriteRule(string title) {
        AnsiConsole.Write(new Rule(title) { Justification = Justify.Left });
    }
    public void WriteHeader(string title, string? additionalText = default) {
        AnsiConsole.Write(new Rule(title) { Justification = Justify.Left });
        if (!string.IsNullOrWhiteSpace(additionalText)) {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(additionalText)}[/]");
        }
    }

    public bool Confirm(string message, bool defaultValue = false) {
        return AnsiConsole.Confirm(message, defaultValue);
    }

    public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull {
        return AnsiConsole.Prompt(prompt);
    }

    public void StartProgress(string description, Action<ProgressContext> action) {
        AnsiConsole.Progress()
            .Start(ctx => action(ctx));
    }

    public async Task StartProgressAsync(string description, Func<ProgressContext, Task> action) {
        await AnsiConsole.Progress()
            .StartAsync(async ctx => await action(ctx));
    }

    public async Task StartStatusAsync(string description, Func<StatusContext, Task> action) {
        await AnsiConsole.Status().Spinner(Spinner.Known.Aesthetic)
            .StartAsync(description, async ctx => await action(ctx));
    }

    public void WriteException(Exception exception) {
        AnsiConsole.WriteException(exception);
    }
}