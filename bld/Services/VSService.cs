using bld.Infrastructure;
using Microsoft.Build.Locator;
using System.Runtime.CompilerServices;

namespace bld.Services;

internal class VSService {

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    extern static VisualStudioInstance CreateVisualStudioInstance(string name, string path, Version version, DiscoveryType discoveryType);

    public VisualStudioInstance[] GetLocations() {
        var locs = MSBuildHelper.GetVS15Locations();
        if (locs is { }) {

            return locs
                .OrderByDescending(loc => loc)
                .Select(loc => {
                    var dir = Path.GetFileName(loc);
                    return CreateVisualStudioInstance(dir, loc, new Version(17, 0), DiscoveryType.VisualStudioSetup);
                }).ToArray();

        }

        return Array.Empty<VisualStudioInstance>();
    }
}
