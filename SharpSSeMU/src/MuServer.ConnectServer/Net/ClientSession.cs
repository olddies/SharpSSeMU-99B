using System.Net.Sockets;
using MuServer.Shared.Protocol;

namespace MuServer.ConnectServer.Net;

/// <summary> Port of CClientManager (per connection). In the original each slot had a fixed index within an
/// array of 100 (MAX_CLIENT) with manual IOCP; here it is simplified to one session per socket (the internal
/// index was invisible to the client/protocol, so it does not affect compatibility), with a semaphore to
/// serialise sends (equivalent to the original's IoSideBuffer/OnSend). </summary>
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
