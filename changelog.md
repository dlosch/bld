# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Changes
- extend `outdated` command: `--max-bump <major|minor|patch>` caps how far a package may move from the version it is pinned at now. Versions above the cap are reported in a new `held` column instead of being applied.
- extend `outdated` command: `--package`/`-p` and `--exclude` select a subset of packages by wildcard pattern.
- extend `outdated` command: `--apply` and dry runs now check the packages they would update against the versions their dependencies will end up at, and hold back any package whose declared range would be violated. `--allow-conflicts` updates anyway. The check covers direct references only.
- extend `outdated` command: `--verify-restore` runs `dotnet restore` after `--apply` and fails the command on NuGet errors.
- bump `System.CommandLine` to 2.0.12 and `Microsoft.SourceLink.GitHub` to 10.0.401.

## [0.2.33] - 2026-05-25

### Changes
- remove net8.0 target
- extend `outdated` command: --orphaned and --interactive
- fix System.Text.Json load error by preloading

## [0.2.32] - 2026-03-29

### Changes
- Added `build-props` command to the root command set.
- [BUG] .slnf file handling changed. Actually applies the fiter from the .slnf file now (it did not before).
