using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Crypto;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.Net;

/// <summary>Puerto de CSocketManager (lado cliente real) — acepta conexiones del cliente MU y
/// despacha su protocolo (cifrado, a diferencia de ConnectServer/JoinServer/DataServer).</summary>
public sealed class GameClientListener
{
    private const int MaxPacketSize = 4096;
    private const int StartIndex = 9000; // OBJECT_START_USER (MAX_OBJECT-MAX_OBJECT_USER = 10000-1000)
    private const int MaxIndex = 10000; // MAX_OBJECT

    private readonly ushort _port;
    private readonly PacketCipher _packetCipher;
    private readonly GameStreamCipher _streamCipher;
    private readonly ClientProtocolHandler _protocol;

    private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
    private int _nextIndex = StartIndex;
    private readonly object _indexLock = new();

    private Socket? _listener;

    public GameClientListener(ushort port, PacketCipher packetCipher, GameStreamCipher streamCipher, ClientProtocolHandler protocol)
    {
        _port = port;
        _packetCipher = packetCipher;
        _streamCipher = streamCipher;
        _protocol = protocol;
    }

    /// <summary>Usado por el heartbeat UDP 0xA1 hacia ConnectServer (UserCount).</summary>
    public int ConnectedCount => _sessions.Count;

    public void Start(CancellationToken ct)
    {
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Any, _port));
        _listener.Listen(20);

        Log.Add(LogColor.Black, "[SocketManager] Server started at port [{0}]", _port);

        _ = AcceptLoopAsync(ct);
    }

    private int AllocateIndex()
    {
        lock (_indexLock)
        {
            for (int n = 0; n < (MaxIndex - StartIndex); n++)
            {
                int candidate = _nextIndex;
                _nextIndex = _nextIndex + 1 >= MaxIndex ? StartIndex : _nextIndex + 1;

                if (!_sessions.ContainsKey(candidate))
                {
                    return candidate;
                }
            }

            return -1;
        }
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
                Log.Add(LogColor.Red, "[SocketManager] AcceptAsync() failed with error: {0}", ex.SocketErrorCode);
                continue;
            }

            _ = HandleClientAsync(socket, ct);
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        var remoteIp = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "0.0.0.0";

        int index = AllocateIndex();

        if (index < 0)
        {
            socket.Close();
            return;
        }

        var session = new ClientSession
        {
            Index = index,
            Socket = socket,
            IpAddress = remoteIp,
            Framer = new GameClientFramer(_streamCipher, _packetCipher, MaxPacketSize),
            StreamCipher = _streamCipher,
            PacketCipher = _packetCipher,
        };

        _sessions[index] = session;

        Log.Add(LogColor.Black, "[SocketManager][{0}] Client connected (IpAddress: {1})", index, remoteIp);

        await _protocol.OnConnectAsync(session, ct);

        var buffer = new byte[MaxPacketSize];

        try
        {
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
                    break;
                }

                List<GameClientFramer.DecodedPacket> packets;

                try
                {
                    packets = session.Framer.Feed(buffer.AsSpan(0, received));
                }
                catch (InvalidDataException ex)
                {
                    Log.Add(LogColor.Red, "[SocketManager][{0}] {1}", index, ex.Message);
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
            session.Connected = false;
            _sessions.TryRemove(index, out _);
            await _protocol.OnDisconnectAsync(session, CancellationToken.None);
            socket.Close();
            Log.Add(LogColor.Black, "[SocketManager][{0}] Client disconnected", index);
        }
    }
}
