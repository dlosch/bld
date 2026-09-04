using bld.Models;

namespace bld.Infrastructure;
#pragma warning disable CS9113 // Parameter is unread.
internal class ProjCfgCache(IConsoleOutput Output) {
#pragma warning restore CS9113 // Parameter is unread.
    private HashSet<ProjCfg> _cache = new HashSet<ProjCfg>(new ProjCfgEqualityComparer());

    // Read under the same lock as Add: HashSet is not safe for concurrent read/write and callers
    // query Count while Parallel.ForEachAsync is still adding.
    public int Count {
        get { lock (_cache) { return _cache.Count; } }
    }

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
