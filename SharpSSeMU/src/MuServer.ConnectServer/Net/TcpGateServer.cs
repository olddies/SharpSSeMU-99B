using System.Net;
using System.Net.Sockets;
using MuServer.ConnectServer.Data;
using MuServer.ConnectServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.ConnectServer.Net;

/// <summary> Port of CSocketManager (TCP side). The original used IOCP with manual worker threads; here the
/// same result (many concurrent, non-blocking connections) is achieved with .NET's
/// Socket.AcceptAsync/ReceiveAsync, which internally also uses the OS's asynchronous I/O (epoll on Linux, IOCP
/// on Windows) — same model, without having to reimplement the manual plumbing. Filtering: the original
/// rejected the connection in the WSAAccept condition (before the 3-way handshake visible to the app
/// completed). Here the socket is accepted and closed immediately if it does not pass BlackList/IpManager — the
/// effect for the client (connection rejected) is equivalent. </summary>
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
            // The real client expects the server NAME list (C2:F3:EA) automatically on connecting, before
            // showing the selection screen -- if it does not arrive, it shows nothing and disconnects (real bug
            // found testing with the real client: this call was missing entirely, BuildNameListPacket() was
            // never invoked from anywhere).
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
                    break; // peer closed the connection
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

    /// <summary> Port of ConnectServerTimeoutProc(). Compatibility note: in the original "MaxConnectionIdle" is
    /// measured against m_OnlineTime (set only on connecting and never updated), which means that in practice
    /// it works as a limit on the TOTAL duration of the connection, not on real inactivity. The same behaviour
    /// is replicated here so as not to alter the observed behaviour. </summary>
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
