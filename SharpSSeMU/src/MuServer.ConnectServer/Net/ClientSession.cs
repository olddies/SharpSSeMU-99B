using System.Net.Sockets;
using MuServer.Shared.Protocol;

namespace MuServer.ConnectServer.Net;

/// <summary>
/// Puerto de CClientManager (por-conexión). En el original cada slot tenía un índice fijo dentro
/// de un array de 100 (MAX_CLIENT) con IOCP manual; acá se simplifica a una sesión por socket
/// (el índice interno era invisible para el cliente/protocolo, así que no afecta compatibilidad),
/// con un semáforo para serializar los envíos (equivalente al IoSideBuffer/OnSend del original).
/// </summary>
public sealed class ClientSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Socket Socket { get; init; }
    public required string IpAddress { get; init; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public DateTime LastPacketWindowStart { get; set; } = DateTime.UtcNow;
    public int PacketCountInWindow;

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
