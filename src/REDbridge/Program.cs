// REDbridge: hosts WolvenKit's engine behind JSON-RPC 2.0 over stdio.
//
//   REDbridge [--game <Cyberpunk 2077 folder>] [--keys]
//
// stdout carries the protocol (Content-Length framed, as language servers do).
// stderr carries the log. Nothing else may write to stdout.
// --keys turns on the keys.* methods (the shared key store), for REDcode and MCP servers that
// read keys; a client that lends its REDbridge to others (REDnode's host) starts it without.

using System.Text.Json;
using REDbridge;
using StreamJsonRpc;

string? gameDir = null;
var keys = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
    case "--game" when i + 1 < args.Length:
        gameDir = args[++i];
        break;
    case "--keys":
        keys = true;
        break;
    case "--help" or "-h":
        Console.Error.WriteLine("usage: REDbridge [--game <Cyberpunk 2077 folder>] [--keys]");
        return 0;
    }
}
gameDir ??= Environment.GetEnvironmentVariable("REDBRIDGE_GAME");

var host = new BridgeHost { KeysEnabled = keys };
host.Log.Info($"REDbridge {BridgeHost.Version} starting, WolvenKit {BridgeHost.WolvenKitVersion}{(keys ? ", with the key store" : "")}");

if (gameDir is not null)
{
    try
    {
        host.LoadGame(gameDir);
    }
    catch (Exception e)
    {
        host.Log.Error($"Could not load the game at '{gameDir}': {e.Message}");
    }
}

// Protocol on raw stdio streams, so no console encoding gets in the way.
using var stdout = Console.OpenStandardOutput();
using var stdin = Console.OpenStandardInput();
// camelCase keys, so JavaScript clients see { gameLoaded, files, ... }.
var formatter = new SystemTextJsonFormatter();
formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
var handler = new HeaderDelimitedMessageHandler(stdout, stdin, formatter);
using var rpc = new JsonRpc(handler, new BridgeApi(host));
rpc.StartListening();
host.Log.Info("Listening on stdio");
await rpc.Completion;
host.Log.Info("Client disconnected, exiting");
return 0;
