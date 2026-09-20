using System.Net.Sockets;
using MuServer.Shared.Protocol;

namespace MuServer.JoinServer.Data;

/// <summary> Port of CServerManager (per connection): represents a GameServer connected to the JoinServer. As
/// in ConnectServer, the original's fixed slot of MAX_SERVER=20 is simplified to one session per socket (the
/// index was an internal detail, invisible in the protocol). </summary>
public sealed class GameServerLink
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Socket Socket { get; init; }
    public required string IpAddress { get; init; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    public string ServerName { get; set; } = string.Empty;
    public ushort ServerPort { get; set; } = 0xFFFF;
    public ushort ServerCode { get; set; } = 0xFFFF;
    public int CurUserCount { get; set; }
    public int MaxUserCount { get; set; }

    public PacketFramer Framer { get; } = new(maxPacketSize: 2048);
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    public async Task SendAsync(byte[] packet, CancellationToken ct)
    {
        await SendLock.WaitAsync(ct);
        try
        {
            await Socket.SendAsync(packet, SocketFlags.None, ct);
        }
        finally
        {
            SendLock.Release();
        }
    }
}
