using System.Text.Json;
using PCL.Core.Shared;

namespace PCL.Linux.Cli;

internal static class TorCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var bridges = McmConfiguration.Bridges().ToList();
        if (args is ["status"])
        {
            Console.WriteLine($"Tor fallback: {McmConfiguration.Get("tor.enabled")}");
            Console.WriteLine($"Automatic bridges: {McmConfiguration.Get("tor.auto_bridges")}");
            Console.WriteLine($"Custom bridges: {bridges.Count}");
            Console.WriteLine("Tor starts on demand. Run mcm tor test to test the connection.");
            return 0;
        }
        if (args is ["test"] or ["bridge", "test"])
        {
            await using var service = await TorNetworkService.StartAsync(bridges, autoBridges: args is ["test"] &&
                bool.TryParse(McmConfiguration.Get("tor.auto_bridges"), out var allowed) && allowed).ConfigureAwait(false);
            Console.WriteLine("Tor connected.");
            return 0;
        }
        if (args is ["bridge", "list"])
        {
            for (var index = 0; index < bridges.Count; index++) Console.WriteLine($"{index + 1}. Configured bridge");
            return 0;
        }
        if (args is ["bridge", "clear"]) bridges.Clear();
        else if (args is ["bridge", "add", var line])
        {
            line = TorNetworkService.ValidateBridge(line);
            if (!bridges.Contains(line, StringComparer.Ordinal)) bridges.Add(line);
        }
        else if (args is ["bridge", "remove", var number] && int.TryParse(number, out var index) && index > 0 && index <= bridges.Count)
            bridges.RemoveAt(index - 1);
        else
        {
            Console.Error.WriteLine("Usage: mcm tor status | test | bridge list | bridge add <bridge-line> | bridge remove <number> | bridge test | bridge clear");
            return 2;
        }
        var config = McmConfiguration.Load();
        config["tor.bridges"] = JsonSerializer.SerializeToNode(bridges);
        McmConfiguration.Save(config);
        Console.WriteLine("Tor bridge configuration updated.");
        return 0;
    }
}
