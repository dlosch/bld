# bld

## dotnet tool available on nuget.org
The code is published as a .NET tool on NuGet.org: https://www.nuget.org/packages/dotnet-bld

### Installation

```
dotnet tool install --global dotnet-bld
```

### Run
```
dotnet bld
```

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

> Note on the target framework: the tool targets `net10.0` (`<TargetFramework>net10.0</TargetFramework>` in `bld/bld.csproj`) and sets `<RollForward>Major</RollForward>`, so it also runs on newer runtimes. (Earlier 0.2.x builds multi-targeted `net8.0;net10.0`; `net8.0` support was dropped.)

## Microsoft.Build & Visual Studio integration

bld leverages the existing dotnet SDK msbuild targets as well as msbuild targets from a Visual Studio installation on the machine, if available. It transparantly locates and uses msbuild assemblies as well as msbuild .targets. It can process project files in current SDK style format as well as old style framework format (it does support processing projects targeting .net Framework even in old project file format). It can also process both recent and old solution file formats (.sln, .slnx, .slnf).

bld fully evaluates project file properties using the Microsoft build system. It *never guesses* where the build output *might* go, it evaluates the project files just as a build would. It extracts the actual artifact and intermediate output paths, package directories ...

## Targeting common dotnet clean pain points ...

**bld clean** is a tool to clean build output folders for (especially .net) MSBuild projects. It can either generate clean scripts (.cmd or .sh) or delete build outputs directly. It is very defensive in what it actually deletes, and never touches any files without explicit consent.

### common pain points 

Your output folders can grow big easily. Even with tooling from the dotnet SDK or msbuild itself, it is not always straightforward to clean (outdated) build output or publishing folders. Creating small proof of concept projects, having agentic coding tools create small projects, or even upgrading the target framework of your projects from .net8.0 to .net10.0 can leave you with obsolete build artifacts which are either not straightforward to delete or which msbuild or dotnet clean won't delete anymore.  

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
- supports both the classic `bin/<Configuration>/<tfm>` layout and the SDK artifacts layout (`UseArtifactsOutput=true`, `artifacts/bin/<project>/<config>_<tfm>`)
- cleans publish and pack output (`PublishDir`, `PackageOutputPath`, `artifacts/publish`, `artifacts/package`) when `--publish` is given
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
| `build-props` | Beta | Trace `Directory.Build.props` files and MSBuild property provenance across projects. |

Commands marked **Beta** may change behavior, arguments, or output formatting.

## Global Options

All commands accept the following shared options unless stated otherwise:

- `--root`, `-r`, or trailing argument — Directory, `.sln`/`.slnx`/`.slnf`, or project file to scan. Defaults to the current working directory.
- `--depth`, `-d` — Directory recursion depth when `--root` is a folder. Default: `3` (max `32`).
- `--log`, `-v`, `--verbosity` — `Debug`, `Verbose`, `Info`, `Warning`, or `Error`. Default: `Warning`.
- `--concurrency` — Degree of parallelism for project evaluation; use `1` for sequential. Default: `max(1, processorCount / 2)`.
- `--markdown`, `-md` — Emit markdown table output where supported. Default: `false`.
- `--vstoolspath`, `-vs` — Explicit `VSToolsPath` for MSBuild evaluation.
- `--novstoolspath`, `-novs` — Skip automatic `VSToolsPath` resolution.

## Stable Commands (Clean & Stats Focus)

### clean

Purpose: enumerate build output, report what would be deleted, and either emit a deletion script (default) or delete the files.

