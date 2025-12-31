# bld

A .NET tool for working with MSBuild project files and solutions. Built to help manage the chaos of modern .NET development.

[![NuGet](https://img.shields.io/nuget/v/Cloudsiders.bld.svg)](https://www.nuget.org/packages/Cloudsiders.bld/)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)

## Features

- **Clean build output** — Reclaim disk space from bin/obj directories
- **NuGet package analysis** — Audit and categorize package dependencies across projects
- **Target framework migration** — Upgrade projects from one TFM to another (e.g., net8.0 → net9.0)
- **Central Package Management** — Convert solutions to use Directory.Packages.props
- **Outdated package detection** — Find and update stale NuGet packages
- **Container discovery** — Find Dockerfiles and .NET SDK container configurations

Particularly useful when working with AI coding assistants that may have... *creative* approaches to target frameworks and package management.

## Installation

### As a .NET Global Tool

```bash
dotnet tool install --global Cloudsiders.bld
```

### As a .NET Local Tool

```bash
dotnet new tool-manifest  # if you don't have one
dotnet tool install Cloudsiders.bld
```

### From Source

```bash
git clone https://github.com/dlosch/bld.git
cd bld
dotnet build bld.slnx
dotnet run --project bld -- --help
```

## Quick Start

```bash
# See what disk space bin/obj folders are using
bld stats --root /path/to/repo

# Generate a cleanup script (dry-run by default)
bld clean --root /path/to/repo

# Analyze NuGet packages across all projects (aggregated by default)
bld nuget --root /path/to/repo

# Check for outdated packages
bld outdated --root /path/to/repo
```

## Commands

| Command | Stability | Purpose |
|---------|-----------|---------|
| `clean` | **Stable** | Evaluate projects, report disk usage, emit OS-specific deletion script |
| `stats` | **Stable** | Print cleaning statistics without writing scripts or deleting files |
| `nuget` | Beta | Analyze NuGet dependencies with categorization and aggregation |
| `tfm` | Beta | Migrate project target frameworks |
| `cpm` | Beta | Convert solutions to Central Package Management |
| `outdated` | Beta | Check and update NuGet packages to newer versions |
| `containerize` | Beta | Discover Dockerfiles and SDK container configurations |
| `mcp` | Beta | Start Model Context Protocol (MCP) server for agentic workflows |

## Global Options

All commands support these options:

| Option | Aliases | Description | Default |
|--------|---------|-------------|---------|
| `--root` | `-r` | Directory or `.sln` to scan | Current directory |
| `--depth` | `-d` | Directory recursion depth | 3 |
| `--log` | `-v`, `--verbosity` | Log level: Debug, Verbose, Info, Warning, Error | Info |
| `--json` | | Output results in JSON format for machine readability | false |
| `--vstoolspath` | `-vs` | Explicit VSToolsPath for MSBuild | Auto-detected |
| `--novstoolspath` | `-novs` | Skip VSToolsPath auto-resolution | false |

## Command Reference

### clean

Evaluate projects and generate platform-specific deletion scripts for build output directories.

```bash
# Basic usage - generates clean.cmd or clean.sh
bld clean --root /path/to/repo

# Include obj directories
bld clean --root /path/to/repo --obj

# Only target non-current TFMs (e.g., net7.0 when project targets net8.0)
bld clean --root /path/to/repo --non-current

# Actually delete files (use with caution!)
bld clean --root /path/to/repo --delete
```

**Options:**
- `--non-current`, `-nc` — Only target non-current TFMs
- `--obj` — Include obj directories
- `--output-file`, `-o` — Output script path (default: clean.cmd/clean.sh)
- `--delete` — Execute deletions instead of generating scripts
- `--force` — Skip confirmation prompts

### stats

Print disk usage statistics for build output without modifying anything.

```bash
bld stats --root MySolution.sln --obj --non-current
```

### nuget

Analyze and categorize NuGet package references across projects. Packages are automatically categorized as:

- **Microsoft Official** — Core .NET packages (System.*, Microsoft.Extensions.*, etc.)
- **Microsoft Non-Official** — Other Microsoft packages
- **Trusted Third-Party** — Well-known packages (Newtonsoft.Json, Serilog, xunit, etc.)
- **Other** — Everything else

```bash
# Aggregated view (default) - see all packages across projects
bld nuget --root /path/to/repo

# Per-project view
bld nuget --root /path/to/repo --no-aggregate

# With custom categorization rules
bld nuget --root /path/to/repo --whitelist-blacklist-file rules.txt
```

**Options:**
- `--aggregate`, `--agg` — Aggregate packages across projects (default: true)
- `--no-aggregate`, `--no-agg` — Show per-project view
- `--show-projects`, `--sp` — Show referencing projects in aggregate mode (default: true)
- `--whitelist-blacklist-file`, `--wbf` — Custom categorization rules file

### tfm

Migrate target frameworks across a solution or project.

```bash
# Auto-detect source and target (uses installed SDK)
bld tfm --root MySolution.sln --apply

# Specify source and target explicitly
bld tfm --root MySolution.sln --from net8.0 --to net9.0 --apply

# Migrate multiple source frameworks
bld tfm --root MySolution.sln --from net7.0,net8.0 --to net9.0 --apply
```

**Options:**
- `--from` — Source TFM(s), comma-separated (auto-detected if omitted)
- `--to` — Target TFM (auto-detected from SDK if omitted)
- `--apply` — Apply changes (dry-run by default)

### cpm

Convert a solution to Central Package Management by creating a `Directory.Packages.props` file and updating project files.

```bash
# Dry-run to see what would change
bld cpm --root MySolution.sln

# Apply changes
bld cpm --root MySolution.sln --apply

# Overwrite existing Directory.Packages.props
bld cpm --root MySolution.sln --apply --overwrite
```

**Options:**
- `--apply` — Apply changes (dry-run by default)
- `--overwrite` — Overwrite existing Directory.Packages.props

### outdated

Check for outdated NuGet packages with target framework compatibility checking.

```bash
# Check for outdated packages
bld outdated --root /path/to/repo

# Include prerelease versions
bld outdated --root /path/to/repo --prerelease

# Update packages in-place
bld outdated --root /path/to/repo --apply
```

**Options:**
- `--apply` — Update packages in-place (dry-run by default)
- `--skip-tfm-check` — Skip target framework compatibility checking
- `--prerelease`, `--pre` — Include prerelease versions

### containerize

Discover Dockerfiles and .NET projects using SDK container support.

```bash
# Find Dockerfiles
bld containerize --root /path/to/repo

# Find .NET SDK container projects
bld containerize --root /path/to/repo --projects

# Find both
bld containerize --root /path/to/repo --all

# List paths only
bld containerize --root /path/to/repo --all --list
```

**Options:**
- `--list`, `-l` — Show paths only, no parsing
- `--projects`, `-p` — Scan for .NET SDK container projects
- `--all`, `-a` — Scan both Dockerfiles and container projects

### mcp

Start the Model Context Protocol (MCP) server for integration with AI coding assistants and agentic workflows.

```bash
# Start MCP server (communicates via stdio)
bld mcp
```

The MCP server exposes all `bld` functionality as tools that can be invoked by AI agents:

| Tool | Description |
|------|-------------|
| `bld_tfm_analyze` | Analyze projects and detect their current Target Framework(s) |
| `bld_tfm_migrate` | Migrate projects from one TFM to another |
| `bld_nuget_analyze` | Analyze NuGet package references across all projects |
| `bld_outdated_check` | Check for outdated NuGet packages |
| `bld_outdated_update` | Update outdated NuGet packages |
| `bld_cpm_analyze` | Analyze solution for Central Package Management conversion |
| `bld_cpm_convert` | Convert solution to Central Package Management |
| `bld_clean_analyze` | Analyze build output directories for cleaning |
| `bld_clean_execute` | Delete build output directories |

#### Registering with Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "bld": {
      "command": "bld",
      "args": ["mcp"]
    }
  }
}
```

#### Registering with VS Code / GitHub Copilot

The MCP server can be used with any MCP-compatible client. For VS Code, configure in your workspace or user settings.

## How It Works

### MSBuild Integration

`bld` uses the official MSBuild APIs to evaluate project files. This means:

1. **In-process evaluation** — Projects are evaluated in the same process for speed
2. **VSToolsPath resolution** — Automatically finds Visual Studio tooling on Windows
3. **Full property evaluation** — MSBuild properties and conditions are properly resolved
4. **Cross-platform** — Works on Windows, macOS, and Linux

The tool uses `Microsoft.Build.Locator` to find installed MSBuild instances and loads the appropriate assemblies.

### NuGet Package Analysis

Package information is extracted from project files after MSBuild evaluation:

1. **PackageReference** items are collected from each project
2. **Version normalization** handles various formats (exact, floating, etc.)
3. **Categorization** uses built-in patterns plus optional custom rules
4. **Aggregation** deduplicates packages across the solution

### Target Framework Detection

The `tfm` command performs intelligent framework detection:

1. **Auto-detects source TFMs** by scanning all projects in the solution
2. **Auto-detects target TFM** from installed .NET SDKs via `dotnet --list-sdks`
3. **Filters to .NET (Core)** frameworks only (excludes .NET Framework, netstandard)
4. **Handles multi-targeting** projects with multiple TFMs

### Central Package Management

The `cpm` command:

1. **Scans all projects** for PackageReference items
2. **Collects unique packages** with their highest versions
3. **Generates Directory.Packages.props** with PackageVersion entries
4. **Updates project files** to remove Version attributes from PackageReferences

## Whitelist/Blacklist Rules File Format

For custom package categorization:

```
# whitelist
My.Trusted.Package
Internal.*

