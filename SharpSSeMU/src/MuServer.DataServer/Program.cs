using MuServer.DataServer.Config;
using MuServer.DataServer.Data;
using MuServer.DataServer.Db;
using MuServer.DataServer.Net;
using MuServer.DataServer.Protocol;
using MuServer.Shared.Logging;

// Puerto funcional del DataServer original (SSeMU 0.99B) a .NET 8. Persiste personajes/inventario/
// rankings/reset/etc contra PostgreSQL (en vez de SQL Server/ODBC) y habla el mismo protocolo
// binario con el/los GameServer, sin cambios para ellos.

var baseDir = AppContext.BaseDirectory;
Log.Configure(Path.Combine(baseDir, "LOG"));

Log.Add(LogColor.Black, "SSeMU DataServer (C# port) iniciando...");

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

Log.Add(LogColor.Blue, "DataServer listo en el puerto TCP {0}. Comandos: 'reload allowlist' | 'reload badsyntax' | 'exit'", config.DataServerPort);

while (!cts.Token.IsCancellationRequested)
{
    var line = Console.ReadLine();

    if (line == null)
    {
        // Sin consola interactiva: no hay comandos que leer, pero el servidor sigue corriendo (mismo
        // bug que GameServer/Program.cs, portado igual -- antes esto mataba el proceso apenas
        // arrancaba en background/sin stdin real).
        Log.Add(LogColor.Blue, "Sin consola interactiva: los comandos quedan deshabilitados, el servidor sigue corriendo.");

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