**Options**
- `--non-current`, `--noncurrent`, `-nc` — Restrict deletion to target-framework-specific directories *not* listed in the project’s current TFMs. Default: `false`.
- `--obj`, `-obj` — Include `obj` / `BaseIntermediateOutputPath` directories. Default: `false` (bin-only).
- `--keep-assets` — When cleaning `obj`, preserve NuGet restore artifacts (`project.assets.json`, etc.) and only delete build-output subdirectories. Default: `false`.
- `--publish` — Also clean publish output (`PublishDir`) and pack output (`PackageOutputPath`). Covers explicitly configured publish directories and, in the artifacts layout, `artifacts/publish/<project>/` and `artifacts/package/`. Default: `false`, because publish output is often kept on purpose for a deployment.
- `--output-file`, `-o` — Where to write the deletion script (`clean.cmd` or `clean.sh` by default depending on OS).
- `--delete` — Execute deletions instead of just generating scripts. Default: `false` (dry-run).
- `--force` — Skip confirmation prompts (requires explicit `--root` to avoid accidental repo-wide deletes). In non-interactive contexts (CI / piped stdin) a missing confirmation is treated as "no" (skip), so `--force` is required to actually delete unattended.
- `--confirm` — Intended confirmation granularity for `--delete` (`None`, `Sln`, `Project`, `Directory`; default `Directory`). *Note: not yet wired up — currently only `--force` affects prompting.*
- Global options (`--root`, `--depth`, `--log`, `--concurrency`, `--vstoolspath`, `--novstoolspath`) apply.

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
- `--keep-assets` — With `--obj`, preserve NuGet restore artifacts and only count build-output subdirectories. Default: `false`.
- `--publish` — Include publish output (`PublishDir`) and pack output (`PackageOutputPath`) in the statistics. Default: `false`.
- Shares all global options (`--root`, `--depth`, `--log`, `--concurrency`, `--markdown`, `--vstoolspath`, `--novstoolspath`).

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
- How it works: evaluates projects via MSBuild, parses package references, applies optional whitelist/blacklist categorization, and optionally aggregates with `--aggregate`/`--show-projects`. `GlobalPackageReference` and `PackageDownload` items are listed too, marked `(global)` and `(download)`. With `--transitive` the resolved dependency graph from `project.assets.json` is included.

Helpful when your favorite agent creates your shiny new project but adds a lot of strange nuget package references. Or even your co-worker.

### tfm
- What it does: migrates target frameworks (e.g., `net6.0` → `net8.0`). With `--update-packages` it can also bump `PackageReference` versions to the latest stable release.
- How it works: scans solutions/projects, infers current TFMs, optionally auto-detects target TFM, and rewrites the TFM when `--apply` is set. `--update-packages` is a latest-stable-version bump, not a framework-compatibility check.

Helpful when your favorite agent creates your shiny new project targeting a old version of .NET.

### cpm
- What it does: converts a solution to Central Package Management by creating `Directory.Packages.props` and stripping per-project version attributes.
- How it works: aggregates package versions across projects, resolves conflicts, writes the props file, and updates project files when `--apply` (with optional `--overwrite`).

### outdated
- What it does: lists packages with newer versions and can update them, in whole or in part.
- How it works: queries NuGet feeds for newer versions, respects TFM compatibility unless `--skip-tfm-check`, and applies updates when `--apply` (with optional `--prerelease`). `--max-bump` caps how far a package may move, `--package`/`--exclude` narrow the set by wildcard, and the declared dependency ranges of the selected packages are checked before anything is written.
- (dotnet-outdated is another .NET tool which updates NuGet package versions)

### containerize
- What it does: finds Dockerfiles and SDK-style projects using container build properties.
- How it works: scans the repo (or specific root) and reports Dockerfile paths, project names, or both depending on `--list`, `--projects`, or `--all`.

### build-props
- What it does: shows where MSBuild properties come from across your projects and lists imported `Directory.Build.props` files.
- How it works: evaluates each project and reports property provenance; `--list` shows just the props-file import tree, `--properties` filters to specific properties, and `--no-overridden` hides shadowed values.

## Beta Commands

### nuget (BETA)

