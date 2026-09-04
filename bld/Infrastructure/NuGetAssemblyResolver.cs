using Microsoft.Build.Locator;
using System.Reflection;
using System.Runtime.Loader;

namespace bld.Infrastructure;

/// <summary>
/// Loads NuGet.* assemblies on demand, picking the newer of the copy bundled with bld and the copy
/// shipped by the hosted MSBuild instance.
///
/// bld.csproj keeps the bundled copies out of deps.json (they live in bin/nuget), so they are not on
/// the runtime's TPA list and this resolver gets to choose. MSBuild 18.9+ references NuGet.Frameworks
/// directly and the runtime refuses to bind that against an older copy (0x80131040); bld's own code
/// needs at least the version it compiled against. The newer of the two satisfies both.
/// </summary>
internal static class NuGetAssemblyResolver {
    private static readonly string BundledDirectory = Path.Combine(AppContext.BaseDirectory, "nuget");

    private static readonly object _lock = new();
    private static string? _msbuildDirectory;
    private static bool _probed;

    /// <summary>
    /// Directory of the MSBuild instance we host. MSBuildService sets this when it registers, but a
    /// NuGet.* assembly can be resolved earlier than that - JIT of a method mentioning NuGet types
    /// loads them before the method's first statement runs, which is exactly what `outdated` does.
    /// Once a version is bound into the default load context it cannot be swapped, so the resolver
    /// discovers the directory itself rather than depending on call order.
    /// </summary>
    public static string? MSBuildDirectory {
        get {
            lock (_lock) {
                if (_msbuildDirectory is null && !_probed) {
                    _probed = true;
                    try {
                        // Same choice RegisterDefaults makes: the locator lists the global.json-resolved
                        // SDK first, then the newest.
                        _msbuildDirectory = MSBuildLocator
                            .QueryVisualStudioInstances(VisualStudioInstanceQueryOptions.Default)
                            .FirstOrDefault()?.MSBuildPath;
                    }
                    catch {
                        // No SDK discoverable: fall back to the bundled copies only.
                    }
                }
                return _msbuildDirectory;
            }
        }
        set {
            lock (_lock) {
                _msbuildDirectory = value;
                _probed = true;
            }
        }
    }

    /// <summary>Must run before any NuGet.* type is touched and before MSBuildLocator adds its own resolver.</summary>
    public static void Register() => AssemblyLoadContext.Default.Resolving += Resolve;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) {
        if (name.Name is null || !name.Name.StartsWith("NuGet.", StringComparison.Ordinal)) {
            return null;
        }
        var candidates = new[] { BundledDirectory, MSBuildDirectory }
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Select(dir => Path.Combine(dir!, name.Name + ".dll"))
            .Where(File.Exists);
        var path = PickNewest(candidates, p => AssemblyName.GetAssemblyName(p).Version);
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }

    /// <summary>Highest version wins; on a tie the first candidate (the bundled copy) wins.</summary>
    internal static string? PickNewest(IEnumerable<string> candidates, Func<string, Version?> versionOf) =>
        candidates.OrderByDescending(versionOf).FirstOrDefault();
}
