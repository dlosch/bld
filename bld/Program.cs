using System.Text;
using bld.Commands;

namespace bld;

public class Program {
    public static async Task<int> Main(string[] args) {
        Console.OutputEncoding = Encoding.UTF8;
        var rootCommand = new RootCommand();
        return await rootCommand.Parse(args).InvokeAsync();
    }
}
