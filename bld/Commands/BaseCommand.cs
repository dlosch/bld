using bld.Infrastructure;
using bld.Models;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal abstract class BaseCommand : Command {
    protected IConsoleOutput Output { get; set; }

    protected virtual Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        return Task.FromResult(0);
    }

    protected readonly Option<string?> _vsToolsPath = new Option<string?>("--vstoolspath", "-vs") {
        Description = "Explicit value for VSToolsPath. If not specified, the tool tries to resolve VSToolsPath either from environment variable or via vswhere.exe. VSToolsPath contains additional msbuild target files which may be required for evaluation of project files.",
        DefaultValueFactory = _ => null,
    };

    protected readonly Option<bool> _noResolveVsToolsPath = new Option<bool>("--novstoolspath", "-novs") {
        Description = "Do not try to resolve VSToolsPath from ENV or vswhere.exe.",
        DefaultValueFactory = _ => false,
    };


    private static bool ValidatePathExists(string? path) {
        return path is not null && (File.Exists(path) || Directory.Exists(path));
    }

    protected static void RootPathValidator(ArgumentResult v) {
        var path = v.GetValueOrDefault<string?>();
        if (!ValidatePathExists(path)) {
            v.AddError($"{path} does not exist.");
        }
    }

    protected static void RootPathValidator(OptionResult v) {
        var path = v.GetValueOrDefault<string?>();
        if (!ValidatePathExists(path)) {
            v.AddError($"{path} does not exist.");
        }
    }
    protected readonly Option<string?> _rootOption = new Option<string?>("--root", "-r") {
        Description = "Root directory, .sln, or project file. Can also be specified as trailing argument.",
        Validators = {
            RootPathValidator
        }
    };

    protected readonly Option<int> _depthOption = new Option<int>("--depth", "-d") {
        Description = "If root is a directory, recursion depth to search for .sln or project files.",
        DefaultValueFactory = _ => 3,
        Validators = {
            v => {
                if (v.GetValueOrDefault<int>() < 0) {
                    v.AddError($"Depth cannot be negative.");
                }
                else if (v.GetValueOrDefault<int>() > 0x20) {
                    v.AddError($"Depth option cannot exceed {0x20}.");
                }
            }
        }
    };

    protected readonly Option<LogLevel> _logLevelOption = new Option<LogLevel>("--log", "-v", "--verbosity") {
        Description = "Log verbosity (Debug, Verbose, Info, Warning, Error).",
        DefaultValueFactory = _ => LogLevel.Warning
    };

    protected readonly Option<bool> _markdownOption = new Option<bool>("--markdown", "-md") {
        Description = "Emit markdown table output where supported.",
        DefaultValueFactory = _ => false
    };


    protected readonly Option<int> _concurrencyOption = new Option<int>("--concurrency") {
        Description = "Degree of parallelism for project evaluation. Use 1 for sequential processing.",
        DefaultValueFactory = _ => Math.Max(1, Environment.ProcessorCount >> 1),
        Validators = {
            v => {
                if (v.GetValueOrDefault<int>() < 1) {
                    v.AddError("Concurrency must be at least 1.");
                }
            }
        }
    };

    protected readonly Argument<string?> _rootArgument = new Argument<string?>("root") {
        Arity = ArgumentArity.ZeroOrOne,
        Validators = {
            RootPathValidator
        }
    };

    protected BaseCommand(string name, string? description, IConsoleOutput console) : base(name, description) {
        Output = console;

        Add(_markdownOption);

        SetAction(async (parseResult, cancellationToken) => {
            var exitCode = await ExecuteAsync(parseResult, cancellationToken);
            return exitCode;
        });
    }

    protected string GetRootPath(ParseResult parseResult) {
        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            return Environment.CurrentDirectory;
        }
        return Path.GetFullPath(rootPath);
    }

    protected bool HasExplicitRoot(ParseResult parseResult) =>
        !string.IsNullOrWhiteSpace(parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption));

    protected static string? TryResolveVSToolsPath(out string? vsRoot) {
        vsRoot = default;
        // Try to resolve VSToolsPath from environment variables or other means
        var ver = Environment.GetEnvironmentVariable("VisualStudioVersion"); // e.g. "17.0"
        vsRoot = Environment.GetEnvironmentVariable("VSINSTALLDIR"); // C:\Program Files\Microsoft Visual Studio\2022\Enterprise\
        if (!string.IsNullOrWhiteSpace(vsRoot) && Directory.Exists(vsRoot)) {
            var toolsPath = Path.Combine(vsRoot, $"MSBuild\\Microsoft\\VisualStudio\\v{ver ?? "17.0"}");
            if (Directory.Exists(toolsPath)) {
                return toolsPath; // Found a valid VSToolsPath
            }
        }

        var paths = MSBuildHelper.GetVS15Locations();
        if (paths is not null && paths.Any()) {
            foreach (var p in paths) {
                vsRoot = p;
                var toolsPath = Path.Combine(p, "MSBuild", "Microsoft", "VisualStudio", $"v{ver ?? "17.0"}");
                if (Directory.Exists(toolsPath)) {
                    return toolsPath; // Found a valid VSToolsPath
                }
            }
        }

        return default;
    }

}
