# bld

`bld` is a utility for working with .NET/MSBuild project files and solutions. It does rely on the Microsoft Build system (Microsoft.Build and NuGet assemblies) for proper property evaluation of msbuild project files (*.csproj ...). It is intentionally small and focuses on repository hygiene:
- Clean build output safely.
- Inspect cleaning statistics without touching the disk.
- List NuGet package references.
- List and update TFMs (target frameworks of your projects).
- Enable Central Package Management.
- Scan and update outdated NuGet package versions.
- Scan for Docker base image references.

This is especially handy when working with agentic coding tools or large repos where TFMs, CPM, package references, and build outputs can drift.

## Microsoft.Build & Visual Studio integration

bld leverages the existing dotnet SDK msbuild targets as well as msbuild targets from a Visual Studio installation on the machine, if available. It transparantly locates and uses msbuild assemblies as well as msbuild .targets. It can process project files in current SDK style format as well as old style framework format (it does support processing projects targeting .net Framework even in old project file format). It can also process both recent and old solution file formats (.sln, .slnx, .slnf).

bld fully evaluates project file properties using the Micrsoft build system. It never guesses where the build output *might* go, it evaluates the project files just as a build would. It extracts the actual artifact and intermediate output paths, package directories ...

## Targeting common dotnet clean pain points ...

**bld clean** is a tool to clean build output folders for (especially .net) MSBuild projects. It can either generate clean scripts (.cmd or .sh) or delete build outputs directly. It is very defensive in what it actually deletes, and never touches any files without explicit consent.

### common pain points 

Your output folders can grow big easily. Even with tooling from the dotnet SDK or msbuild itself, it is not always straightforward to clean (outdated) build output or publishing folders. Creating small proof of concept projects, having agentic coding tools create small projects, or even upgrading the target framework of your projects from .net8.0 to .net10.0 can leave you with obsolete build artifacts which dotnet clean won't delete anymore.  

### what does it do?

It cleans build output, publishing and intermediate folders.

Yes, you can use **dotnet clean** or **msbuild /t:clean** to clean build output from your solutions ... 

However, these tools ... well these
- don't clean old build targets (after migrating from net8.0 to net9.0, net8.0 output doesn't get cleaned)
- don't delete default publishing folders (which can be huge)
- don't delete intermediate build folders (obj)
- dotnet clean can have limitations cleaning older framework-style projects

Yes, you can just use git/source control to nuke anything not under source control
- not all projects are under git/source control
- if the build output isn't below the repo, this doesn't work (dotnet\runtime)

### what this tool does
- traverse directories looking for .sln, .slnx, .slnf
- process all configurations from the solution files
- in process evaluation of properties for each project and configuration using the Microsoft build assemblies and target files, just as a build would (note: the Microsoft.Build evaluation is *not* instant)
- automatically resolves default msbuild install (typically .NET SDK) and resolves VSToolsPath for additional target files provided by Visual Studio installations (if available, not required)
- enables you to delete only non-current build output (TagetFramework(s) no longer referenced in proj file), esp. useful after upgrading projects to a recent target framework
- validates tfms for .net projects to make sure the correct stuff gets cleaned
- by default doesn't delete, only dumps stats and the command line to delete folders. Nothing gets touched unless you specify --delete
- support for linux
- defensive approach in determining what to delete
- never deletes or changes files without explicit consent

Note:
- global.json ... due to the consistent /s way msbuild, dotnet msbuild, and dotnet build handle global.json ... 

## Quick Start

```powershell
dotnet build bld.sln
dotnet run --project bld -- clean --root <root-or-sln>
```

Use `--delete` only after you have reviewed the generated script or statistics.

## Command Overview

| Command | Stability | Purpose |
| --- | --- | --- |
| `clean` | Stable | Evaluate projects, report disk usage, and emit an OS-specific deletion script (dry-run by default). |
| `stats` | Stable | Print cleaning statistics only; never writes scripts or deletes files. |
| `nuget` | Beta | Inspect NuGet dependencies and optionally aggregate package usage. |
| `tfm` | Beta | Migrate project target frameworks. |
| `cpm` | Beta | Convert a solution to Central Package Management. |
| `outdated` | Beta | Check (and optionally update) NuGet packages to newer versions. |
| `containerize` | Beta | Discover Dockerfiles and projects using SDK container build properties. |

Commands marked **Beta** may change behavior, arguments, or output formatting.

