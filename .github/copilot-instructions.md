# bld AI Coding Instructions

Project `bld` is a .NET CLI tool for managing MSBuild projects (cleaning, NuGet analysis, TFM migration, CPM conversion).

## Architecture & Patterns

- **CLI Framework**: Uses `System.CommandLine`. All commands inherit from `BaseCommand` in [bld/Commands/BaseCommand.cs](../bld/Commands/BaseCommand.cs), which provides shared options like `--root`, `--depth`, and `--log`.
- **Orchestration**: Commands delegate logic to "Application" or "Service" classes in [bld/Services/](../bld/Services/) (e.g., `CleaningApplication`, `NugetAnalysisApplication`).
- **MSBuild Interaction**: Core logic for project evaluation resides in [bld/Infrastructure/ProjParser.cs](../bld/Infrastructure/ProjParser.cs) and [bld/Services/MSBuildService.cs](../bld/Services/MSBuildService.cs).
- **Console Output**: Always use `IConsoleOutput` (implemented by `SpectreConsoleOutput`) for user interaction. Avoid `Console.WriteLine`.
- **Error Handling**: Use `ErrorSink` to collect non-fatal errors during batch processing and report them at the end.

## Critical Workflows

- **Build**: Use `dotnet build bld.slnx`. Note the `.slnx` format.
- **Run**: `dotnet run --project bld -- [args]`.
- **Test**: `dotnet test`. Tests are located in [bld.Tests/](../bld.Tests/).

## Key Conventions

- **MSBuild Initialization**: `MSBuildService.RegisterMSBuildDefaults` MUST be called before any MSBuild types are loaded. This is typically handled in the `InitAsync` method of application classes.
- **Project Evaluation**: Projects are evaluated using `Microsoft.Build.Evaluation.Project`. Global properties like `Configuration`, `Platform`, and `VSToolsPath` are passed to ensure correct evaluation.
- **Solution Scanning**: Use `SlnScanner` to find `.sln` or `.slnx` files within a directory tree, respecting the `--depth` option.
- **Dependency Management**: Uses Central Package Management (CPM). Update versions in [Directory.Packages.props](../Directory.Packages.props).

## Examples

### Adding a New Command
1. Create a class inheriting from `BaseCommand` in [bld/Commands/](../bld/Commands/).
2. Register it in [bld/Commands/RootCommand.cs](../bld/Commands/RootCommand.cs).
3. Implement logic in a new service class in [bld/Services/](../bld/Services/).

### Evaluating Project Properties
```csharp
var properties = projParser.LoadProject(projCfg, ProjConstants.PropertyNames);
var tfm = properties["TargetFramework"];
```

### Reporting Errors
```csharp
errorSink.Add(projCfg.Path, "Failed to evaluate project.");
```
