using bld.Commands;
using bld.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace bld;

public class Program {
    public static async Task<int> Main(string[] args) {
        // Check if running as MCP server
        if (args.Length > 0 && args[0] == "mcp") {
            await RunMcpServerAsync(args.Skip(1).ToArray());
            return 0;
        }

        var rootCommand = new RootCommand();
        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task RunMcpServerAsync(string[] args) {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<BldMcpTools>();

        var app = builder.Build();
        await app.RunAsync();
    }
}