## Global Options

All commands accept the following shared options unless stated otherwise:

- `--root`, `-r`, or trailing argument — Directory or `.sln` to scan. Defaults to the current working directory.
- `--depth`, `-d` — Directory recursion depth when `--root` is a folder. Default: `3`.
- `--log`, `-v`, `--verbosity` — `Debug`, `Verbose`, `Info`, `Warning`, or `Error`. Default: `Info`.
- `--vstoolspath`, `-vs` — Explicit `VSToolsPath` for MSBuild evaluation.
- `--novstoolspath`, `-novs` — Skip automatic `VSToolsPath` resolution.

## Stable Commands (Clean & Stats Focus)

### clean

Purpose: enumerate build output, report what would be deleted, and either emit a deletion script (default) or delete the files.

**Options**
- `--non-current`, `--noncurrent`, `-nc` — Restrict deletion to target-framework-specific directories *not* listed in the project’s current TFMs. Default: `false`.
- `--obj`, `-obj` — Include `obj` / `BaseIntermediateOutputPath` directories. Default: `false` (bin-only).
- `--output-file`, `-o` — Where to write the deletion script (`clean.cmd` or `clean.sh` by default depending on OS).
- `--delete` — Execute deletions instead of just generating scripts. Default: `false` (dry-run).
- `--force` — Skip confirmation prompts (requires explicit `--root` to avoid accidental repo-wide deletes).
- Global options (`--root`, `--depth`, `--log`, `--vstoolspath`, `--novstoolspath`) apply.

**Behavior**
1. Resolves the root (directory or solution) and recursion depth, then resolves `VSToolsPath` unless `--novstoolspath` is set.
2. Enumerates solutions/projects, evaluates MSBuild properties per configuration, and locates `bin`/`obj` output directories.
3. Marks candidate directories, honoring:
   - `--non-current` to only select TFM folders that are no longer referenced.
   - Safety checks that avoid touching project roots or nested solutions.
4. Default dry-run writes an OS-specific script to `--output-file` (or prints to console) with sizes and file counts.
5. With `--delete`, the tool prompts per directory (or skips prompts with `--force`) and removes directories immediately.

**Example**

```powershell
bld clean --root C:\src\MyRepo --depth 4 --obj
```

### stats

Purpose: compute what *would* be cleaned and show totals without generating scripts or deleting anything.

**Options**
- `--non-current`, `--noncurrent`, `-nc` — Only report TFM directories that no longer match current project TFMs. Default: `false`.
- `--obj`, `-obj` — Include `obj` directories in the statistics. Default: `false`.
- Shares all global options (`--root`, `--depth`, `--log`, `--vstoolspath`, `--novstoolspath`).

**Behavior**
1. Uses the same discovery and safety logic as `clean` but never writes files or deletes anything.
2. Produces a table with file counts, KiB/MiB sizes, and TFM hints for each marked directory plus a total row.
3. Reports “No directories marked for deletion” when nothing qualifies, making it safe to run in automation.

**Example**

```powershell
bld stats --root MySolution.sln --non-current
```

## Other Commands (Beta, short overviews)

### nuget
- What it does: analyzes NuGet `PackageReference` usage across projects; can aggregate to a solution-wide view.
- How it works: evaluates projects via MSBuild, parses package references, applies optional whitelist/blacklist categorization, and optionally aggregates with `--aggregate`/`--show-projects`.

Helpful when your favorite agent creates your shiny new project but adds a lot of strange nuget package references. Or even your co-worker.

### tfm
- What it does: migrates target frameworks (e.g., `net6.0` → `net8.0`) and can update package versions for compatibility.
- How it works: scans solutions/projects, infers current TFMs, optionally auto-detects target TFM, evaluates compatibility via NuGet, and applies changes when `--apply` is set.

Helpful when your favorite agent creates your shiny new project targeting a old version of .NET.

### cpm
- What it does: converts a solution to Central Package Management by creating `Directory.Packages.props` and stripping per-project version attributes.
- How it works: aggregates package versions across projects, resolves conflicts, writes the props file, and updates project files when `--apply` (with optional `--overwrite`).

### outdated
- What it does: lists packages with newer versions and can update them.
- How it works: queries NuGet feeds for newer versions, respects TFM compatibility unless `--skip-tfm-check`, and applies updates when `--apply` (with optional `--prerelease`).
- (dotnet-outdated is another .NET tool which updates NuGet package versions)