# blacklist
Deprecated.Package
SomeVulnerable.*

# microsoft
MyCompany.Microsoft.*

# trusted
My.Known.Good.Package
```

Patterns support `*` wildcards for prefix matching.

## Typical Workflows

### Preparing for a TFM Upgrade

```bash
# 1. See current state
bld stats --root MySolution.sln

# 2. Check for outdated packages
bld outdated --root MySolution.sln

# 3. Update packages (with dry-run first)
bld outdated --root MySolution.sln --apply

# 4. Migrate TFM
bld tfm --root MySolution.sln --to net9.0 --apply
```

### Auditing a New Repository

```bash
# 1. Analyze NuGet dependencies
bld nuget --root /path/to/repo

# 2. Check for containers
bld containerize --root /path/to/repo --all

# 3. See build output size
bld stats --root /path/to/repo --obj
```

### Cleanup After Build Issues

```bash
# 1. Preview what would be cleaned
bld stats --root MySolution.sln --obj

# 2. Generate cleanup script
bld clean --root MySolution.sln --obj

# 3. Review clean.cmd/clean.sh, then run it
```

## Notes & Caveats

- **MSBuild evaluation failures** are reported but don't abort the run — you'll see which projects had issues
- **VSToolsPath** may need to be specified manually on systems without Visual Studio
- **Beta commands** may change behavior between versions — please file issues if something seems off
- **Dry-run by default** — Commands that modify files require explicit `--apply` or `--delete`

## Contributing

Issues and pull requests welcome! Please include command lines and logs when reporting bugs.

## License

Apache 2.0 — see [LICENSE](LICENSE) for details.
