using bld.Infrastructure;
using bld.Models;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace bld.Services;

/// <summary>
/// Service responsible for discovering package references from solutions and projects
/// </summary>
internal class PackageDiscoveryService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public PackageDiscoveryService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    /// <summary>
    /// Discovers all package references from the specified root path
    /// </summary>
    /// <param name="rootPath">Root path to scan for solutions/projects</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of discovered package references</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<(Dictionary<string, PackageInfoContainer> PackageReferences, int ProjectCount, ErrorSink ErrorSink)> DiscoverPackageReferencesAsync(
        string rootPath,
        CancellationToken cancellationToken = default) {
        
        MSBuildService.RegisterMSBuildDefaults(_console, _options);

        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);
        var fileSystem = new FileSystem(_console, errorSink);
        var cache = new ProjCfgCache(_console);

        var allPackageReferences = new Dictionary<string, PackageInfoContainer>(StringComparer.OrdinalIgnoreCase);

        try {
            var projParser = new ProjParser(_console, errorSink, _options);

            await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
                await _console.StartStatusAsync($"Processing solution {slnPath}", async ctx => {
                    await foreach (var projCfg in slnParser.ParseSolution(slnPath, fileSystem)) {
                        var packageRefs = new PackageInfoContainer();
                        
                        if (!string.Equals(projCfg.Configuration, "Release", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!cache.Add(projCfg)) continue;

                        var refs = projParser.GetPackageReferences(projCfg);
                        if (refs?.PackageReferences is null || !refs.PackageReferences.Any()) {
                            _console.WriteDebug($"No references in {projCfg.Path}");
                            continue;
                        }

                        var exnm = refs.PackageReferences.Select(re => new PackageInfo {
                            Id = re.Key,
                            FromProps = refs.UseCpm ?? false,
                            TargetFramework = refs.TargetFramework,
                            TargetFrameworks = refs.TargetFrameworks,
                            ProjectPath = refs.Proj.Path,
                            PropsPath = refs.CpmFile,
                            Item = re.Value
                        });

                        var bad = exnm.Where(e => string.IsNullOrEmpty(e.Version)).ToList();
                        if (bad.Any()) _console.WriteWarning($"Project {projCfg.Path} has package references with no resolvable version: {string.Join(", ", bad.Select(b => b.Id))}");
                        packageRefs.AddRange(exnm);

                        foreach (var pkg in packageRefs) {
                            if (!allPackageReferences.TryGetValue(pkg.Id, out var list)) {
                                list = new PackageInfoContainer();
                                allPackageReferences[pkg.Id] = list;
                            }
                            list.Add(pkg);
                        }
                    }
                });
            }
        }
        catch (Exception ex) {
            _console.WriteException(ex);
            throw;
        }

        return (allPackageReferences, cache.Count, errorSink);
    }
}