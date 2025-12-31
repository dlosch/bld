using bld.Models;

namespace bld.Services;

public record class DirResult(DirectoryInfo Directory, List<Dir> References);
public record class MarkDeleteResult(List<DirResult> Directories) {
    public MarkDeleteResult() : this(new List<DirResult>()) { }
}

public interface IMarkDeleteResultProcessor {
    Task ProcessAsync(MarkDeleteResult result);
}
