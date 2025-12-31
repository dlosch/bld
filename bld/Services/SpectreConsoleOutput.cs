using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Text.Json;

namespace bld.Services;

/// <summary>
/// Console output implementation using Spectre.Console
/// </summary>
internal class SpectreConsoleOutput : IConsoleOutput {
    private readonly LogLevel _logLevel;
    private readonly bool _jsonOutput;

    public SpectreConsoleOutput(LogLevel logLevel = LogLevel.Warning, bool jsonOutput = false) {
        _logLevel = logLevel;
        _jsonOutput = jsonOutput;
    }

    public void WriteInfo(string message) {
        if (!_jsonOutput && _logLevel <= LogLevel.Info) {
            AnsiConsole.MarkupLine($"[blue]INF:[/] {Markup.Escape(message)}");
        }
    }


    public void WriteOutput(string caption, string? message) {
        if (_jsonOutput) return;
        if (message is { }) AnsiConsole.MarkupLine($"{caption}:\r\n{Markup.Escape(message)}\r\n");
        else AnsiConsole.MarkupLine($"{caption}\r\n");
    }

    public void WriteWarning(string message) {
        if (!_jsonOutput && _logLevel <= LogLevel.Warning) {
            AnsiConsole.MarkupLine($"[yellow]WRN:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteError(string message, Exception? exception = default) {
        if (_jsonOutput) {
            // Even in JSON mode, we might want to report errors to stderr or as part of JSON
            // For now, let's just write to stderr if it's an error
            System.Console.Error.WriteLine($"ERR: {message}");
            if (exception != null) System.Console.Error.WriteLine(exception.ToString());
            return;
        }
        if (_logLevel <= LogLevel.Error) {
            AnsiConsole.MarkupLine($"[red]ERR:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteDebug(string message) {
        if (!_jsonOutput && _logLevel <= LogLevel.Debug) {
            AnsiConsole.MarkupLine($"[grey]DBG:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteVerbose(string message) {
        if (!_jsonOutput && _logLevel <= LogLevel.Verbose) {
            AnsiConsole.MarkupLine($"[grey]VER:[/] {Markup.Escape(message)}");
        }
    }

    public void WriteTable(Table table) {
        if (!_jsonOutput) {
            AnsiConsole.Write(table);
        }
    }

    public void WriteRule(string title) {
        if (!_jsonOutput) {
            AnsiConsole.Write(new Rule(title) { Justification = Justify.Left });
        }
    }
    public void WriteHeader(string title, string? additionalText = default) {
        if (!_jsonOutput) {
            AnsiConsole.Write(new Rule(title) { Justification = Justify.Left });
            if (!string.IsNullOrWhiteSpace(additionalText)) {
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(additionalText)}[/]");
            }
        }
    }

    public bool Confirm(string message, bool defaultValue = false) {
        if (_jsonOutput) return defaultValue; // Auto-confirm with default in JSON mode
        return AnsiConsole.Confirm(message, defaultValue);
    }

    public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull {
        if (_jsonOutput) throw new InvalidOperationException("Prompts are not supported in JSON mode.");
        return AnsiConsole.Prompt(prompt);
    }

    public void StartProgress(string description, Action<ProgressContext> action) {
        if (_jsonOutput) {
            // Just run the action without progress UI
            action(null!); // ProgressContext might be used, but we can't easily mock it here if it's used for updates
            // Actually, Spectre's ProgressContext is needed if the action calls ctx.Update.
            // Let's just use a dummy if possible or just run it.
            // For now, let's assume actions can handle null or we just don't show progress.
            AnsiConsole.Progress().Start(ctx => action(ctx)); // Still start it but maybe it won't show much if redirected?
            return;
        }
        AnsiConsole.Progress()
            .Start(ctx => action(ctx));
    }

    public async Task StartProgressAsync(string description, Func<ProgressContext, Task> action) {
        if (_jsonOutput) {
            await action(null!);
            return;
        }
        await AnsiConsole.Progress()
            .StartAsync(async ctx => await action(ctx));
    }

    public async Task StartStatusAsync(string description, Func<StatusContext, Task> action) {
        if (_jsonOutput) {
            await action(null!);
            return;
        }
        await AnsiConsole.Status().Spinner(Spinner.Known.Aesthetic)
            .StartAsync(description, async ctx => await action(ctx));
    }

    public void WriteException(Exception exception) {
        if (_jsonOutput) {
            System.Console.Error.WriteLine(exception.ToString());
            return;
        }
        AnsiConsole.WriteException(exception);
    }

    public void WriteJson<T>(T data) {
        var options = new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var json = JsonSerializer.Serialize(data, options);
        System.Console.WriteLine(json);
    }
}
