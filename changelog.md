# Changelog

All notable changes to this project are documented in this file.

## [0.2.33] - 2026-05-25

### Changes
- remove net8.0 target
- extend `outdated` command: --orhphaned and --interactive
- fix System.Text.Json load error by preloading

## [0.2.32] - 2026-03-29

### Changes
- Added `build-props` command to the root command set.
- [BUG] .slnf file handling changed. Actually applies the fiter from the .slnf file now (it did not before).
