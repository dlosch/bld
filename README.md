# bld

`bld` utility for working with .NET/msbuild project files and solutions. 
- clean build output
- list nuget packages
- list and update tfm
- enable central package management
- scan and update outdated nuget package versions
- scan for docker base image references

Some of this can be handy when working with agentic coding tools which at this stage may have an unconventional approach to tfms, central package management, nuget package references and versions.

> 💡 **Fun fact**: `bld` was born from the frustration of watching gigabytes of build artifacts pile up faster than dependencies in a JavaScript project. Because sometimes you need a tool that speaks fluent MSBuild and knows when to take out the trash. 🗑️

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

## Stable Commands

### clean

- `--non-current`, `--noncurrent`, `-nc` — Only target frameworks no longer referenced. Default: `false`.
- `--obj`, `-obj` — Include `obj` directories. Default: `false`.
- `--output-file`, `-o` — Where to write the deletion script (`clean.cmd` or `clean.sh` by default).
- `--delete` — Execute deletions instead of just generating scripts. Default: `false`.
- `--force` — Skip confirmation prompts (requires explicit `--root`).

Example:

```powershell
bld clean --root C:\src\MyRepo --depth 4 --obj
```

### stats

- `--non-current`, `--noncurrent`, `-nc` — Only report non-current TFMs. Default: `false`.
- `--obj`, `-obj` — Include `obj` directories in the statistics. Default: `false`.

Example:

```powershell
bld stats --root MySolution.sln --non-current
```

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

---

`bld` is developed for repositories that accumulate large volumes of build output. Review scripts before executing deletions, especially when using `--delete`.
