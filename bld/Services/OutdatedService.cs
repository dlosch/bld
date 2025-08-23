using bld.Infrastructure;
using bld.Models;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace bld.Services;

internal class OutdatedService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;
    private readonly SourceCacheContext _cache;
    private readonly ILogger _logger;

    public OutdatedService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
        _cache = new SourceCacheContext();
        _logger = new NuGetLogger(_console);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> CheckOutdatedPackagesAsync(string rootPath, bool updatePackages, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);
        
        _console.WriteInfo("Checking for outdated packages...");

        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

        var allPackageReferences = new Dictionary<string, List<PackageInfo>>();
        var projectFiles = new List<string>();

        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            _console.WriteVerbose($"Processing solution: {slnPath}");
            
            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                var projectPath = projCfg.Path;
                projectFiles.Add(projectPath);
                
                var packageRefs = await ExtractPackageReferencesAsync(projectPath, cancellationToken);
                foreach (var packageInfo in packageRefs) {
                    if (!allPackageReferences.TryGetValue(packageInfo.Id, out var list)) {
                        list = new List<PackageInfo>();
                        allPackageReferences[packageInfo.Id] = list;
                    }
                    list.Add(packageInfo);
                }
            }
        }

        if (allPackageReferences.Count == 0) {
            _console.WriteInfo("No package references found.");
            return 0;
        }

        _console.WriteInfo($"Found {allPackageReferences.Count} unique packages across {projectFiles.Count} projects");

        var outdatedPackages = new List<OutdatedPackageInfo>();
        var packageSource = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var packageMetadataResource = await packageSource.GetResourceAsync<PackageMetadataResource>(cancellationToken);

        foreach (var (packageId, packageInfos) in allPackageReferences) {
            try {
                _console.WriteVerbose($"Checking {packageId}...");
                
                var metadata = await packageMetadataResource.GetMetadataAsync(packageId, true, true, _cache, _logger, cancellationToken);
                var latestStable = metadata
                    .Where(m => !m.Identity.Version.IsPrerelease)
                    .OrderByDescending(m => m.Identity.Version)
                    .FirstOrDefault();

                if (latestStable == null) {
                    _console.WriteWarning($"No stable version found for {packageId}");
                    continue;
                }

                var latestVersion = latestStable.Identity.Version;
                var currentVersions = packageInfos.Select(p => p.Version).Distinct().ToList();
                
                var hasOutdated = false;
                foreach (var currentVersionStr in currentVersions) {
                    if (NuGetVersion.TryParse(currentVersionStr, out var currentVersion)) {
                        if (currentVersion < latestVersion) {
                            hasOutdated = true;
                            var projects = packageInfos.Where(p => p.Version == currentVersionStr).Select(p => p.ProjectPath).ToList();
                            outdatedPackages.Add(new OutdatedPackageInfo {
                                PackageId = packageId,
                                CurrentVersion = currentVersionStr,
                                LatestVersion = latestVersion.ToString(),
                                ProjectPaths = projects
                            });
                        }
                    }
                }

                if (!hasOutdated) {
                    _console.WriteVerbose($"{packageId} is up to date");
                }
            }
            catch (Exception ex) {
                _console.WriteWarning($"Failed to check {packageId}: {ex.Message}");
            }
        }

        if (outdatedPackages.Count == 0) {
            _console.WriteInfo("All packages are up to date!");
            return 0;
        }

        _console.WriteInfo($"\nFound {outdatedPackages.Count} outdated package references:");
        foreach (var outdated in outdatedPackages.OrderBy(x => x.PackageId)) {
            _console.WriteWarning($"{outdated.PackageId}: {outdated.CurrentVersion} → {outdated.LatestVersion}");
            foreach (var project in outdated.ProjectPaths) {
                _console.WriteVerbose($"  - {Path.GetFileName(project)}");
            }
        }

        if (updatePackages) {
            _console.WriteInfo("\nUpdating packages to latest versions...");
            foreach (var outdated in outdatedPackages) {
                foreach (var projectPath in outdated.ProjectPaths) {
                    await UpdatePackageVersionAsync(projectPath, outdated.PackageId, outdated.LatestVersion, cancellationToken);
                    _console.WriteInfo($"Updated {outdated.PackageId} to {outdated.LatestVersion} in {Path.GetFileName(projectPath)}");
                }
            }
            _console.WriteInfo($"Updated {outdatedPackages.Count} packages in {outdatedPackages.Sum(x => x.ProjectPaths.Count)} project files");
        } else {
            _console.WriteInfo("\nUse --update to apply these changes.");
        }

        return 0;
    }

    private async Task<List<PackageInfo>> ExtractPackageReferencesAsync(string projectPath, CancellationToken cancellationToken) {
        var packageReferences = new List<PackageInfo>();

        try {
            using var stream = File.OpenRead(projectPath);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            var packageRefElements = doc.Descendants("PackageReference");

            foreach (var element in packageRefElements) {
                var include = element.Attribute("Include")?.Value;
                var version = element.Attribute("Version")?.Value ?? 
                             element.Element("Version")?.Value;

                if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version)) {
                    packageReferences.Add(new PackageInfo {
                        Id = include,
                        Version = version,
                        ProjectPath = projectPath
                    });
                }
            }
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to parse {projectPath}: {ex.Message}");
        }

        return packageReferences;
    }

    private async Task UpdatePackageVersionAsync(string projectPath, string packageId, string newVersion, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }
            
            var packageRefElements = doc.Descendants("PackageReference")
                .Where(e => e.Attribute("Include")?.Value == packageId);

            foreach (var element in packageRefElements) {
                var versionAttr = element.Attribute("Version");
                var versionElement = element.Element("Version");

                if (versionAttr != null) {
                    versionAttr.Value = newVersion;
                } else if (versionElement != null) {
                    versionElement.Value = newVersion;
                }
            }

            using var writeStream = File.Create(projectPath);
            await doc.SaveAsync(writeStream, SaveOptions.None, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {projectPath}: {ex.Message}");
        }
    }

    private class PackageInfo {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
    }

    private class OutdatedPackageInfo {
        public string PackageId { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public List<string> ProjectPaths { get; set; } = new();
    }

    private class NuGetLogger : ILogger {
        private readonly IConsoleOutput _console;

        public NuGetLogger(IConsoleOutput console) {
            _console = console;
        }

        public void LogDebug(string data) => _console.WriteVerbose(data);
        public void LogVerbose(string data) => _console.WriteVerbose(data);
        public void LogInformation(string data) => _console.WriteInfo(data);
        public void LogMinimal(string data) => _console.WriteInfo(data);
        public void LogWarning(string data) => _console.WriteWarning(data);
        public void LogError(string data) => _console.WriteError(data);
        public void LogInformationSummary(string data) => _console.WriteInfo(data);
        public void Log(NuGet.Common.LogLevel level, string data) {
            switch (level) {
                case NuGet.Common.LogLevel.Debug:
                case NuGet.Common.LogLevel.Verbose:
                    LogVerbose(data);
                    break;
                case NuGet.Common.LogLevel.Information:
                case NuGet.Common.LogLevel.Minimal:
                    LogInformation(data);
                    break;
                case NuGet.Common.LogLevel.Warning:
                    LogWarning(data);
                    break;
                case NuGet.Common.LogLevel.Error:
                    LogError(data);
                    break;
            }
        }

        public Task LogAsync(NuGet.Common.LogLevel level, string data) {
            Log(level, data);
            return Task.CompletedTask;
        }

        public void Log(ILogMessage message) => Log(message.Level, message.Message);

        public Task LogAsync(ILogMessage message) {
            Log(message);
            return Task.CompletedTask;
        }
    }
}