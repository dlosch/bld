using bld.Infrastructure;
using bld.Models;
using bld.Services;

namespace bld.Commands;

internal class RootCommand : System.CommandLine.RootCommand {

    public RootCommand() : base("bld") {
        IConsoleOutput console = new SpectreConsoleOutput(LogLevel.Warning);

        Add(new CleanCommand(console));
        Add(new StatsCommand(console));
        Add(new SlnxCommand(console));
    }
}
