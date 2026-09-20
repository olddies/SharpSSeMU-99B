using System.Net.Sockets;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Net;

/// <summary>
/// Puerto mínimo de la porción "cliente de DataServer" (DataServerConnect/DataServerMsgProc de
/// GameServer.cpp + DSProtocol.cpp). Fase 1 solo necesita el handshake 0x00 (info de servidor);
/// el resto del protocolo (lista/crear/borrar personaje, guardado, etc.) se agrega en Fase 2.
/// </summary>
public sealed class DataServerConnection
{
    private readonly string _address;
    private readonly ushort _port;
    private readonly string _serverName;
    private readonly ushort _serverPort;
    private readonly ushort _serverCode;
    private readonly Func<CharacterInfoFromDataServer, CancellationToken, Task> _onCharacterInfo;
    private readonly Func<CharacterListFromDataServer, CancellationToken, Task> _onCharacterList;
    private readonly Func<CharacterCreateResultFromDataServer, CancellationToken, Task> _onCharacterCreate;
    private readonly Func<GlobalWhisperResultFromDataServer, CancellationToken, Task> _onGlobalWhisperResult;
    private readonly Func<GlobalWhisperEchoFromDataServer, CancellationToken, Task> _onGlobalWhisperEcho;
    private readonly Func<byte[], CancellationToken, Task> _onFriend;
    private readonly Func<byte[], CancellationToken, Task>? _onWarehouse;

    private Socket? _socket;
    private readonly PacketFramer _framer = new(maxPacketSize: 8192);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected { get; private set; }

    public DataServerConnection(
        string address, ushort port, string serverName, ushort serverPort, ushort serverCode,
        Func<CharacterInfoFromDataServer, CancellationToken, Task> onCharacterInfo,
        Func<CharacterListFromDataServer, CancellationToken, Task> onCharacterList,
        Func<CharacterCreateResultFromDataServer, CancellationToken, Task> onCharacterCreate,
        Func<GlobalWhisperResultFromDataServer, CancellationToken, Task> onGlobalWhisperResult,
        Func<GlobalWhisperEchoFromDataServer, CancellationToken, Task> onGlobalWhisperEcho,
        Func<byte[], CancellationToken, Task> onFriend,
        Func<byte[], CancellationToken, Task>? onWarehouse = null)
    {
        _address = address;
        _port = port;
        _serverName = serverName;
        _serverPort = serverPort;
        _serverCode = serverCode;
        _onCharacterInfo = onCharacterInfo;
        _onCharacterList = onCharacterList;
        _onCharacterCreate = onCharacterCreate;
        _onGlobalWhisperResult = onGlobalWhisperResult;
        _onGlobalWhisperEcho = onGlobalWhisperEcho;
        _onFriend = onFriend;
        _onWarehouse = onWarehouse;
    }

    public void Start(CancellationToken ct) => _ = ConnectLoopAsync(ct);

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await _socket.ConnectAsync(_address, _port, ct);
                IsConnected = true;

                Log.Add(LogColor.Blue, "[DataServer] Connected to {0}:{1}", _address, _port);

                var w = new PacketWriter();
                w.WriteByte(0);
                w.WriteUInt16(_serverPort);
                w.WriteFixedString(_serverName, 50);
                w.WriteUInt16(_serverCode);
                w.WriteUInt32(0); // FreeSize
                await SendAsync(PacketBuilder.BuildC1(0x00, w.ToArray()), ct);

                await ReceiveLoopAsync(ct);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // reintenta abajo
            }
            catch (Exception ex)
            {
                Log.Add(LogColor.Red, "[DataServer] Error: {0}", ex.Message);
            }

            IsConnected = false;
            _socket?.Close();

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            int received = await _socket!.ReceiveAsync(buffer, SocketFlags.None, ct);

            if (received == 0)
            {
                break;
            }

            var packets = _framer.Feed(buffer.AsSpan(0, received));

            foreach (var packet in packets)
            {
                byte head = packet[0] == 0xC1 ? packet[2] : packet[3];

                switch (head)
                {
                    case 0x01:
                        await _onCharacterList(CharacterListFromDataServer.Parse(packet), ct);
                        break;

                    case 0x02:
                        await _onCharacterCreate(CharacterCreateResultFromDataServer.Parse(packet), ct);
                        break;

                    case 0x04:
                        await _onCharacterInfo(CharacterInfoFromDataServer.Parse(packet), ct);
                        break;

                    case 0x05:
                        if (_onWarehouse != null) await _onWarehouse(packet, ct);
                        break;

                    case 0x72:
                        await _onGlobalWhisperResult(GlobalWhisperResultFromDataServer.Parse(packet), ct);
                        break;

                    case 0x73:
                        await _onGlobalWhisperEcho(GlobalWhisperEchoFromDataServer.Parse(packet), ct);
                        break;

                    case 0xB0:
                        await _onFriend(packet, ct);
                        break;

                    default:
                        Log.Add(LogColor.Black, "[DataServer] Head 0x{0:X2} recibido (dispatch completo pendiente para fases siguientes)", head);
                        break;
                }
            }
        }
    }

    public async Task SendAsync(byte[] packet, CancellationToken ct)
    {
        if (_socket == null || !IsConnected)
        {
            return;
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(packet, SocketFlags.None, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
