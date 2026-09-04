using bld.Infrastructure;
using bld.Models;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using System.Runtime.CompilerServices;

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

    // MSBuildLocator.Register* installs an AssemblyLoadContext resolver that maps
    // assembly loads to the dotnet SDK directory. The SDK ships its own
    // System.Text.Json (and friends), which can be older than what this app links
    // against. If S.T.J is first touched *after* the resolver is active, the
    // resolver wins and we end up with a version mismatch. Force-load the
    // suspects here so they're already resolved by the normal probe path.
    // Kept NoInlining so the JIT can't pull these loads into a caller frame that
    // also references Microsoft.Build.* types.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PreloadAssembliesBeforeMSBuildLocator() {
        _ = typeof(System.Text.Json.JsonSerializer).Assembly;
        _ = typeof(System.IO.Pipelines.PipeReader).Assembly;
        _ = typeof(System.Text.Encodings.Web.HtmlEncoder).Assembly;
    }

    public static void RegisterMSBuildDefaults(IConsoleOutput? console, CleaningOptions cleaningOptions) {
        PreloadAssembliesBeforeMSBuildLocator();
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
                                NuGetAssemblyResolver.MSBuildDirectory = loc.MSBuildPath;
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
                            NuGetAssemblyResolver.MSBuildDirectory = instance.MSBuildPath;
                            MSBuildLocator.RegisterInstance(instance);
                            console?.WriteDebug($"Registered MSBuild instance: {instance.Name} {instance.Version} ({instance.MSBuildPath})");
                        }
                        else {
                            instance = MSBuildLocator.RegisterDefaults();
                            NuGetAssemblyResolver.MSBuildDirectory = instance.MSBuildPath;
                            console?.WriteDebug($"Registered default MSBuild instance: {instance.Name} {instance.Version} ({instance.MSBuildPath})");
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
