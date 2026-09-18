# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Changes
- extend `tfm` command: `--update-packages` now runs `outdated --apply` on the same input after the frameworks are written, so packages are checked for compatibility with the new target framework, capped by the new `--max-bump` option and the saved package policies, checked for dependency conflicts, and updated in `Directory.Packages.props` under central package management. It used to bump every inline `PackageReference` to the latest stable version without any of that, and updated nothing under central package management.
- [BUG] exit codes: `nuget` and `build-props` returned 0 after reporting errors; they now return 1 when a solution or project could not be analyzed (`build-props` also when a project could not be evaluated). `cpm` and `tfm` collected solution and project load errors and never showed them; they are now printed and fail the command.
- build: `TreatWarningsAsErrors` is set (the previous `WarningsAsErrors` property takes a list of warning ids, so warnings never failed the build); a repo `nuget.config` pins the restore to nuget.org. A CI workflow runs the test suite on every push and pull request, and the publish workflow runs it before packing and refuses a release tag that does not match the project version.
- extend `outdated` command: `--interactive` shows the packages grouped, so a whole family can be toggled in one keystroke, and left/right move a package between the versions the feeds offer for it — highest patch, highest minor, highest major — or cap a whole family at a bump class from its group line, which shows the class (a package with nothing at or below the cap is unchecked until the cap admits it again); packages are indented under their group line. `--group-by <prefix|bump|none>` (default `prefix`), `--group-depth` and `--group-min` control the grouping; passing `--group-by` explicitly also groups the report table. Every row names its bump class (patch/minor/major, colored), `--preselect <all|no-major|patch|none>` picks what starts out checked, versions the bump cap held back are listed unchecked instead of being unreachable, and esc cancels without writing. [BUG] `--interactive` without a terminal threw out of Spectre; it now fails with an exit code and a hint.
- extend `outdated` command: one lookup per package now collects the highest compatible version per bump class instead of stopping at the first usable one, so `--max-bump` picks from a set rather than constraining the fetch. [BUG] the version in the `held` column was never checked for target framework compatibility; every offered version is now checked, and registration pages that end below the pinned version are no longer fetched.
- extend `outdated` command: `--max-bump-for "<pattern>=<level>"` overrides `--max-bump` per package pattern, and `--interactive` prints the equivalent prompt-free command line after the selection.
- extend `outdated` command: the dependency check also runs in reverse. A package that stays where it is and declares a range on one being updated now holds that update back when the new version falls outside the range (`A 9.0.1 breaks Q 3.1.0, which requires A [8.0.0, 9.0.0)`); `--interactive` offers to include the blocking package's update instead. The manifests come from the registration data already fetched, so it costs no extra request. [BUG] a range declared for one target framework was dropped when another framework group declared an open-ended range on the same dependency, so an upper bound could go unchecked.
- extend `outdated` command: package policies. `policy.json` under `BLD_HOME` (or the local application data folder) holds persistent per-pattern caps (`MassTransit*` at most `minor`) that every run applies; `--max-bump-for` overrides a policy, `--ignore-policy` skips them. In `--interactive`, `p` saves a "no major" rule for the package under the cursor (the family's pattern on a prefix group line) and caps the row, or removes the rule it has. The held-back summary names the source of each cap.
- [BUG] `outdated --interactive`: the picker was not drawn until the first key was pressed; only the "Interactive update selection" rule was visible.
- speed up `outdated`: ProjectReferences are read from the same MSBuild evaluation as the PackageReferences (they used to cost a second, sequential evaluation of every project configuration), only the Release configuration is evaluated, evaluations reuse pooled `ProjectCollection`s and one shared evaluation context so the SDK imports are parsed once per worker and the SDK is resolved once per run instead of once per project, the SDK's default item globs are not expanded (nothing read depends on them), every package source is queried at once, all registration pages that can hold a candidate are fetched at once, and lookups run at least 16 wide regardless of `--concurrency`. `-v Info` prints how long evaluation and metadata fetching took.
- extend `outdated` command: `--eval-cache` skips the MSBuild evaluation of a project configuration when the project file and every non-SDK import are byte-identical to the last evaluation (SHA-256, keyed by global properties, MSBuild version and which `Directory.Build.*` files exist up the tree). Entries live under `BLD_HOME` or the local application data folder. Opt-in: properties set through environment variables are not detected.

## [0.3.0] - 2026-09-12

### Changes
- [BUG] `clean`/`stats`: projects using the SDK artifacts layout (`UseArtifactsOutput=true`) were only cleaned when single-targeted; multi-targeted output under `artifacts/bin/<project>/<config>_<tfm>[_<rid>]/` was skipped without any message. Both are now recognized, `--non-current` applies to the TFM segment, and an `OutDir` that matches no known layout is reported as a warning.
- extend `clean`/`stats`: `--publish` also cleans publish output (`PublishDir`) and pack output (`PackageOutputPath`), including `artifacts/publish/<project>/` and `artifacts/package/` in the artifacts layout. Off by default.
- extend `tfm` command: warns when the governing `global.json` pins an SDK that cannot build the target framework; `--update-global-json` sets `sdk.version` to the highest installed SDK of the target's major on `--apply`.
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
