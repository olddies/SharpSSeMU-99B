using MuServer.Shared.Config;
using MuServer.Shared.Localization;
using MuServer.JoinServer.Config;
using MuServer.JoinServer.Data;
using MuServer.JoinServer.Db;
using MuServer.JoinServer.Net;
using MuServer.JoinServer.Protocol;
using MuServer.Shared.Logging;

// Functional port of the original JoinServer (SSeMU 0.99B) to .NET 8. It authenticates accounts against
// PostgreSQL (instead of SQL Server/ODBC) and speaks the same binary protocol with the GameServer(s)
// (C1:00/01/02/05/11/20/30) and the UDP heartbeat 0xA2 towards ConnectServer, with no changes for them.

var baseDir = AppContext.BaseDirectory;
Log.Configure(Path.Combine(baseDir, "LOG"));

Loc.Configure(IniFile.Load(Path.Combine(baseDir, "JoinServer.ini")).GetString("JoinServerInfo", "Language", "en"));  // en | es

Log.Add(LogColor.Black, "SSeMU JoinServer (C# port) starting...");

var config = JoinServerConfig.Load(Path.Combine(baseDir, "JoinServer.ini"));

var allowList = new AllowableIpStore();
allowList.Load(Path.Combine(baseDir, "AllowableIpList.txt"));

var repo = new NpgsqlAccountRepository(config.PostgresConnectionString);
var sessions = new AccountSessionStore(config.CaseSensitive);
var registry = new GameServerRegistry();
var protocolHandler = new JoinServerProtocolHandler(repo, sessions, registry, config.CaseSensitive, config.Md5Encryption);

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var gameServerLink = new GameServerLinkServer(config.JoinServerPort, allowList, registry, protocolHandler);
gameServerLink.Start(cts.Token);

var heartbeat = new ConnectServerHeartbeatClient(config.ConnectServerAddress, config.ConnectServerPort);
heartbeat.Start(cts.Token);

// Equivalente a TIMER_1000 -> gAccountManager.DisconnectProc().
_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(1000, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        try
        {
            await protocolHandler.RunStaleMapMoveSweepAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Add(LogColor.Red, "DisconnectProc error: {0}", ex.Message);
        }
    }
});

Log.Add(LogColor.Blue, "JoinServer ready. TCP:{0} -> ConnectServer {1}:{2}. Commands: 'reload allowlist' | 'exit'",
    config.JoinServerPort, config.ConnectServerAddress, config.ConnectServerPort);

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

        case "exit":
        case "quit":
            cts.Cancel();
            break;
    }
}
