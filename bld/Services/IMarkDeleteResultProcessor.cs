using bld.Models;

namespace bld.Services;

internal record class DirResult(DirectoryInfo Directory, List<Dir> References);
internal record class MarkDeleteResult(List<DirResult> Directories);

internal interface IMarkDeleteResultProcessor {
    Task ProcessAsync(MarkDeleteResult result);
}
