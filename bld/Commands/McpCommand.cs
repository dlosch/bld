using bld.Infrastructure;
using bld.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.CommandLine;

namespace bld.Commands;

internal sealed class McpCommand : BaseCommand {
    public McpCommand(IConsoleOutput console) : base("mcp", "Start the Model Context Protocol (MCP) server for agentic workflows.", console) {
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var builder = Host.CreateApplicationBuilder();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<BldMcpTools>();

        var app = builder.Build();
        await app.RunAsync(cancellationToken);
        return 0;
    }
}
