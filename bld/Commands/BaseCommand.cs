using bld.Infrastructure;
using bld.Models;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal abstract class BaseCommand : Command {
    protected IConsoleOutput Console { get; set; }
    protected BaseCommand(string name, string? description, IConsoleOutput console) : base(name, description) {
        Console = console;

        SetAction(async (parseResult, cancellationToken) => {
            var exitCode = await ExecuteAsync(parseResult, cancellationToken);
            return exitCode;
        });
    }

    protected virtual Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        return Task.FromResult(0);
    }

    //protected readonly Option<string> _logFileOption = new Option<string>("--log-file", "-lf") {
    //    Description = "Path to the log file (not implemented).",
    //    DefaultValueFactory = _ => "clean.log"
    //};

    protected readonly Option<string?> _vsToolsPath = new Option<string?>("--vstoolspath", "-vs") {
        Description = "Explicit value for VSToolsPath. If not specified, the tool tries to resolve VSToolsPath either from environment variable or via vswhere.exe. VSToolsPath contains additional msbuild target files which may be required for evaluation of project files.",
        DefaultValueFactory = _ => null,
    };

    protected readonly Option<bool> _noResolveVsToolsPath = new Option<bool>("--novstoolspath", "-novs") {
        Description = "Do not try to resolve VSToolsPath from ENV or vswhere.exe.",
        DefaultValueFactory = _ => false,
    };

    protected readonly Option<bool> _nonCurrentOption = new Option<bool>("--non-current", "--noncurrent", "-nc") {
        Description = "Only clean directories for non-current target frameworks.",
        DefaultValueFactory = _ => false
    };

    protected readonly Option<bool> _objOption = new Option<bool>("--obj", "-obj") {
        Description = "Also clean BaseIntermediateOutputPath (obj folder).",
        DefaultValueFactory = _ => false
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
        Description = "Root directory or .sln. Can also be specified as trailing argument.",
        Validators = {
            RootPathValidator
        }
    };

    protected readonly Option<int> _depthOption = new Option<int>("--depth", "-d") {
        Description = "If root is a directory, recursion depth to search for .sln.",
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
        DefaultValueFactory = _ => LogLevel.Info
    };

    protected readonly Option<bool> _nupkgOption = new Option<bool>("--nupkg") {
        Description = "(Currently implicit via CleanAll) Also delete produced .nupkg artifacts.",
        DefaultValueFactory = _ => false
    };

    protected readonly Option<bool> _jsonOption = new Option<bool>("--json") {
        Description = "Output results in JSON format for machine readability.",
        DefaultValueFactory = _ => false
    };

    protected readonly Argument<string?> _rootArgument = new Argument<string?>("root") {
        Arity = ArgumentArity.ZeroOrOne,
        Validators = {
            RootPathValidator
        }
    };

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