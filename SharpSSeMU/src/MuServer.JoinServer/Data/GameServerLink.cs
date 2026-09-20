using System.Net.Sockets;
using MuServer.Shared.Protocol;

namespace MuServer.JoinServer.Data;

/// <summary>
/// Puerto de CServerManager (por-conexión): representa un GameServer conectado al JoinServer.
/// Igual que en ConnectServer, se simplifica el slot fijo de MAX_SERVER=20 del original a una
/// sesión por socket (el índice era un detalle interno, invisible en el protocolo).
/// </summary>
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
