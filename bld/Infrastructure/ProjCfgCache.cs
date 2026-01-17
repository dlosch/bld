using bld.Models;

namespace bld.Infrastructure;
#pragma warning disable CS9113 // Parameter is unread.
internal class ProjCfgCache(IConsoleOutput Console) {
#pragma warning restore CS9113 // Parameter is unread.
    private HashSet<ProjCfg> _cache = new HashSet<ProjCfg>(new ProjCfgEqualityComparer());

    public int Count => _cache.Count;

    public bool Add(ProjCfg projCfg) {
        lock (_cache) {
            if (_cache.Contains(projCfg)) {
                return false;
            }

            _cache.Add(projCfg);
            return true;
        }
    }
}
