using bld.Infrastructure;
using bld.Models;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;

namespace bld.Services;
/// <summary>
/// MSBuild service using in-process Microsoft.Build.Evaluation
/// </summary>
internal class MSBuildService : IMSBuildService, IDisposable {
    private static bool _isRegistered = false;
    private static readonly object _lockObject = new object();
    private readonly IConsoleOutput _console;

    public MSBuildService(IConsoleOutput console) {
        _console = console;
    }


    public static void RegisterMSBuildDefaults(IConsoleOutput? console, CleaningOptions cleaningOptions) {
        lock (_lockObject) {
            if (!_isRegistered) {
                try {
                    if (MSBuildLocator.CanRegister) {
                        if (cleaningOptions.UseVSInstance) {
                            console?.WriteDebug($"Query VS instances ...");
                            var loc = new VSService()
                                .GetLocations()
                                .FirstOrDefault();
                            if (loc is { }) {
                                console?.WriteWarning($"Registering MSBuild instance: {loc.Name} {loc.MSBuildPath} {Path.Exists(loc.MSBuildPath)} {loc.Version}");
                                MSBuildLocator.RegisterInstance(loc);
                                return;
                            }
                        }

                        var queryOptions = new VisualStudioInstanceQueryOptions { DiscoveryTypes = DiscoveryType.VisualStudioSetup | DiscoveryType.DotNetSdk | DiscoveryType.DeveloperConsole };

                        var instance = MSBuildLocator.QueryVisualStudioInstances(queryOptions)
                            .Where(x => x.DiscoveryType != DiscoveryType.DotNetSdk)
                            .OrderByDescending(x => x.Version)
                            .FirstOrDefault();

                        if (instance is { })
                        {
                            MSBuildLocator.RegisterInstance(instance);
                            console?.WriteDebug($"Registered MSBuild instance: {instance.Name} {instance.Version}");
                        }
                        else {
                            MSBuildLocator.RegisterDefaults();
                            console?.WriteDebug("Registered default MSBuild instance");
                        }
                    }
                    _isRegistered = true;
                }
                catch (Exception ex) {
                    console?.WriteError($"Failed to register MSBuild: {ex.FormatMessage()}");
                    throw;
                }
            }
        }
    }

    internal static bool IsRegistered {
        get {
            lock (_lockObject) {
                return _isRegistered;
            }
        }
    }

    public Task<ProjectProperties?> GetProjectPropertiesAsync(string projectPath, string? configuration = null, string? platform = null, params string[] properties) {
        try {
            var globalProperties = new Dictionary<string, string>();

            if (!string.IsNullOrEmpty(configuration))
                globalProperties["Configuration"] = configuration;

            using var projectCollection = new ProjectCollection();

            var project = new Project(projectPath, globalProperties, null, projectCollection);

            var resultProperties = new Dictionary<string, string>();

            if (properties.Length == 0) {
                foreach (var prop in project.Properties) {
                    resultProperties[prop.Name] = prop.EvaluatedValue;
                }
            }
            else {
                foreach (var propName in properties) {
                    var prop = project.GetProperty(propName);
                    if (prop != null) {
                        resultProperties[propName] = prop.EvaluatedValue;
                    }
                }
            }

            return Task.FromResult<ProjectProperties?>(new ProjectProperties { Properties = resultProperties });
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to evaluate project {projectPath}: {ex.FormatMessage()}");
            return Task.FromResult<ProjectProperties?>(null);
        }
    }

    public void Dispose() {
    }
}
