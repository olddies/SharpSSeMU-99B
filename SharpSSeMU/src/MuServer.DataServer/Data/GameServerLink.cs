using System.Net.Sockets;
using MuServer.Shared.Protocol;

namespace MuServer.DataServer.Data;

/// <summary>Port of CServerManager (per connection), same pattern as in JoinServer.</summary>
public sealed class GameServerLink
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Socket Socket { get; init; }
    public required string IpAddress { get; init; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    public string ServerName { get; set; } = string.Empty;
    public ushort ServerPort { get; set; } = 0xFFFF;
    public ushort ServerCode { get; set; } = 0xFFFF;

    public PacketFramer Framer { get; } = new(maxPacketSize: 8192);
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
