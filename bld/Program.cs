using bld.Commands;

namespace bld;

public class Program {
    public static async Task<int> Main(string[] args) {
        var rootCommand = new RootCommand();
        return await rootCommand.Parse(args).InvokeAsync();
    }
}
