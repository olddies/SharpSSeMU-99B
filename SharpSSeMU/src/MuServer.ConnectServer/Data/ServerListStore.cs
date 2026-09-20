using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;
using MuServer.Shared.Scripting;

namespace MuServer.ConnectServer.Data;

/// <summary> Port of CServerList: loads ServerList.dat, receives the UDP "heartbeats" from each GameServer
/// (0xA1) and from the JoinServer (0xA2), and builds the server list/name packets the client asks for. Same
/// expiry logic (10s without heartbeat => considered down). </summary>
public sealed class ServerListStore
{
    private const int MaxJoinServerQueueSize = 100;
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<int, ServerListInfo> _servers = new();
    private readonly object _sync = new();

    private bool _joinServerState;
    private DateTime _joinServerStateTime;
    private uint _joinServerQueueSize;

    public void Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, script.GetLastError());
            return;
        }

        lock (_sync)
        {
            _servers.Clear();

            while (true)
            {
                if (script.GetToken() == TokenResult.End)
                {
                    break;
                }

                if (script.GetString() == "end")
                {
                    break;
                }

                var serverCode = script.GetNumber();
                var serverName = script.GetAsString();
                var serverAddress = script.GetAsString();
                var serverPort = (ushort)script.GetAsNumber();
                var serverShow = script.GetAsNumber() > 0;

                _servers[serverCode] = new ServerListInfo
                {
                    ServerCode = serverCode,
                    ServerName = serverName,
                    ServerAddress = serverAddress,
                    ServerPort = serverPort,
                    ServerShow = serverShow,
                };
            }
        }

        Log.Add(LogColor.Blue, "ServerList loaded: {0} servers", _servers.Count);
    }

    /// <summary>Run every 1s (same as the original's TIMER_1000): expires states without heartbeat.</summary>
    public void MainProc()
    {
        lock (_sync)
        {
            if (_joinServerState && (DateTime.UtcNow - _joinServerStateTime) > LiveTimeout)
            {
                _joinServerState = false;
                Log.Add(LogColor.Red, "[SocketUDP] JoinServer disconnected");
            }

            foreach (var info in _servers.Values)
            {
                if (info.ServerState && (DateTime.UtcNow - info.ServerStateTime) > LiveTimeout)
                {
                    info.ServerState = false;
                    Log.Add(LogColor.Red, "[SocketUDP] GameServer disconnected [{0}] [{1}:{2}][{3}]",
                        info.ServerName, info.ServerAddress, info.ServerPort, info.ServerCode);
                }
            }
        }
    }

    public bool CheckJoinServerState()
    {
        lock (_sync)
        {
            return _joinServerState && _joinServerQueueSize <= MaxJoinServerQueueSize;
        }
    }

    public ServerListInfo? GetServerListInfo(int serverCode)
    {
        lock (_sync)
        {
            return _servers.GetValueOrDefault(serverCode);
        }
    }

    /// <summary>Construye el payload de PMSG_SERVER_LIST_SEND (0xC2 F4:02).</summary>
    public byte[] BuildServerListPacket()
    {
        lock (_sync)
        {
            using var ms = new MemoryStream();

            byte count = 0;

            if (CheckJoinServerState())
            {
                foreach (var info in _servers.Values)
                {
                    if (!info.ServerShow || !info.ServerState)
                    {
                        continue;
                    }

                    ushort userTotalPct = info.UserTotal == 0
                        ? (ushort)0
                        : (ushort)Math.Min(100, (info.UserCount * 100) / info.UserTotal);

                    ms.WriteByte((byte)(info.ServerCode & 0xFF));
                    ms.WriteByte((byte)((info.ServerCode >> 8) & 0xFF)); // WORD ServerCode, little-endian
                    ms.WriteByte((byte)userTotalPct);                    // BYTE UserTotal (%)
                    ms.WriteByte(0xCC);                                  // BYTE type
                    count++;
                }
            }

            var body = new byte[1 + ms.Length];
            body[0] = count;
            ms.ToArray().CopyTo(body, 1);

            return PacketBuilder.BuildC2Sub(0xF4, 0x02, body);
        }
    }

    /// <summary>Construye el payload de PMSG_SERVER_NAME_LIST_SEND (0xC2 F3:EA).</summary>
    public byte[] BuildServerNameListPacket()
    {
        lock (_sync)
        {
            using var ms = new MemoryStream();

            byte count = 0;

            if (CheckJoinServerState())
            {
                foreach (var info in _servers.Values)
                {
                    if (!info.ServerState)
                    {
                        continue;
                    }

                    ms.WriteByte((byte)(info.ServerCode & 0xFF));
                    ms.WriteByte((byte)((info.ServerCode >> 8) & 0xFF)); // WORD index, little-endian
                    ms.Write(PacketBuilder.FixedString(info.ServerName, 32));
                    count++;
                }
            }

            var body = new byte[1 + ms.Length];
            body[0] = count;
            ms.ToArray().CopyTo(body, 1);

            return PacketBuilder.BuildC2Sub(0xF3, 0xEA, body);
        }
    }

    /// <summary>Construye PMSG_SERVER_INFO_SEND (0xC1 F4:03) para un ServerCode puntual.</summary>
    public byte[]? BuildServerInfoPacket(int serverCode)
    {
        if (!CheckJoinServerState())
        {
            return null;
        }

        var info = GetServerListInfo(serverCode);

        if (info == null || !info.ServerShow || !info.ServerState)
        {
            return null;
        }

        using var ms = new MemoryStream();
        ms.Write(PacketBuilder.FixedString(info.ServerAddress, 16));
        ms.WriteByte((byte)(info.ServerPort & 0xFF));
        ms.WriteByte((byte)((info.ServerPort >> 8) & 0xFF)); // WORD ServerPort, little-endian

        return PacketBuilder.BuildC1Sub(0xF4, 0x03, ms.ToArray());
    }

    /// <summary>Heartbeat 0xA1 recibido por UDP desde un GameServer.</summary>
    public void OnGameServerLive(int serverCode, uint userCount, uint userTotal)
    {
        lock (_sync)
        {
            if (!_servers.TryGetValue(serverCode, out var info))
            {
                return;
            }

            if (!info.ServerState)
            {
                Log.Add(LogColor.Green, "[SocketUDP] GameServer connected [{0}] [{1}:{2}][{3}]",
                    info.ServerName, info.ServerAddress, info.ServerPort, info.ServerCode);
            }

            info.ServerState = true;
            info.ServerStateTime = DateTime.UtcNow;
            info.UserCount = userCount;
            info.UserTotal = userTotal;
        }
    }

    /// <summary>Heartbeat 0xA2 received over UDP from the JoinServer.</summary>
    public void OnJoinServerLive(uint queueSize)
    {
        lock (_sync)
        {
            if (!_joinServerState)
            {
                Log.Add(LogColor.Green, "[SocketUDP] JoinServer connected");
            }

            _joinServerState = true;
            _joinServerStateTime = DateTime.UtcNow;
            _joinServerQueueSize = queueSize;
        }
    }
}
