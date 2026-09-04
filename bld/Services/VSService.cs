using bld.Infrastructure;
using Microsoft.Build.Locator;
using System.Runtime.CompilerServices;

namespace bld.Services;

internal class VSService {

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    extern static VisualStudioInstance CreateVisualStudioInstance(string name, string path, Version version, DiscoveryType discoveryType);

    /// <summary>
    /// MSBuildLocator derives MSBuildPath from the version: major >= 16 means MSBuild\Current\Bin,
    /// anything older means MSBuild\15.0\Bin. Hardcoding 17.0 therefore handed a VS 2017 install a
    /// path that does not exist, and RegisterInstance threw instead of falling back to the SDK.
    /// Detect the layout on disk instead, and drop installs where neither directory is present.
    /// </summary>
    private static Version? DetectVersion(string installationPath) {
        if (Directory.Exists(Path.Combine(installationPath, "MSBuild", "Current", "Bin"))) return new Version(17, 0);
        if (Directory.Exists(Path.Combine(installationPath, "MSBuild", "15.0", "Bin"))) return new Version(15, 0);
        return null;
    }

    public VisualStudioInstance[] GetLocations() {
        var locs = MSBuildHelper.GetVS15Locations();
        if (locs is null) return Array.Empty<VisualStudioInstance>();

        return locs
            .Select(loc => (Path: loc, Version: DetectVersion(loc)))
            .Where(x => x.Version is not null)
            // Order by version, then path. Ordering by the path string alone put D:\VS2019 ahead of
            // C:\Program Files\Microsoft Visual Studio\2022\Enterprise.
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => CreateVisualStudioInstance(Path.GetFileName(x.Path), x.Path, x.Version!, DiscoveryType.VisualStudioSetup))
            .ToArray();
    }
}
