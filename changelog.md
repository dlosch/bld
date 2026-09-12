# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Changes
- [BUG] `clean`/`stats`: projects using the SDK artifacts layout (`UseArtifactsOutput=true`) were only cleaned when single-targeted; multi-targeted output under `artifacts/bin/<project>/<config>_<tfm>[_<rid>]/` was skipped without any message. Both are now recognized, `--non-current` applies to the TFM segment, and an `OutDir` that matches no known layout is reported as a warning.
- extend `clean`/`stats`: `--publish` also cleans publish output (`PublishDir`) and pack output (`PackageOutputPath`), including `artifacts/publish/<project>/` and `artifacts/package/` in the artifacts layout. Off by default.
- extend `nuget` and `outdated`: `GlobalPackageReference` and `PackageDownload` items are listed and checked. [BUG] a `GlobalPackageReference` was attributed to the SDK's NuGet.targets instead of `Directory.Packages.props`, so `outdated --apply` reported the update but wrote nothing; it is now updated in place. `PackageDownload` versions are updated in their bracketed form and skip the TFM check.
- extend `nuget` command: `--transitive` lists the packages resolved through other packages (from `project.assets.json`), categorized and blacklist-checked like direct references, with the packages that pull them in.
- extend `outdated` command: package sources come from the `nuget.config` hierarchy (enabled sources, `packageSourceMapping`, `packageSourceCredentials`) instead of only api.nuget.org, so packages on private feeds are checked and updated. `--source` restricts or overrides the sources, `--ignore-source-mapping` queries every source. `tfm --update-packages` uses the same sources.
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
