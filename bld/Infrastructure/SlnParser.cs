using bld.Models;
using bld.Services;
using Microsoft.Build.Construction;

namespace bld.Infrastructure;

internal enum ProcessingType {
    Solution,
    Project,
    Directory
}

internal sealed class SlnParser(IConsoleOutput Output, ErrorSink ErrorSink) {

    public async IAsyncEnumerable<ProjCfg> ParseSolution(string slnPath, IFileSystem? fileSystem = default, bool createDefaultDebugConfiguration = true) {
        if (SlnScanner.IsProjectFile(slnPath)) {
            var proj = new Proj(slnPath, null);
            if (createDefaultDebugConfiguration) {
                yield return new ProjCfg(proj, "Debug", null);
            }
            yield return new ProjCfg(proj, "Release", null);
            yield break;
        }

        var parseResult = default(SlnFileHelper.SlnParseResult);
        var sln = new Sln(slnPath);
        try {
            parseResult = SlnFileHelper.ParseWithFilter(slnPath);
        }
        catch (Exception xcptn) {
            ErrorSink.AddError($"Failed to parse solution file.", exception: xcptn, sln: sln);
            Output.WriteError($"{slnPath} could not be parsed: {xcptn.FormatMessage()}");
            yield break;
        }

        foreach (var project in SlnFileHelper.EnumerateIncludedMSBuildProjects(parseResult).Where(p => File.Exists(p.AbsolutePath))) {
            var fullyQualifiedPath = fileSystem?.FullyQualifyPath(project.AbsolutePath) ?? project.AbsolutePath;

            var queryPlatform = false;
            switch (Path.GetExtension(fullyQualifiedPath)!.ToLowerInvariant()) {
                case ".csproj":
                case ".fsproj":
                case ".sqlproj":
                case ".vbproj": // old proj do not have the <tfm>
                    queryPlatform = false;
                    break;
                case ".vcxproj":
                    queryPlatform = true;
                    break;
                default:
                    continue;
            }

            var proj = new Proj(fullyQualifiedPath, sln);
            // ProjectConfigurations is never null - it is an empty dictionary when the solution has no
            // ActiveCfg entries for this project - so the null check alone left the fallback below
            // unreachable and the project was dropped from the run without a word.
            var configurations = project.ProjectConfigurations
                  .Select(x => (x.Value?.ConfigurationName, queryPlatform ? x.Value?.PlatformName : null))
                        .Where(x => x.ConfigurationName is not null)
                        .Distinct()
                        .ToList();

            if (configurations.Count > 0) {
                foreach (var cfg in configurations) {
                    var projCfg = new ProjCfg(proj, cfg.ConfigurationName!, cfg.Item2);
                    yield return projCfg;
                }
            }
            else {
                Output.WriteWarning($"{fullyQualifiedPath} has no configuration entries in {Path.GetFileName(slnPath)}; assuming default configurations.");
                if (createDefaultDebugConfiguration) {
                    var projCfg = new ProjCfg(proj, "Debug", null);
                    yield return projCfg;
                }
                var projCfgRelease = new ProjCfg(proj, "Release", null);
                yield return projCfgRelease;
            }
        }
    }
}
