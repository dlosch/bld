using bld.Models;

namespace bld.Infrastructure;
#pragma warning disable CS9113 // Parameter is unread.
internal class ProjCfgCache(IConsoleOutput Console) {
#pragma warning restore CS9113 // Parameter is unread.
    private HashSet<ProjCfg> _cache = new HashSet<ProjCfg>(new ProjCfgEqualityComparer());
    private HashSet<string> _cacheTxt = new HashSet<string>();

    public int Count => _cache.Count;

    public bool Add(ProjCfg projCfg) {
        lock (_cache) {
            var key = $"{projCfg.Path}|#|{projCfg.Configuration?.ToLowerInvariant()}";

            if (_cache.Contains(projCfg)) return false;
            else {
                // todo check of the comparer works and remove
                if (_cacheTxt.Contains(key)) {
                    return false;
                }
            }

            _cache.Add(projCfg);
            _cacheTxt.Add(key);
            return true;
        }
    }
}
