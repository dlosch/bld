using bld.Services;

namespace bld.Infrastructure;
#pragma warning disable CS9113 // Parameter is unread.
internal class FileSystem(IConsoleOutput Console, ErrorSink errorSink) : IFileSystem {
#pragma warning restore CS9113 // Parameter is unread.
    // todo 
    public string FullyQualifyPath(string path) => Path.GetFullPath(path);
}
