using System.Net;
using System.Net.Sockets;
using MuServer.ConnectServer.Data;
using MuServer.ConnectServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.ConnectServer.Net;

/// <summary>
/// Puerto de CSocketManager (lado TCP). El original usaba IOCP con hilos worker manuales;
/// acá se logra el mismo resultado (muchas conexiones concurrentes, no bloqueante) con
/// Socket.AcceptAsync/ReceiveAsync de .NET, que internamente también usa E/S asíncrona del SO
/// (epoll en Linux, IOCP en Windows) — mismo modelo, sin tener que reimplementar el plumbing manual.
///
/// Filtrado: el original rechazaba la conexión en la condición de WSAAccept (antes de completarse
/// el 3-way handshake visible a la app). Acá se acepta el socket y se cierra inmediatamente si no
/// pasa BlackList/IpManager — el efecto para el cliente (conexión rechazada) es equivalente.
/// </summary>
public sealed class TcpGateServer
{
    private readonly ushort _port;
    private readonly BlackListStore _blackList;
    private readonly IpConnectionTracker _ipTracker;
    private readonly ClientSessionManager _sessions;
    private readonly ConnectServerProtocolHandler _protocol;

    public int MaxPacketPerSecond { get; set; }
    public int MaxConnectionLifetimeSeconds { get; set; }

    private Socket? _listener;

    public TcpGateServer(
        ushort port,
        BlackListStore blackList,
        IpConnectionTracker ipTracker,
        ClientSessionManager sessions,
        ConnectServerProtocolHandler protocol,
        int maxPacketPerSecond,
        int maxConnectionLifetimeSeconds)
    {
        _port = port;
        _blackList = blackList;
        _ipTracker = ipTracker;
        _sessions = sessions;
        _protocol = protocol;
        MaxPacketPerSecond = maxPacketPerSecond;
        MaxConnectionLifetimeSeconds = maxConnectionLifetimeSeconds;
    }

    public void Start(CancellationToken ct)
    {
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Any, _port));
        _listener.Listen(5);

        Log.Add(LogColor.Black, "[SocketTCP] Server started at port [{0}]", _port);

        _ = AcceptLoopAsync(ct);
        _ = LifetimeSweepLoopAsync(ct);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;

            try
            {
                socket = await _listener!.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Add(LogColor.Red, "[SocketTCP] AcceptAsync() failed with error: {0}", ex.SocketErrorCode);
                continue;
            }

            _ = HandleClientAsync(socket, ct);
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        var remoteIp = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "0.0.0.0";
        Log.Add(LogColor.Green, "[SocketTCP] Client connected from {0}", remoteIp);

        if (!_blackList.IsAllowed(remoteIp) || !_ipTracker.CheckIpAddress(remoteIp))
        {
            socket.Close();
            return;
        }

        var session = new ClientSession { Socket = socket, IpAddress = remoteIp };

        if (!_sessions.TryAdd(session))
        {
            socket.Close();
            return;
        }

        _ipTracker.InsertIpAddress(remoteIp);

        try
        {
            await session.SendAsync(ConnectServerProtocolHandler.BuildInitPacket(true), ct);
            // El cliente real espera la lista de NOMBRES de servidor (C2:F3:EA) automáticamente al
            // conectar, antes de mostrar la pantalla de selección -- si no llega, no muestra nada y
            // se desconecta (bug real encontrado probando con el cliente real: esta llamada faltaba
            // por completo, BuildNameListPacket() nunca se invocaba desde ningún lado).
            await session.SendAsync(_protocol.BuildNameListPacket(), ct);

            var buffer = new byte[2048];

            while (!ct.IsCancellationRequested)
            {
                int received;

                try
                {
                    received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                }
                catch (SocketException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (received == 0)
                {
                    break; // peer cerró la conexión
                }

                if (!CheckPacketRate(session))
                {
                    Log.Add(LogColor.Red, "[DataRecv] Packets exceeded. (IpAddress: {0})", session.IpAddress);
                    break;
                }

                List<byte[]> packets;

                try
                {
                    packets = session.Framer.Feed(buffer.AsSpan(0, received));
                }
                catch (InvalidDataException ex)
                {
                    Log.Add(LogColor.Red, "[SocketTCP] {0} (IpAddress: {1})", ex.Message, session.IpAddress);
                    break;
                }

                foreach (var packet in packets)
                {
                    await _protocol.HandlePacketAsync(session, packet, ct);
                }
            }
        }
        finally
        {
            _sessions.Remove(session);
            _ipTracker.RemoveIpAddress(remoteIp);
            socket.Close();
        }
    }

    private bool CheckPacketRate(ClientSession session)
    {
        if (MaxPacketPerSecond <= 0)
        {
            return true;
        }

        var now = DateTime.UtcNow;

        if ((now - session.LastPacketWindowStart).TotalSeconds >= 1)
        {
            session.LastPacketWindowStart = now;
            session.PacketCountInWindow = 0;
        }

        session.PacketCountInWindow++;

        return session.PacketCountInWindow < MaxPacketPerSecond;
    }

    /// <summary>
    /// Puerto de ConnectServerTimeoutProc(). Nota de compatibilidad: en el original "MaxConnectionIdle"
    /// se mide contra m_OnlineTime (fijado solo al conectar y nunca actualizado), es decir que en la
    /// práctica funciona como un límite de duración TOTAL de la conexión, no de inactividad real.
    /// Se replica ese mismo comportamiento aquí para no alterar el comportamiento observado.
    /// </summary>
    private async Task LifetimeSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ContinueWith(_ => { }, TaskScheduler.Default);

            if (MaxConnectionLifetimeSeconds <= 0)
            {
                continue;
            }

            var limit = TimeSpan.FromSeconds(MaxConnectionLifetimeSeconds);

            foreach (var session in _sessions.Snapshot())
            {
                if (DateTime.UtcNow - session.ConnectedAt >= limit)
                {
                    Log.Add(LogColor.Black, "[{0}] Client disconnected, timeout.", session.IpAddress);
                    session.Socket.Close();
                }
            }
        }
    }
}
