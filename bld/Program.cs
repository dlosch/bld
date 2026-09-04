using System.Text;
using bld.Commands;
using bld.Infrastructure;

namespace bld;

public class Program {
    public static async Task<int> Main(string[] args) {
        NuGetAssemblyResolver.Register();
        Console.OutputEncoding = Encoding.UTF8;
        var rootCommand = new RootCommand();
        return await rootCommand.Parse(args).InvokeAsync();
    }
}
