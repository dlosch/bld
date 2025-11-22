# bld (BETA)

This repository is in BETA. Features, commands, and behavior are experimental and may change or be removed without notice.

bld is a command-line tool to clean build output folders for (especially .NET) MSBuild projects.

## What it does

- Traverse directories looking for .sln, .slnx, .slnf
- Process all configurations from solution files
- Evaluate MSBuild properties in-process for each project/configuration
- Resolve a default MSBuild installation and optionally VSToolsPath (from Visual Studio)
- Optionally delete only non-current build outputs (TFMs no longer referenced)
- Validate TFMs for .NET projects to avoid incorrect deletions
- Dry-run by default: produces stats and an OS-specific deletion script. Nothing is deleted unless `--delete` is specified
- Basic Linux support

## Commands

Note: Commands marked (BETA) are experimental. Only `clean` and `stats` are considered stable for now.

- clean — Evaluate solutions/projects and produce a summary and an OS-specific deletion script (dry-run by default). Use `--delete` to actually remove files. (stable)
- stats — Compute and print cleaning statistics only (no deletion, no deletion script). Useful to preview impact. (stable)
- nuget (BETA) — Analyze NuGet packages and dependencies
- containerize (BETA) — Prepare project for containerization
- cpm (BETA) — Central Package Management helpers
- outdated (BETA) — Find outdated packages and optionally update them
- cleanup (BETA) — Additional cleanup helpers
- tfm (BETA) — Target framework management

Commands marked as BETA are experimental and may change or be removed in future releases.

Note:
- global.json ... doe to the consistent /s way msbuild, dotnet msbuild, and dotnet build handle global.json ... 


## bld (dotnet tool) Commands

| Command | Description |
|---|---|
| clean | Evaluate solutions/projects and produce a summary and an OS-specific deletion script (dry-run by default). Use `--delete` to actually remove files. |
| stats | Compute and print cleaning statistics only (no deletion and no deletion script). Useful to preview impact. |
| containerize | Analyze and display information about Dockerfiles and .NET container projects. Searches for Dockerfiles and projects with SDK container build properties (PublishProfile=DefaultContainer, EnableSdkContainerSupport), showing base images, container families, registries, and configuration details. |

## Examples

Generate a deletion script (dry-run):

```text
bld clean --root <rootDir> --depth 3 -o clean.cmd
```

Show only statistics (no script):

```text
bld stats --root <rootDir> --depth 2 --non-current --obj
```

Run and actually delete (use with care):

```text
bld clean --root <rootDir> --delete [--force]
```

Analyze NuGet packages (BETA):
## Options (current defaults & meanings)
```text
bld nuget --root <rootDir> --depth 2 --whitelist-blacklist-file rules.txt
```

Analyze Dockerfiles in a project:

```text
bld containerize --root <rootDir> --depth 3
```

Scan for .NET projects with container build properties:

```text
bld containerize --projects --root <rootDir>
```

Scan for both Dockerfiles and container projects:

```text
bld containerize --all --root <rootDir>
```

List files only (without parsing details):

```text
bld containerize --list --root <rootDir>
```

Convert projects to Central Package Management (dry-run):

```text
bld cpm --root <rootDir> --dry-run
```

Check outdated packages (no changes):

```text
bld outdated --root <rootDir> --prerelease
```

Cleanup helpers (BETA) — usage varies by subcommand:

```text
bld cleanup --help
```

TFM management (BETA):

```text
bld tfm --help
```

## Options (global and per-command)

Global options (available to most commands):

- `--root`, `-r` (string) — Root directory or a `.sln` path. Default: current working directory (or trailing argument)
- `--depth`, `-d` (int) — Recursion depth to search for solution files when `--root` is a directory. Default: 3
- `--log`, `-v`, `--verbosity` (LogLevel) — Log verbosity: Debug, Verbose, Info, Warning, Error. Default: Warning
- `--vstoolspath`, `-vs` (string) — Explicit VSToolsPath for MSBuild evaluation; if omitted the tool may try to resolve it from Visual Studio instances
- `--novstoolspath`, `-novs` (bool) — Do not try to auto-resolve VSToolsPath from environment or vswhere. Default: false

clean (stable) options:

- `--non-current`, `--noncurrent`, `-nc` (bool) — Only consider output for TFMs no longer referenced in the project. Default: false
- `--obj`, `-obj` (bool) — Also consider BaseIntermediateOutputPath (obj folder) for cleaning. Default: true
- `--output-file`, `-o` (string) — Path to write the deletion script (batch file or shell commands depending on OS). Default: `clean.cmd` on Windows or `clean.sh` on Unix
- `--delete` (bool) — Perform deletions instead of a dry-run. Default: false
- `--force` (bool) — Do not ask for confirmation (requires explicit root). Default: false

stats (stable) options:

- `--non-current`, `--noncurrent`, `-nc` (bool) — Report only non-current TFMs. Default: false
- `--obj`, `-obj` (bool) — Include obj folders in statistics. Default: true

nuget (BETA) options:

- `--whitelist-blacklist-file`, `--wbf` (string) — Path to a whitelist/blacklist file for categorization rules
- Global options apply as well (root, depth, vstoolspath, etc.)

containerize (BETA) options:

- `--update`, `-u` (bool) — Apply changes to project files. Default: false (dry-run)
- Global options apply as well

cpm (BETA) options:

- `--dry-run` (bool) — Show what would be changed without modifying files. Default: true
- `--force` (bool) — Apply changes to create/modify Directory.Packages.props and update project files. Default: false
- `--overwrite` (bool) — Overwrite existing Directory.Packages.props if it exists. Default: false

outdated (BETA) options:

- `--update`, `-u` (bool) — Update packages to their latest versions instead of just checking. Default: false
- `--skip-tfm-check` (bool) — Skip target framework compatibility checks when suggesting updates. Default: false
- `--prerelease` (bool) — Include prerelease versions of NuGet packages. Default: false

cleanup (BETA) and tfm (BETA):

- These commands include subcommands and options. Run `bld cleanup --help` or `bld tfm --help` for details.

## Notes / Caveats

- MSBuild evaluation can be slow for large repos because the tool evaluates per project/configuration to compute accurate paths. This is deliberate to be correct rather than fast.
- MSBuild property evaluation may fail for misconfigured projects — such projects are reported and skipped.

## Installing as a dotnet tool

Example:

```powershell
dotnet tool install -g <package>
bld clean --help
```

For a local install use `dotnet tool install --local <package>` in a folder with a tool manifest.

---

This tool performs in-process MSBuild evaluation (no external -getproperty calls). Use with care when running deletion operations.