### containerize
- What it does: finds Dockerfiles and SDK-style projects using container build properties.
- How it works: scans the repo (or specific root) and reports Dockerfile paths, project names, or both depending on `--list`, `--projects`, or `--all`.

## Beta Commands

### nuget (BETA)

- `--whitelist-blacklist-file`, `--wbf` — Path to categorization rules.
- `--aggregate`, `--agg` — Collapse results across projects. Default: `false`.
- `--show-projects`, `--sp` — When aggregating, list referencing projects. Default: `true`.

Example:

```powershell
bld nuget --root C:\src\MyRepo --aggregate
```

### tfm (BETA)

- `--from` — Comma-separated source TFMs (auto-detected when possible).
- `--to` — Target TFM (auto-detected from installed SDKs when omitted).
- `--apply` — Persist changes instead of a dry-run.

Example:

```powershell
bld tfm --root MySolution.sln --to net9.0 --apply
```

The command will scan for consistent TFMs, infer .NET SDK versions, and report conflicts before applying changes.

### cpm (BETA)

- `--apply` — Modify projects and create `Directory.Packages.props`. Default: dry-run.
- `--overwrite` — Replace an existing `Directory.Packages.props`.

Example:

```powershell
bld cpm --root MySolution.sln --apply --overwrite
```

### outdated (BETA)

- `--apply` — Update packages in-place. Default: dry-run/report only.
- `--skip-tfm-check` — Ignore target framework compatibility checks.
- `--prerelease`, `--pre` — Consider prerelease package versions.

Example:

```powershell
bld outdated --root C:\src\MyRepo --prerelease
```

### containerize (BETA)

- `--list`, `-l` — Show file paths only. Default: `false`.
- `--projects`, `-p` — Scan for SDK-style container projects. Default: `false`.
- `--all`, `-a` — Scan Dockerfiles and container projects together.

Example:

```powershell
bld containerize --root C:\src\MyRepo --all --depth 5
```

## Typical Workflows

- Generate a deletion script for inspection:
	```powershell
	bld clean --root C:\src\MyRepo --depth 3 -o clean.cmd
	```
- Preview disk impact only:
	```powershell
	bld stats --root MySolution.sln --obj --non-current
	```
- Audit NuGet usage across solutions:
	```powershell
	bld nuget --root C:\src\MyRepo --aggregate --show-projects
	```
- List Dockerfiles without parsing:
	```powershell
	bld containerize --root . --list
	```

## Notes & Caveats

- MSBuild evaluation happens in-process; evaluation failures are reported but do not abort the run.
- Auto-resolving MSBuild toolsets may require Visual Studio or the .NET SDK to be installed.
- Beta commands surface rich diagnostics but are still evolving; file issues with exact command lines and logs when something looks off.

## Detailed internals (clean & stats)

- **Discovery pipeline**: `SlnScanner` finds solutions under `--root`/`--depth`, `SlnParser` enumerates project configs, and `ProjParser` evaluates MSBuild properties (OutDir, BaseIntermediateOutputPath, TFMs). `VSToolsPath` is resolved automatically unless `--novstoolspath` is specified.
- **Marking logic**: `MarkDeleteProcessor` collects bin/obj candidates, deduplicates directories shared across configurations, and refuses to touch paths that look like project roots or nested solutions. When `--non-current` is set, TFM directories matching the project’s declared TFMs are skipped.
- **Stats vs clean**:
  - `stats` hands results to `MarkDeleteResultStatsProcessor`, which enumerates files (depth-limited) to compute counts and KiB/MiB totals without creating any output files.
  - `clean` hands results to either `MarkDeleteResultBatchFileProcessor` (default) or `MarkDeleteResultDeleteProcessor` when `--delete` is set. The batch processor writes platform-specific scripts (respecting `--output-file`) and prints a table. The delete processor prompts per directory unless `--force` is used.
- **Safety rails**: depth defaults to 3, `--force` requires an explicit `--root`, and the tool stops silently when nothing is marked. Errors are aggregated via `ErrorSink` and printed after processing.
- **Other commands** reuse the same MSBuild initialization and scanning primitives, layering command-specific processors (NuGet analysis, TFM migration with NuGet metadata checks, CPM rewrite helpers, outdated package lookups, and Dockerfile/project scanners).

---

`bld` is developed for repositories that accumulate large volumes of build output. Review scripts before executing deletions, especially when using `--delete`.