- `--whitelist-blacklist-file`, `--wbf` — Path to categorization rules.
- `--aggregate`, `--agg` — Collapse results across projects (aggregate view). Default: `true`. Pass `--aggregate false` for the per-project view.
- `--show-projects`, `--sp` — When aggregating, list referencing projects. Default: `true`.
- `--transitive` — Also list the packages restore resolved through other packages, read from each project's `project.assets.json` (under `MSBuildProjectExtensionsPath`, i.e. `obj/`). Requires a prior `dotnet restore`; a project without the file is reported with a warning and listed with its direct references only. Transitive packages are categorized and matched against the whitelist/blacklist like direct ones and show which packages pull them in. Default: `false`.

Example:

```powershell
bld nuget --root C:\src\MyRepo --aggregate false
bld nuget --root C:\src\MyRepo --transitive --wbf packages.rules
```

### tfm (BETA)

- `--from` — Comma-separated source TFMs (auto-detected when possible).
- `--to` — Target TFM (auto-detected from installed SDKs when omitted).
- `--apply` — Persist changes instead of a dry-run.
- `--update-packages` — With `--apply`, also bump `PackageReference`s to their latest stable version. This is a latest-version bump, not a framework-compatibility check, so it is off by default. Versions are looked up on the sources from the project's `nuget.config` hierarchy (with package source mapping), or nuget.org when none is configured.
- `--update-global-json` — With `--apply`, set `sdk.version` in the governing `global.json` to the highest installed SDK of the target's major (prereleases only when `allowPrerelease` is set). Without `--apply`, report what would change. Only the version line is rewritten; indentation, line endings and BOM are kept.

