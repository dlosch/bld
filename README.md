# bld clean

This is a tool to clean build output folders for (especially .net) MSBuild projects.

## what does it do?

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
- in process evaluation of properties for each project and configuration (note: the Microsoft.Build evaluation is *not* instant)
- automatically resolves default msbuild install (typically .NET SDK) and resolves VSToolsPath for additional target files provided by Visual Studio installations (if available)
- enables you to delete only non-current build output (TagetFramework(s) no longer referenced in proj file)
- validates tfms for .net projects to make sure the correct stuff gets cleaned
- by default doesn't delete, only dumps stats and the command line to delete folders. Nothing gets touched unless you specify --delete
- basic support for linux

Note:
- global.json ... doe to the consistent /s way msbuild, dotnet msbuild, and dotnet build handle global.json ... 


## bld (dotnet tool) Commands

| Command | Description |
|---|---|
| clean | Evaluate solutions/projects and produce a summary and an OS-specific deletion script (dry-run by default). Use `--delete` to actually remove files. |
| stats | Compute and print cleaning statistics only (no deletion and no deletion script). Useful to preview impact. |
| containerize | Analyze and display information about Dockerfiles in the project. Searches for and parses Dockerfiles, showing base images, build stages, exposed ports, and other configuration details. |

## Examples

Generate a deletion script:

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

Analyze Dockerfiles in a project:

```text
bld containerize --root <rootDir> --depth 3
```

List Dockerfiles only (without parsing details):

```text
bld containerize --root <rootDir> --list
```


## Options (current defaults & meanings)

| Option | Type | Default | Description |
|---|---:|---:|---|
| `--root`, `-r` | string | current working directory | Root directory or a `.sln` path (can also be specified as the trailing argument). |
| `--depth`, `-d` | int | 3 | Recursion depth to search for solution files when `--root` is a directory. |
| `--non-current`, `--noncurrent`, `-nc` | bool | false | Only consider directories for target frameworks no longer referenced by the project (non-current TFMs). |
| `--obj`, `-obj` | bool | true | Also consider `BaseIntermediateOutputPath` (obj) for cleaning. Note: CleaningOptions defaults to keep obj handling enabled. |
| `--log`, `-v`, `--verbosity` | LogLevel | Warning | Log verbosity: Debug, Verbose, Info, Warning, Error. |
| `--output-file`, `-o` | string | `clean.cmd` | Path to write the deletion script (batch file or shell commands depending on OS). |
| `--vstoolspath`, `-vs` | string | null | Explicit VSToolsPath for MSBuild evaluation; if omitted, the tool may try to resolve it from Visual Studio instances. |
| `--novstoolspath`, `-novs` | bool | false | Do not try to auto-resolve VSToolsPath from environment or vswhere. |
| `--delete` | bool | false | Perform deletions instead of a dry-run. |

Note: the code includes additional internal flags (parallel/processor modes) and different processors (stats, batch file writer, delete) — the default command wiring uses the batch-file (script) processor for `clean` and the stats processor for `stats`.

## Notes / Caveats

- MSBuild evaluation can be slow for large repos because the tool invokes MSBuild evaluation per project/configuration pair to compute accurate paths. This is deliberate: better to be correct and slow than fast and wrong.
- MSBuild property evaluation may fail for misconfigured projects — in that case the tool reports the error and skips the problematic project.

## Installing as a dotnet tool

Package name and distribution depend on how you publish. Example install (replace `<package>` with the real package id):

```powershell
dotnet tool install -g <package>
# then run:
bld clean --help
```

For a local install, use `dotnet tool install --local <package>` in a folder with a tool manifest.

---

This is a rewrite of a previous tool which used msbuild command line invocation with -getproperty to query properties. This is now in proc using the default msbuild installation.
