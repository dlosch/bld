using bld.Models;

namespace bld.Services;

internal interface IProjectProcessor {
    Task ProcessAsync(ProjCfg projCfg, ProjectInfo projInfo);
}
