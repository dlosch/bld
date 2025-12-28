# bld

*Because sometimes your build output folders grow to the size of a small country*

A .NET tool for those of us who've stared into the abyss of a 50GB `bin` folder and wondered where our life went wrong.

## What Is This?

`bld` is a command-line tool that helps you:

- **Clean build output** — For when your SSD is crying
- **Analyze NuGet packages** — Find out which of the 847 packages you're actually using
- **Migrate target frameworks** — Because net8.0 felt so modern... last year
- **Enable Central Package Management** — One `Directory.Packages.props` to rule them all
- **Find outdated packages** — That package you installed in 2019? Still there. Still outdated.
- **Discover containers** — Dockerfiles hiding in places you forgot existed

This tool is especially handy when working with AI coding assistants that occasionally decide net47, net48, and net8.0 should all coexist in the same project. Somehow. For reasons.

## Installation

### The Professional Way

```bash
dotnet tool install --global Cloudsiders.bld
```

### The "I Don't Trust You Yet" Way

```bash
git clone https://github.com/dlosch/bld.git
cd bld
dotnet build bld.slnx
dotnet run --project bld -- --help
# *squints at code suspiciously*
```

## Quick Start

```bash
# "Where did all my disk space go?"
bld stats --root /path/to/repo

# "I'd like to delete things but I'm terrified"
bld clean --root /path/to/repo

# "Show me all the packages I've accumulated like a digital hoarder"
bld nuget --root /path/to/repo

# "Which packages are from the Before Times?"
bld outdated --root /path/to/repo
```

## Commands

| Command | Stability | What It Does |
|---------|-----------|--------------|
| `clean` | **Stable** | Generates scripts to delete build output. We're not brave enough to delete by default. |
| `stats` | **Stable** | Tells you how bad things are without doing anything about it. Like a therapist. |
| `nuget` | Beta | Analyzes your package dependencies. Prepare for existential questions. |
| `tfm` | Beta | Migrates target frameworks. The "migrate from net6.0 to net9.0" speedrun. |
| `cpm` | Beta | Converts to Central Package Management. Your `Directory.Packages.props` awaits. |
| `outdated` | Beta | Lists packages that need updating. Spoiler: it's a lot of them. |
| `containerize` | Beta | Finds Dockerfiles and container configs hiding in your repo. |

## Command Reference

### clean

*For when you've reached acceptance about your disk space situation*

```bash
# Generate a cleanup script (we believe in previewing before panicking)
bld clean --root /path/to/repo

# Include the obj folder (you brave soul)
bld clean --root /path/to/repo --obj

# Actually delete things (requires signing a waiver in your mind)
bld clean --root /path/to/repo --delete
```

The `--delete` flag exists, but we default to generating scripts because we've made mistakes too.

### stats

*"I just want to know how bad it is"*

```bash
bld stats --root MySolution.sln --obj
```

This command is pure. It only looks. It does not touch. Like window shopping for disk space.

### nuget

*Package archaeology*

```bash
# Aggregated view (default) - the birds-eye view of your dependencies
bld nuget --root /path/to/repo

# Per-project view - for when you want to know exactly which project brought in that weird package
bld nuget --root /path/to/repo --no-aggregate
```

Packages are automatically sorted into categories:
- **Microsoft Official** — The stuff that comes with .NET
- **Microsoft Non-Official** — Microsoft packages that don't have "official" energy
- **Trusted Third-Party** — Newtonsoft.Json has been there since the beginning
- **Other** — *squints* what is this and why do we have 47 versions?

### tfm

*The "I should really update this" command*

```bash
# Auto-detect everything (living dangerously)
bld tfm --root MySolution.sln --apply

# Be explicit about it
bld tfm --root MySolution.sln --from net8.0 --to net9.0 --apply

# Multi-targeting? We've got you
bld tfm --root MySolution.sln --from net7.0,net8.0 --to net9.0 --apply
```

The tool auto-detects your installed SDK version because we assume you want the shiny new one. If you don't, specify `--to`.

### cpm

*Centralizing your package chaos*

```bash
# Dry-run first, always
bld cpm --root MySolution.sln

# Apply the changes
bld cpm --root MySolution.sln --apply
```

This creates a `Directory.Packages.props` file and removes version numbers from your project files. It's like Marie Kondo for your PackageReferences.

### outdated

*The guilt trip command*

```bash
# See all the packages you've been neglecting
bld outdated --root /path/to/repo

# Include prereleases (for the adventurous)
bld outdated --root /path/to/repo --prerelease

# Actually update them
bld outdated --root /path/to/repo --apply
```

### containerize

*Finding containers in the wild*

```bash
# Find Dockerfiles
bld containerize --root /path/to/repo

# Find .NET SDK container projects
bld containerize --root /path/to/repo --projects

# Find everything container-related
bld containerize --root /path/to/repo --all
```

## How It Works (The Somewhat Technical Bit)

### MSBuild Integration

We use the official MSBuild APIs, which means:
- Projects are evaluated properly (with all those conditions and properties)
- VSToolsPath is auto-detected (on Windows, at least — sorry Linux users, you might need `--vstoolspath`)
- We load MSBuild assemblies using `Microsoft.Build.Locator` like civilized people

### Package Analysis

1. Load projects with MSBuild
2. Extract PackageReference items
3. Categorize them (Microsoft? Third-party? That thing Dave added three years ago?)
4. Present them in a way that doesn't induce panic

### TFM Detection

The `tfm` command is smarter than it looks:
1. Runs `dotnet --list-sdks` to find your installed SDKs
2. Scans your projects to find existing TFMs
3. Filters out netstandard and net48 (we don't migrate those, that's a whole different therapy session)
4. Proposes changes and only applies them if you say `--apply`

## Global Options

Available on all commands for your convenience (and our sanity):

| Option | Aliases | What It Does |
|--------|---------|--------------|
| `--root` | `-r` | Where to look. Defaults to "right here." |
| `--depth` | `-d` | How deep to search. Default is 3, because we have limits. |
| `--log` | `-v`, `--verbosity` | How much do you want to know? (Debug, Verbose, Info, Warning, Error) |
| `--vstoolspath` | `-vs` | Explicit VSToolsPath. For when auto-detection fails you. |
| `--novstoolspath` | `-novs` | Don't try to find VSToolsPath. You do you. |

## FAQ

**Q: Will this delete my code?**  
A: No. The `clean` command only touches build output (`bin`/`obj`). And even then, it generates a script first by default. We're cautious.

**Q: Why is aggregate mode the default for `nuget`?**  
A: Because nobody wants to see the same package listed 47 times across 47 projects. Trust us.

**Q: What if something goes wrong?**  
A: Most commands are dry-run by default. You have to explicitly say `--apply` or `--delete` to change anything. We learned this lesson so you don't have to.

**Q: Why does this exist?**  
A: We had a repo with 80GB of build output and made some choices.

## Notes & Caveats

- **MSBuild evaluation can fail** on some projects. We'll tell you which ones, but we won't stop the whole run.
- **VSToolsPath shenanigans** — On Windows with Visual Studio, it usually works. Elsewhere... bring your own `--vstoolspath`.
- **Beta commands** might change. We're iterating. Please file issues with logs if something explodes.

## Contributing

Found a bug? Have an idea? Please open an issue. Include your command line and any error output — we promise not to judge your folder structure.

## License

Apache 2.0 — See [LICENSE](LICENSE). Use responsibly.

---

*Built with ☕ and mild frustration at the state of build folders everywhere.*
