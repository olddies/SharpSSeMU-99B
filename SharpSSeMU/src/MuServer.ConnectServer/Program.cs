using MuServer.ConnectServer;
using MuServer.ConnectServer.Data;
using MuServer.ConnectServer.Net;
using MuServer.ConnectServer.Protocol;
using MuServer.Shared.Logging;

// Puerto funcional del ConnectServer original (SSeMU 0.99B) a .NET 8, pensado para correr en
// Linux. Mismo protocolo binario (C1/C2, 0xF4:02/03, 0xF3:EA, heartbeats UDP 0xA1/0xA2) y mismos
// archivos de configuración (ConnectServer.ini, BlackList.txt, ServerList.dat) que el original,
// para que el main.exe/Main.dll del cliente se conecte sin ningún cambio.

var baseDir = AppContext.BaseDirectory;
Log.Configure(Path.Combine(baseDir, "LOG"));

Log.Add(LogColor.Black, "SSeMU ConnectServer (C# port) iniciando...");

var config = ConnectServerConfig.Load(Path.Combine(baseDir, "ConnectServer.ini"));

var blackList = new BlackListStore();
blackList.Load(Path.Combine(baseDir, "BlackList.txt"));

var serverList = new ServerListStore();
serverList.Load(Path.Combine(baseDir, "ServerList.dat"));

var ipTracker = new IpConnectionTracker(config.MaxConnectionPerIp);
var sessions = new ClientSessionManager(maxClients: 100);
var protocolHandler = new ConnectServerProtocolHandler(serverList);

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var tcpServer = new TcpGateServer(
    config.TcpPort,
    blackList,
    ipTracker,
    sessions,
    protocolHandler,
    config.MaxPacketPerSecond,
    config.MaxConnectionIdleSeconds);

var udpServer = new UdpHeartbeatServer(config.UdpPort, serverList);

tcpServer.Start(cts.Token);
udpServer.Start(cts.Token);

// Equivalente a TIMER_1000 del original: expira estados de GameServer/JoinServer sin heartbeat.
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

        serverList.MainProc();
    }
});

Log.Add(LogColor.Blue, "ConnectServer listo. TCP:{0} UDP:{1}. Comandos: 'reload blacklist' | 'reload serverlist' | 'reload config' | 'exit'", config.TcpPort, config.UdpPort);

while (!cts.Token.IsCancellationRequested)
{
    var line = Console.ReadLine();

    if (line == null)
    {
        // Sin consola interactiva (lanzado como servicio/en background, con stdin redirigido o
        // cerrado): no hay comandos que leer, pero el servidor SÍ tiene que seguir corriendo. Antes
        // se salía acá, y eso mataba el proceso apenas arrancaba en cuanto no había una consola de
        // verdad detrás (mismo bug que GameServer/Program.cs, portado igual).
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
        case "reload blacklist":
            blackList.Load(Path.Combine(baseDir, "BlackList.txt"));
            Log.Add(LogColor.Blue, "BlackList reloaded successfully");
            break;

        case "reload serverlist":
            serverList.Load(Path.Combine(baseDir, "ServerList.dat"));
            Log.Add(LogColor.Blue, "ServerList reloaded successfully");
            break;

        case "reload config":
            config = ConnectServerConfig.Load(Path.Combine(baseDir, "ConnectServer.ini"));
            ipTracker.MaxConnectionPerIp = config.MaxConnectionPerIp;
            tcpServer.MaxPacketPerSecond = config.MaxPacketPerSecond;
            tcpServer.MaxConnectionLifetimeSeconds = config.MaxConnectionIdleSeconds;
            Log.Add(LogColor.Blue, "Config reloaded successfully");
            break;

        case "exit":
        case "quit":
            cts.Cancel();
            break;
    }
}
