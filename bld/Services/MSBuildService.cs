using bld.Infrastructure;
using bld.Models;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using System.Collections.Concurrent;

namespace bld.Services;
/// <summary>
/// MSBuild service using in-process Microsoft.Build.Evaluation
/// </summary>
internal class MSBuildService : IMSBuildService, IDisposable {
    private static bool _isRegistered = false;
    private static readonly object _lockObject = new object();
    private readonly IConsoleOutput _console;
    private readonly ConcurrentDictionary<string, ProjectCollection> _projectCollections = new();

    public MSBuildService(IConsoleOutput console) {
        _console = console;
        //RegisterMSBuildDefaults();
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
                        var instances = MSBuildLocator.QueryVisualStudioInstances(queryOptions).ToList();


                        //var instance = new VisualStudioInstance()
                        var instance = MSBuildLocator.QueryVisualStudioInstances(queryOptions)
                            .Where(x => x.DiscoveryType != DiscoveryType.DotNetSdk)
                            .OrderByDescending(x => x.Version)
                            .FirstOrDefault();

                        if (instance is { }) // and not { DiscoveryType: DiscoveryType.DotNetSdk })
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
                    console?.WriteError($"Failed to register MSBuild: {ex.Message}");
                    throw;
                }
            }
        }
    }

    public Task<ProjectProperties?> GetProjectPropertiesAsync(string projectPath, string? configuration = null, string? platform = null, params string[] properties) {
        try {
            var globalProperties = new Dictionary<string, string>();

            if (!string.IsNullOrEmpty(configuration))
                globalProperties["Configuration"] = configuration;

            //if (!string.IsNullOrEmpty(platform))
            //    globalProperties["Platform"] = platform;

            var projectCollection = new ProjectCollection();

            try {

                var project = new Project(projectPath, globalProperties, null, projectCollection); // projectCollection.LoadProject(projectPath);

                var resultProperties = new Dictionary<string, string>();

                // Get all properties if none specified
                if (properties.Length == 0) {
                    foreach (var prop in project.Properties) {
                        resultProperties[prop.Name] = prop.EvaluatedValue;
                    }
                }
                else {
                    // Get specific properties
                    foreach (var propName in properties) {
                        var prop = project.GetProperty(propName);
                        if (prop != null) {
                            resultProperties[propName] = prop.EvaluatedValue;
                        }
                    }
                }

                return Task.FromResult<ProjectProperties?>(new ProjectProperties { Properties = resultProperties });
            }
            finally {
                projectCollection.Dispose();
            }
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to evaluate project {projectPath}: {ex.Message}");
            return Task.FromResult<ProjectProperties?>(null);
        }
    }

    public void Dispose() {
        foreach (var collection in _projectCollections.Values) {
            collection.Dispose();
        }
        _projectCollections.Clear();
    }
}