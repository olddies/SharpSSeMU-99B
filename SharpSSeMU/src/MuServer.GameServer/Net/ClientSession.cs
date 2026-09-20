using System.Net.Sockets;
using MuServer.Shared.Crypto;
using MuServer.GameServer.World;

namespace MuServer.GameServer.Net;

/// <summary>Port of the "real client connection" portion of OBJECTSTRUCT (User.h) — only what is needed for the
/// connection lifecycle and login (Phase 1). The ~300 character/inventory/combat fields of OBJECTSTRUCT are
/// added in later phases.</summary>
public sealed class ClientSession
{
    public required int Index { get; init; }
    public required Socket Socket { get; init; }
    public required string IpAddress { get; init; }
    public required GameClientFramer Framer { get; init; }
    public required GameStreamCipher StreamCipher { get; init; }
    public required PacketCipher PacketCipher { get; init; }

    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public bool LoginMessageSent { get; set; }
    public string Account { get; set; } = string.Empty;
    public bool Connected { get; set; } = true;

    /// <summary>Account level (0-3, AL0..AL3) that JoinServer returns on connecting
    /// (JoinAccountResultRecv.AccountLevel, port of WZ_GetAccountLevel -- see
    /// JoinServerProtocolHandler.OnConnectAccountAsync). It is stored here because it arrives BEFORE the
    /// PlayerObject exists (the login has not yet chosen a character); World/PlayerObject.AccountLevel copies
    /// it only on entering the world.</summary>
    public int AccountLevel { get; set; }

    /// <summary>Not null once the player enters the world (after 0xF3:03) -- see World/PlayerObject.cs.</summary>
    public PlayerObject? Player { get; set; }

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private byte _sendSerial;

    public async Task SendAsync(byte[] logicalPacket, CancellationToken ct)
    {
        // The original GameServer never applies XorData/serial when sending C1/C2 packets (see PacketCipher.cs)
        // -- only the socket stream cipher (GameStreamCipher) wraps the output. For C3/C4 use
        // SendEncryptedAsync.
        var copy = (byte[])logicalPacket.Clone();
        StreamCipher.Encrypt(copy);

        await RawSendAsync(copy, ct);
    }

    /// <summary> Exact port of CSocketManager::DataSend for the C3 case (SocketManager.cpp:437-451): the
    /// "logical" packet already comes with type=0xC3 set -- the size byte (offset 1) is taken and TEMPORARILY
    /// replaced by a serial number before block encryption (the original does exactly this swap and restores it
    /// afterwards, but here, since it is a new buffer, nothing needs restoring). Unlike receiving, sending
    /// NEVER applies XorData -- this is confirmed by reading the real DataSend, it is not an assumption.
    /// </summary>
    public async Task SendEncryptedAsync(byte[] logicalC3Packet, CancellationToken ct)
    {
        int size = logicalC3Packet.Length;

        var plainForCipher = new byte[size - 1];
        plainForCipher[0] = _sendSerial++;
        Array.Copy(logicalC3Packet, 2, plainForCipher, 1, size - 2);

        var cipherText = PacketCipher.Encrypt(plainForCipher);

        var wire = new byte[2 + cipherText.Length];
        wire[0] = 0xC3;
        wire[1] = (byte)wire.Length;
        cipherText.CopyTo(wire, 2);

        StreamCipher.Encrypt(wire);

        await RawSendAsync(wire, ct);
    }

    /// <summary> C4 variant (2-byte big-endian size) of <see cref="SendEncryptedAsync"/> -- same contract
    /// (block encryption + serial replacing the first plaintext byte, no outgoing XorData), but with a 3-byte
    /// header (type+size_hi+size_lo) instead of 2. It is needed from Phase 3 on because PMSG_ITEM_LIST_SEND
    /// (the full inventory, up to 108 slots) can exceed the 255 bytes that fit in C3's 1-byte size. The input
    /// "logical" packet must be built with PacketBuilder.BuildC2Sub (2-byte size header) -- the real type byte
    /// (0xC4) is set by this function, just as SendEncryptedAsync does for C3. </summary>
    public async Task SendEncryptedC4Async(byte[] logicalC2Packet, CancellationToken ct)
    {
        int size = logicalC2Packet.Length;

        var plainForCipher = new byte[size - 2];
        plainForCipher[0] = _sendSerial++;
        Array.Copy(logicalC2Packet, 3, plainForCipher, 1, size - 3);

        var cipherText = PacketCipher.Encrypt(plainForCipher);

        var wire = new byte[3 + cipherText.Length];
        wire[0] = 0xC4;
        int wireSize = wire.Length;
        wire[1] = (byte)((wireSize >> 8) & 0xFF);
        wire[2] = (byte)(wireSize & 0xFF);
        cipherText.CopyTo(wire, 3);

        StreamCipher.Encrypt(wire);

        await RawSendAsync(wire, ct);
    }

    private async Task RawSendAsync(byte[] wireBytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            await Socket.SendAsync(wireBytes, SocketFlags.None, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
