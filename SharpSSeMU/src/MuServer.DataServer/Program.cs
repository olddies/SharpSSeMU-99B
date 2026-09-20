using MuServer.Shared.Config;
using MuServer.Shared.Localization;
using MuServer.DataServer.Config;
using MuServer.DataServer.Data;
using MuServer.DataServer.Db;
using MuServer.DataServer.Net;
using MuServer.DataServer.Protocol;
using MuServer.Shared.Logging;

// Functional port of the original DataServer (SSeMU 0.99B) to .NET 8. It persists characters/inventory/
// rankings/reset/etc against PostgreSQL (instead of SQL Server/ODBC) and speaks the same binary protocol with
// the GameServer(s), with no changes for them.

var baseDir = AppContext.BaseDirectory;
Log.Configure(Path.Combine(baseDir, "LOG"));

Loc.Configure(IniFile.Load(Path.Combine(baseDir, "DataServer.ini")).GetString("DataServerInfo", "Language", "en"));  // en | es

Log.Add(LogColor.Black, "SSeMU DataServer (C# port) starting...");

var config = DataServerConfig.Load(Path.Combine(baseDir, "DataServer.ini"));

var allowList = new AllowableIpStore();
allowList.Load(Path.Combine(baseDir, "AllowableIpList.txt"));

var badSyntax = new BadSyntaxStore();
badSyntax.Load(Path.Combine(baseDir, "BadSyntax.txt"));

var repo = new NpgsqlCharacterDataRepository(config.PostgresConnectionString);
var sessions = new CharacterSessionStore();
var registry = new GameServerRegistry();
var protocolHandler = new DataServerProtocolHandler(repo, sessions, registry, badSyntax);

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var gameServerLink = new GameServerLinkServer(config.DataServerPort, allowList, registry, sessions, protocolHandler);
gameServerLink.Start(cts.Token);

Log.Add(LogColor.Blue, "DataServer ready on TCP port {0}. Commands: 'reload allowlist' | 'reload badsyntax' | 'exit'", config.DataServerPort);

while (!cts.Token.IsCancellationRequested)
{
    var line = Console.ReadLine();

    if (line == null)
    {
        // No interactive console: there are no commands to read, but the server keeps running (same bug as
        // GameServer/Program.cs, ported the same way -- this used to kill the process as soon as it started in
        // the background/without a real stdin).
        Log.Add(LogColor.Blue, "No interactive console: commands are disabled, the server keeps running.");

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        break;
    }

    switch (line.Trim().ToLowerInvariant())
    {
        case "reload allowlist":
            allowList.Load(Path.Combine(baseDir, "AllowableIpList.txt"));
            Log.Add(LogColor.Blue, "AllowableIpList reloaded successfully");
            break;

        case "reload badsyntax":
            badSyntax.Load(Path.Combine(baseDir, "BadSyntax.txt"));
            Log.Add(LogColor.Blue, "BadSyntax reloaded successfully");
            break;

        case "exit":
        case "quit":
            cts.Cancel();
            break;
    }
}