**global.json.** The command always looks for the `global.json` that governs the input (walking up from its directory, like the SDK does) and warns when its pin cannot build the target framework: a pinned major below the target with any `rollForward` other than `latestMajor` blocks the build, and `major` only rolls forward when the pinned SDK is not installed. Nothing is written without `--update-global-json`.

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
- `--orphaned` — List `PackageVersion` entries in `Directory.Packages.props` that have no matching `PackageReference` and have a newer version on NuGet. Report-only; works for project or solution input.
- `--comment-orphans` — With `--apply`, comment out outdated orphan entries. Only honored for solution input (`.sln`/`.slnx`/`.slnf`), since a single project can't see all CPM consumers. Implies `--orphaned`.
- `--interactive`, `-i` — Pick the packages to update from a grouped list before applying, and pick the target version per package; surfaces dependency version conflicts when you skip a needed package. Implies `--apply`. Requires an interactive terminal — with redirected input the command fails instead of prompting into the void.

  Keys: **up/down** move, **space** toggles the row (or the whole family on a group line), **left/right** move the package between the versions the feeds offer for it — highest patch, highest minor, highest major — or, on a group line, cap the whole family at a bump class (the line shows the class and the arrows that still do something; a package with nothing at or below that class is unchecked, and checked again once the cap admits it), **a**/**n** select all or none, **enter** confirms, **esc** cancels without writing anything. `--max-bump` only decides where each row *starts*; anything the cap held back is in the list too, unchecked, one keypress away.
- `--group-by <prefix|bump|none>` — How `--interactive` groups the list: `prefix` (longest common package id prefix, the default), `bump` (patch/minor/major) or `none` (one flat list). Toggling a group line takes its packages with it, so a patch day is one keystroke per family. Passing the option explicitly also groups the report table (and adds a `Group` column to `--markdown`); without it the report is unchanged.
- `--group-depth <n>` — Maximum prefix length in dot-separated segments for `--group-by prefix`. Default: `2`, so `Microsoft.Extensions.*` is a group but `Microsoft.Extensions.Logging.*` is not.
- `--group-min <n>` — Smallest number of packages a prefix group must have. Default: `2`. Packages left over land in `(other)`.
- `--preselect <all|no-major|patch|none>` — Which rows `--interactive` starts out with a check mark on. Default: `all`, as before. Each row shows its bump class colored (green `patch`, yellow `minor`, red `MAJOR`), so `no-major` makes "no breaking updates today" one keystroke per exception.
- `--max-bump <major|minor|patch>` — Largest version step to propose, relative to the version a package is pinned at now: `major` (no cap, the default), `minor` (same major, so no breaking change per SemVer) or `patch` (same major and minor).
- `--max-bump-for "<pattern>=<major|minor|patch>"` — Override `--max-bump` for the packages matching a pattern. Same wildcard syntax as `--package`, may be repeated, accepts `;`-separated lists; the most specific matching pattern wins and ties go to the one given last.
- `--package <pattern>`, `-p` — Only consider packages whose id matches one of these patterns. Supports `*` wildcards, is case-insensitive, may be repeated, and accepts `;`-separated lists. Default: all packages.
- `--exclude <pattern>` — Skip packages whose id matches one of these patterns. Same syntax as `--package` and applied after it.
- `--allow-conflicts` — Update packages even when a dependency they require stays at a version that does not satisfy their declared range. Without this, such packages are held back.
- `--verify-restore` — After `--apply`, run `dotnet restore` on the input and fail the command when NuGet reports errors.
- `--source <name|url>` — Package source(s) to query: the name of a source in `nuget.config` or a v3 feed URL. May be repeated. Overrides the configuration, including package source mapping.
- `--ignore-source-mapping` — Query every enabled source for every package instead of honoring the `packageSourceMapping` section.
- `--eval-cache` — Skip the MSBuild evaluation of a project configuration when nothing that fed its last evaluation has changed. An entry stores the extracted package and project references together with a SHA-256 of the project file and every import outside the SDK (`Directory.Build.props`, `Directory.Packages.props`, restore-generated props, package build files); the key also covers the global properties, the MSBuild version and which `Directory.Build.*`/`Directory.Packages.props` files exist between the project and the root, so a newly added one invalidates the entry. Entries live under `$BLD_HOME/cache/eval` (default: `%LOCALAPPDATA%\bld` on Windows, `~/.local/share/bld` on Linux) and are safe to delete. Opt-in because of what it cannot see: MSBuild properties coming from environment variables, `Exists()` conditions on files that are not imported, and changes inside the SDK or a Visual Studio installation at the same version. The run reports how many configurations came from the cache.

**Item types.** Besides `PackageReference` (inline, `VersionOverride`, or centrally managed), `outdated` checks `GlobalPackageReference` entries in `Directory.Packages.props` (updated in place there) and `PackageDownload` items (compared by their highest bracketed version, updated as `[x.y.z]`, and exempt from the TFM check because a download is never referenced). A `PackageVersion` that only a `GlobalPackageReference` uses is not an orphan.

**Package sources.** `outdated` reads the `nuget.config` hierarchy as NuGet does, starting from the directory of the input (repo config, user config, machine config): enabled sources, `packageSourceMapping`, and `packageSourceCredentials` (clear-text or `%ENV_VAR%` references; credentials are sent as Basic auth, which is what Azure Artifacts and GitHub Packages expect for a PAT). Each source's service index is fetched once to find its registration endpoint. When several sources may serve a package, the highest version wins. A source that is unreachable, a v2 feed, or a local directory is skipped for the run with a warning. A package that source mapping assigns to no source is skipped with a warning and does not affect the exit code. Without any configured source, nuget.org is used.

Examples:

```powershell
bld outdated --root C:\src\MyRepo --prerelease
bld outdated --root C:\src\MyRepo --source internal --source nuget.org
bld outdated --root C:\src\MyRepo --max-bump minor --apply
bld outdated --root C:\src\MyRepo -p "Serilog.*" -p "xunit*" --exclude "Serilog.Sinks.Seq" --max-bump minor --apply
bld outdated MyRepo.slnx --apply --max-bump minor --max-bump-for "Microsoft.Extensions.*=patch" --max-bump-for "Serilog*=major"
```

The **held** column names the newest version that `--max-bump` refused, so a capped run does not read as "up to date". A package that has nothing but a held-back version is listed with `current` equal to `latest`, and `--apply` leaves it untouched. In `--interactive` it is a row like any other, unchecked, reachable with **left/right** — so capping the run at `minor` and still taking one specific major is a keypress, not a second run.

Every version the picker offers has passed the same listing, prerelease and target framework checks as the default target, because one lookup per package collects the highest compatible version in each bump class instead of stopping at the first usable one.

After the selection is settled, `--interactive` prints the command line that reproduces it without any prompt — a fully selected prefix group collapses into a single `-p "<prefix>.*"`, and any target above the cap becomes a `--max-bump-for <id>=<level>`. It reproduces the *selection*, not necessarily the result: a later run can see newer versions than this one did.

Before writing, the command checks the packages it is about to update in both directions: a package whose new version requires a dependency at a version it will not end up at is held back, and so is a package whose new version falls outside the range that another reference staying where it is declares on it (`A 9.0.1 breaks Q 3.1.0, which requires A [8.0.0, 9.0.0)`). In `--interactive` the second case offers to include the blocking package's own update instead. The check is a heuristic that sees **direct references only**: transitive chains are outside its reach, and a pinned version the feed no longer lists has no manifest to check, so `--verify-restore` is the only complete answer. Held-back packages are a normal outcome and do not change the exit code; failures to analyze a project, look up a package, or restore do.

### containerize (BETA)

- `--list`, `-l` — Show file paths only. Default: `false`.
- `--projects`, `-p` — Scan for SDK-style container projects. Default: `false`.
- `--all`, `-a` — Scan Dockerfiles and container projects together.

Example:

```powershell
bld containerize --root C:\src\MyRepo --all --depth 5
```

### build-props (BETA)

- `--list`, `-l` — Only list imported `Directory.Build.props` files as a tree, without property contents. Default: `false`.
- `--properties` — Comma-separated properties to trace (e.g. `TargetFramework,Nullable`); when set, only these are shown.
- `--no-overridden` — Hide properties that originate from `Directory.Build.props` but are overridden later in evaluation. Default: `false`.

Example:

```powershell
bld build-props --root C:\src\MyRepo --properties TargetFramework,LangVersion
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
- **Marking logic**: `MarkDeleteProcessor` collects bin/obj candidates, deduplicates directories shared across configurations, and refuses to touch paths that look like project roots or nested solutions. When `--non-current` is set, TFM directories matching the project’s declared TFMs are skipped. Projects with `UseArtifactsOutput=true` are handled through `artifacts/bin/<project>/`, where every subdirectory named `<config>[_<tfm>][_<rid>]` is a candidate. With `--publish`, `PublishDir` and `PackageOutputPath` are added in a second pass and dropped when they already sit inside a marked build-output directory (the default case). An `OutDir` that matches none of the known layouts is reported as a warning instead of being skipped silently.
- **Stats vs clean**:
  - `stats` hands results to `MarkDeleteResultStatsProcessor`, which enumerates files (depth-limited) to compute counts and KiB/MiB totals without creating any output files.
  - `clean` hands results to either `MarkDeleteResultBatchFileProcessor` (default) or `MarkDeleteResultDeleteProcessor` when `--delete` is set. The batch processor writes platform-specific scripts (respecting `--output-file`) and prints a table. The delete processor prompts per directory unless `--force` is used.
- **Safety rails**: depth defaults to 3, `--force` requires an explicit `--root`, and the tool stops silently when nothing is marked. Errors are aggregated via `ErrorSink` and printed after processing.
- **Other commands** reuse the same MSBuild initialization and scanning primitives, layering command-specific processors (NuGet analysis, TFM migration with NuGet metadata checks, CPM rewrite helpers, outdated package lookups, and Dockerfile/project scanners).

---

`bld` is developed for repositories that accumulate large volumes of build output. Review scripts before executing deletions, especially when using `--delete`.
