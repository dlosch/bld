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
        // The payload here is verbatim text (markdown tables, generated batch scripts). Spectre wraps
        // at the profile width - 80 columns when redirected - which splits markdown rows and breaks
        // the table. Write the body straight to stdout so it survives redirection unaltered.
        AnsiConsole.MarkupLine(message is { } ? $"{Markup.Escape(caption)}:" : $"{Markup.Escape(caption)}");
        if (message is { }) Console.Out.WriteLine(message);
        Console.Out.WriteLine();
    }

    public void WriteWarning(string message) {
        if (_logLevel <= LogLevel.Warning) {
            AnsiConsole.MarkupLine($"[yellow]warning:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteError(string message, Exception? exception = default) {
        if (_logLevel <= LogLevel.Error) {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(message)}");
            // The caller took the trouble to hand us the exception; surfacing its type and message
            // is usually the only way to tell "missing SDK" from "malformed XML".
            if (exception is { }) {
                AnsiConsole.MarkupLine($"[red]     [/] {Markup.Escape($"{exception.GetType().Name}: {exception.FormatMessage()}")}");
                if (_logLevel <= LogLevel.Debug) AnsiConsole.WriteException(exception);
            }
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

    /// <summary>Title is markup — callers pass literals like "[bold]x[/]". Never pass user data here.</summary>
    public void WriteRule(string title) {
        AnsiConsole.Write(new Rule(title) { Justification = Justify.Left });
    }

    /// <summary>Title is plain text (callers pass file paths), so it is escaped.</summary>
    public void WriteHeader(string title, string? additionalText = default) {
        AnsiConsole.Write(new Rule(Markup.Escape(title)) { Justification = Justify.Left });
        if (!string.IsNullOrWhiteSpace(additionalText)) {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(additionalText)}[/]");
        }
    }

    public bool Confirm(string message, bool defaultValue = false) {
        // In non-interactive contexts (CI, piped/redirected stdin) AnsiConsole.Confirm throws while
        // trying to read input. Fall back to the supplied default so callers (e.g. clean --delete,
        // the batch-file overwrite prompt) skip safely instead of crashing with a stack trace.
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive) {
            WriteWarning($"Non-interactive input; assuming '{(defaultValue ? "yes" : "no")}' for prompt: {message}");
            return defaultValue;
        }
        // Callers pass plain text containing paths and package ids; Confirm renders markup.
        return AnsiConsole.Confirm(Markup.Escape(message), defaultValue);
    }

    public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull {
        return AnsiConsole.Prompt(prompt);
    }

    public List<T> MultiPrompt<T>(MultiSelectionPrompt<T> prompt) where T : notnull {
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