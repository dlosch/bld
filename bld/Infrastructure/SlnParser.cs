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

        var solution = default(SolutionFile);
        var sln = new Sln(slnPath);
        try {
            solution = SolutionFile.Parse(slnPath);
        }
        catch (Exception xcptn) {
            ErrorSink.AddError($"Failed to parse solution file.", exception: xcptn, sln: sln);
            Output.WriteError($"{slnPath} could not be parsed: {xcptn.FormatMessage()}");
            yield break;
        }

        foreach (var project in solution.ProjectsInOrder.Where(p => File.Exists(p.AbsolutePath) && p.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)) {
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
            if (project.ProjectConfigurations is { }) {
                foreach (var cfg in project.ProjectConfigurations
                      .Select(x => (x.Value?.ConfigurationName, queryPlatform ? x.Value?.PlatformName : null))
                            .Where(x => x.ConfigurationName is not null)
                            .Distinct()
                    ) {
                    var projCfg = new ProjCfg(proj, cfg.ConfigurationName!, cfg.Item2);
                    yield return projCfg;
                }
            }
            else {
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