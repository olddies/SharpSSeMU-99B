using System.Net.Sockets;
using System.Threading.Channels;
using MuServer.Shared.Crypto;
using MuServer.Shared.Protocol;

namespace WorldTestClient;

/// <summary> Simulated MU client (for tests) that faithfully reproduces the real client's protocol: it uses the
/// real encryption keys (Enc1.dat/Dec2.dat) for the login (C3, with the "outgoing" XorData obfuscation deduced
/// mathematically -- already validated in TestClient/Program.cs), and decodes what the server sends WITHOUT
/// expecting XorData obfuscation in that direction: it was confirmed by reading SocketManager.cpp::DataSend
/// that the original server NEVER applies XorData when sending (only when receiving) -- so for the protocol to
/// work end to end, the real client cannot be expecting that layer in the packets it receives from the server
/// either. See the notes in GameServer/Net/ClientSession.cs::SendEncryptedAsync (the server side of this same
/// contract). </summary>
public sealed class FakeMuClient
{
    public sealed record DecodedPacket(byte Type, byte Head, byte SubHead, byte[] Full);

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly GameStreamCipher _streamCipher;
    private readonly PacketCipher _packetCipher; // Enc1=encrypts what the client sends, Dec2=decrypts what the server sends
    private readonly byte[] _serverSerial;
    private readonly byte[] _clientVersion;
    private readonly Channel<DecodedPacket> _incoming = Channel.CreateUnbounded<DecodedPacket>();
    private readonly byte[] _recvBuf = new byte[8192];
    private int _recvSize;

    public string Name { get; }

    public FakeMuClient(string name, string serverSerial, string clientVersion, string enc1Path, string dec2Path)
    {
        Name = name;
        _serverSerial = FixedBytes(serverSerial, 17);
        _clientVersion = FixedBytes(clientVersion, 5);
        _packetCipher = PacketCipher.LoadFromFiles(enc1Path, dec2Path);
        _streamCipher = GameStreamCipher.FromServerSerial(_serverSerial);
    }

    public static byte[] FixedBytes(string s, int len)
    {
        var b = new byte[len];
        var src = System.Text.Encoding.ASCII.GetBytes(s);
        Array.Copy(src, b, Math.Min(src.Length, len));
        return b;
    }

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        await _socket.ConnectAsync(host, port, ct);
        _ = ReceiveLoopAsync(ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _socket.ReceiveAsync(buffer, SocketFlags.None, ct);

                if (n == 0)
                {
                    break;
                }

                var chunk = new byte[n];
                Array.Copy(buffer, chunk, n);
                _streamCipher.Decrypt(chunk); // stream decryption over EVERYTHING that arrives, as soon as it arrives

                Array.Copy(chunk, 0, _recvBuf, _recvSize, n);
                _recvSize += n;

                ExtractPackets();
            }
        }
        catch (Exception)
        {
            // connection closed -- ok for the test harness
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private void ExtractPackets()
    {
        int count = 0;

        while (true)
        {
            if (_recvSize - count < 3)
            {
                break;
            }

            byte type = _recvBuf[count];
            int size;
            int headerLen;

            if (type == 0xC1 || type == 0xC3)
            {
                size = _recvBuf[count + 1];
                headerLen = 2;
            }
            else if (type == 0xC2 || type == 0xC4)
            {
                if (_recvSize - count < 4)
                {
                    break;
                }

                size = (_recvBuf[count + 1] << 8) | _recvBuf[count + 2];
                headerLen = 3;
            }
            else
            {
                // Out of sync -- it should not happen in a well-formed stream.
                _recvSize = 0;
                return;
            }

            if (count + size > _recvSize)
            {
                break; // paquete incompleto
            }

            byte[] logical;

            if (type == 0xC1 || type == 0xC2)
            {
                logical = new byte[size];
                Array.Copy(_recvBuf, count, logical, 0, size);
            }
            else
            {
                // C3/C4: block-decrypt. Without XorData -- see the class comment.
                int cipherOffset = count + headerLen;
                int cipherLen = size - headerLen;
                var plainWithSerial = _packetCipher.Decrypt(_recvBuf.AsSpan(cipherOffset, cipherLen));

                if (plainWithSerial == null || plainWithSerial.Length < 1)
                {
                    _recvSize = 0;
                    return;
                }

                int payloadLen = plainWithSerial.Length - 1;
                int logicalSize = headerLen + payloadLen;
                logical = new byte[logicalSize];
                logical[0] = (byte)(type == 0xC3 ? 0xC1 : 0xC2);

                if (headerLen == 2)
                {
                    logical[1] = (byte)logicalSize;
                }
                else
                {
                    logical[1] = (byte)((logicalSize >> 8) & 0xFF);
                    logical[2] = (byte)(logicalSize & 0xFF);
                }

                Array.Copy(plainWithSerial, 1, logical, headerLen, payloadLen);
            }

            byte head = logical[0] == 0xC1 ? logical[2] : logical[3];
            byte subh = (logical[0] == 0xC1 && logical.Length > 3) ? logical[3]
                : (logical[0] == 0xC2 && logical.Length > 4) ? logical[4] : (byte)0;

            _incoming.Writer.TryWrite(new DecodedPacket(logical[0], head, subh, logical));

            count += size;
        }

        int remaining = _recvSize - count;

        if (remaining > 0 && count > 0)
        {
            Array.Copy(_recvBuf, count, _recvBuf, 0, remaining);
        }

        _recvSize = remaining;
    }

    public async Task<DecodedPacket> WaitForAsync(byte head, TimeSpan timeout, byte? subHead = null)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await foreach (var pkt in _incoming.Reader.ReadAllAsync(cts.Token))
            {
                if (pkt.Head == head && (subHead == null || pkt.SubHead == subHead))
                {
                    return pkt;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        throw new TimeoutException($"[{Name}] Timeout esperando head 0x{head:X2}" + (subHead != null ? $":0x{subHead:X2}" : ""));
    }

    private async Task SendRawAsync(byte[] logicalC1OrC2)
    {
        var copy = (byte[])logicalC1OrC2.Clone();

        // The server applies XorData de-obfuscation to EVERY received packet, not only to C3/C4 post-decryption
        // (see ExtractPacket/GameClientFramer: the C1/C2 branch runs it too). Discovered in this test -- Phase
        // 1 never needed it because the only packet the client sends there is the login (C3). Here the
        // "outgoing" counterpart has to be replicated for any C1/C2 packet the client sends as well.
        int headerLen = copy[0] == 0xC1 ? 2 : 3;
        MuServer.Shared.Crypto.PacketCipher.ObfuscateInPlace(copy, copy.Length, headerLen);

        _streamCipher.Encrypt(copy);
        await _socket.SendAsync(copy, SocketFlags.None);
    }

    public Task SendMoveViewportEnableAsync() => SendRawAsync(PacketBuilder.BuildC1Sub(0xF3, 0x12, Array.Empty<byte>()));

    /// <summary>PMSG_CHARACTER_LIST_RECV, C1:F3:00 -- no body. The real client sends it as soon as the login
    /// gives result=1, before choosing a character (0xF3:0x03).</summary>
    public Task SendCharacterListRequestAsync() => SendRawAsync(PacketBuilder.BuildC1Sub(0xF3, 0x00, Array.Empty<byte>()));

    /// <summary>PMSG_CHARACTER_CREATE_RECV, C1:F3:01 -- name[10]+Class(1).</summary>
    public Task SendCharacterCreateAsync(string characterName, byte characterClass)
    {
        var w = new PacketWriter();
        w.WriteFixedString(characterName, 10);
        w.WriteByte(characterClass);
        return SendRawAsync(PacketBuilder.BuildC1Sub(0xF3, 0x01, w.ToArray()));
    }

    public Task SendCharacterSelectAsync(string characterName)
    {
        var w = new PacketWriter();
        w.WriteFixedString(characterName, 10);
        return SendRawAsync(PacketBuilder.BuildC1Sub(0xF3, 0x03, w.ToArray()));
    }

    /// <summary>Movimiento de un solo tile (rawCount=0): path[0] = dir&lt;&lt;4.</summary>
    public Task SendMoveAsync(byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte(x);
        w.WriteByte(y);
        var path = new byte[8];
        path[0] = (byte)(dir << 4);
        w.WriteBytes(path, 8);
        return SendRawAsync(PacketBuilder.BuildC1(0xD7, w.ToArray()));
    }

    /// <summary>Same as <see cref="SendMoveAsync"/> but sending the path TRUNCATED to a single byte (instead of
    /// the full 8) -- it reproduces exactly what the real client sends (confirmed with production logs:
    /// PMSG_MOVE_RECV declares a fixed path[8] in the C++ struct but the client only sends the path bytes it
    /// really needs according to the number of steps, not always 8). It serves to test the MoveRecv.Parse fix
    /// against a real short packet.</summary>
    public Task SendMoveShortPathAsync(byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte((byte)(dir << 4)); // un solo byte de path, no los 8 completos
        return SendRawAsync(PacketBuilder.BuildC1(0xD7, w.ToArray()));
    }

    /// <summary>PMSG_ITEM_MOVE_RECV, C1:24: SourceFlag/SourceSlot/ItemInfo[5](ignored by the
    /// server)/TargetFlag/TargetSlot. flag=0 on both sides = Inventory (the only container supported by the
    /// server in this pass of Phase 3). 12-byte packet in total (ItemManager.h:63-71) -- see the doc-comment of
    /// ItemMoveRecv.Parse in WorldPackets.cs.</summary>
    public Task SendItemMoveAsync(byte sourceSlot, byte targetSlot)
    {
        var w = new PacketWriter();
        w.WriteByte(0); // SourceFlag = Inventory
        w.WriteByte(sourceSlot);
        w.WriteBytes(new byte[5], 5); // ItemInfo (MAX_ITEM_INFO=5) -- el servidor lo ignora
        w.WriteByte(0); // TargetFlag = Inventory
        w.WriteByte(targetSlot);
        return SendRawAsync(PacketBuilder.BuildC1(0x24, w.ToArray()));
    }

    /// <summary>PMSG_CHAT_RECV, C1:00 -- public chat (name=own name, verified by the server against the real
    /// one).</summary>
    public Task SendChatAsync(string name, string message)
    {
        var w = new PacketWriter();
        w.WriteFixedString(name, 10);
        w.WriteFixedString(message, 60);
        return SendRawAsync(PacketBuilder.BuildC1(0x00, w.ToArray()));
    }

    /// <summary>PMSG_CHAT_WHISPER_RECV, C1:02 -- name=nombre del destinatario.</summary>
    public Task SendWhisperAsync(string targetName, string message)
    {
        var w = new PacketWriter();
        w.WriteFixedString(targetName, 10);
        w.WriteFixedString(message, 60);
        return SendRawAsync(PacketBuilder.BuildC1(0x02, w.ToArray()));
    }

    /// <summary>PMSG_FRIEND_LIST_RECV, C1:C0 -- sin cuerpo.</summary>
    public Task SendFriendListRequestAsync() => SendRawAsync(PacketBuilder.BuildC1(0xC0, Array.Empty<byte>()));

    /// <summary>PMSG_FRIEND_REQUEST_RECV, C1:C1 -- pedir amistad con targetName.</summary>
    public Task SendFriendRequestAsync(string targetName)
    {
        var w = new PacketWriter();
        w.WriteFixedString(targetName, 10);
        return SendRawAsync(PacketBuilder.BuildC1(0xC1, w.ToArray()));
    }

    /// <summary>PMSG_FRIEND_RESULT_RECV, C1:C2 -- aceptar/rechazar la solicitud de requesterName.</summary>
    public Task SendFriendResultAsync(byte result, string requesterName)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteFixedString(requesterName, 10);
        return SendRawAsync(PacketBuilder.BuildC1(0xC2, w.ToArray()));
    }

    /// <summary>PMSG_FRIEND_DELETE_RECV, C1:C3 -- borrar a targetName de la lista de amigos.</summary>
    public Task SendFriendDeleteAsync(string targetName)
    {
        var w = new PacketWriter();
        w.WriteFixedString(targetName, 10);
        return SendRawAsync(PacketBuilder.BuildC1(0xC3, w.ToArray()));
    }

    /// <summary>PMSG_PARTY_REQUEST_RECV, C1:40 -- invitar a targetIndex a party.</summary>
    public Task SendPartyRequestAsync(int targetIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        return SendRawAsync(PacketBuilder.BuildC1(0x40, w.ToArray()));
    }

    /// <summary>PMSG_PARTY_REQUEST_RESULT_RECV, C1:41 -- answering an invitation (result=1 accepts).</summary>
    public Task SendPartyRequestResultAsync(byte result, int inviterIndex)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte((byte)((inviterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(inviterIndex & 0xFF));
        return SendRawAsync(PacketBuilder.BuildC1(0x41, w.ToArray()));
    }

    /// <summary>PMSG_PARTY_DEL_MEMBER_RECV, C1:43 -- leave (number=own slot) or kick (leader).</summary>
    public Task SendPartyDelMemberAsync(byte number)
    {
        return SendRawAsync(PacketBuilder.BuildC1(0x43, new[] { number }));
    }

    /// <summary>PMSG_ATTACK_RECV (Attack.h:13-19), C1:D9 -- basic melee attack, without a skill.</summary>
    public Task SendAttackAsync(int targetIndex, byte action, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        w.WriteByte(action);
        w.WriteByte(dir);
        return SendRawAsync(PacketBuilder.BuildC1(0xD9, w.ToArray()));
    }

    /// <summary>PMSG_SKILL_ATTACK_RECV (SkillManager.h:102-108), C3:19 in the original -- here sent as a
    /// logical C1 like the rest of this test harness's packets (the server processes it identically:
    /// GameClientFramer synthesises any real C3 into this same format before reaching the dispatcher, see
    /// GameClientFramer.cs).</summary>
    public Task SendSkillAttackAsync(byte skill, int targetIndex, byte dis = 3)
    {
        var w = new PacketWriter();
        w.WriteByte(skill);
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        w.WriteByte(dis);
        return SendRawAsync(PacketBuilder.BuildC1(0x19, w.ToArray()));
    }

    /// <summary>PMSG_DEVIL_SQUARE_ENTER_RECV, C1:90 -- level=bracket pedido (0-based), slot=slot de
    /// INVENTARIO COMPLETO (con equipo, el servidor resta 12 antes de usarlo).</summary>
    public Task SendDevilSquareEnterAsync(byte level, byte slot)
    {
        var w = new PacketWriter();
        w.WriteByte(level);
        w.WriteByte(slot);
        return SendRawAsync(PacketBuilder.BuildC1(0x90, w.ToArray()));
    }

    /// <summary>PMSG_EVENT_REMAIN_TIME_RECV, C1:91 -- eventType=1 es Devil Square.</summary>
    public Task SendEventRemainTimeAsync(byte eventType, byte itemLevel)
    {
        var w = new PacketWriter();
        w.WriteByte(eventType);
        w.WriteByte(itemLevel);
        return SendRawAsync(PacketBuilder.BuildC1(0x91, w.ToArray()));
    }

    /// <summary>PMSG_ACTION_RECV, C1:18 -- pose/emote/sentarse (ver ActionRecv.Parse).</summary>
    public Task SendActionAsync(byte dir, byte action, int targetIndex = 0xFFFF)
    {
        var w = new PacketWriter();
        w.WriteByte(dir);
        w.WriteByte(action);
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        return SendRawAsync(PacketBuilder.BuildC1(0x18, w.ToArray()));
    }

    /// <summary>PMSG_LEVEL_UP_POINT_RECV, C1:F3:06 -- type: 0=Str,1=Dex,2=Vit,3=Ene,4=Lead.</summary>
    public Task SendLevelUpPointAsync(byte type) => SendRawAsync(PacketBuilder.BuildC1Sub(0xF3, 0x06, new[] { type }));

    /// <summary>PMSG_NPC_TALK_RECV, C1:30 -- the NPC's index (index of gObj[]/MonsterRegistry).</summary>
    public Task SendNpcTalkAsync(int npcIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((npcIndex >> 8) & 0xFF));
        w.WriteByte((byte)(npcIndex & 0xFF));
        return SendRawAsync(PacketBuilder.BuildC1(0x30, w.ToArray()));
    }

    /// <summary>PMSG_NPC_TALK_CLOSE_RECV, C1:31 -- sin cuerpo.</summary>
    public Task SendNpcTalkCloseAsync() => SendRawAsync(PacketBuilder.BuildC1(0x31, Array.Empty<byte>()));

    /// <summary>PMSG_ITEM_BUY_RECV, C1:32 -- slot dentro de la grilla 8x15 de la tienda abierta.</summary>
    public Task SendItemBuyAsync(byte shopSlot) => SendRawAsync(PacketBuilder.BuildC1(0x32, new[] { shopSlot }));

    /// <summary>PMSG_ITEM_SELL_RECV, C1:33 -- slot del inventario PROPIO (rango completo 0-107).</summary>
    public Task SendItemSellAsync(byte inventorySlot) => SendRawAsync(PacketBuilder.BuildC1(0x33, new[] { inventorySlot }));

    /// <summary>PMSG_ITEM_GET_RECV, C1:22 -- index of the ground item (within the map's 300-slot array, see
    /// GroundItem.Index), NOT a global object index.</summary>
    public Task SendItemGetAsync(int groundIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((groundIndex >> 8) & 0xFF));
        w.WriteByte((byte)(groundIndex & 0xFF));
        return SendRawAsync(PacketBuilder.BuildC1(0x22, w.ToArray()));
    }

    /// <summary>PMSG_ITEM_DROP_RECV, C1:23 -- x/y destino en el piso + slot del inventario propio.</summary>
    public Task SendItemDropAsync(byte x, byte y, byte slot)
    {
        var w = new PacketWriter();
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(slot);
        return SendRawAsync(PacketBuilder.BuildC1(0x23, w.ToArray()));
    }

    private static void ClientArgumentXor(byte[] outBuf, byte[] inBuf)
    {
        byte[] table = { 0xFC, 0xCF, 0xAB };
        for (int n = 0; n < outBuf.Length; n++) outBuf[n] = (byte)(inBuf[n] ^ table[n % 3]);
    }

    public async Task SendLoginAsync(string account, string password)
    {
        var accXor = new byte[10];
        var passXor = new byte[10];
        ClientArgumentXor(accXor, FixedBytes(account, 10));
        ClientArgumentXor(passXor, FixedBytes(password, 10));

        const int logicalSize = 49;
        var logical = new byte[logicalSize];
        logical[0] = 0xC1;
        logical[1] = logicalSize;
        logical[2] = 0xF1;
        logical[3] = 0x01;
        accXor.CopyTo(logical, 4);
        passXor.CopyTo(logical, 14);
        BitConverter.GetBytes((uint)Environment.TickCount).CopyTo(logical, 24);
        _clientVersion.CopyTo(logical, 28);
        _serverSerial.AsSpan(0, 16).CopyTo(logical.AsSpan(33, 16));

        PacketCipher.ObfuscateInPlace(logical, logicalSize, headerLength: 2);

        var bplain = new byte[48];
        bplain[0] = 1;
        Array.Copy(logical, 2, bplain, 1, 47);

        var cipherText = _packetCipher.Encrypt(bplain);

        var wire = new byte[2 + cipherText.Length];
        wire[0] = 0xC3;
        wire[1] = (byte)wire.Length;
        cipherText.CopyTo(wire, 2);

        _streamCipher.Encrypt(wire);
        await _socket.SendAsync(wire, SocketFlags.None);
    }

    public void Close() => _socket.Close();
}
