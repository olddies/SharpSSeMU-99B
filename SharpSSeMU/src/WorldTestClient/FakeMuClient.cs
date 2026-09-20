using System.Net.Sockets;
using System.Threading.Channels;
using MuServer.Shared.Crypto;
using MuServer.Shared.Protocol;

namespace WorldTestClient;

/// <summary>
/// Cliente MU simulado (para pruebas) que reproduce fielmente el protocolo del cliente real: usa
/// las claves de cifrado reales (Enc1.dat/Dec2.dat) para el login (C3, con la ofuscación XorData
/// "de ida" deducida matemáticamente -- ya validada en TestClient/Program.cs), y decodifica lo que
/// manda el servidor SIN esperar ofuscación XorData en esa dirección: se confirmó leyendo
/// SocketManager.cpp::DataSend que el servidor original NUNCA aplica XorData al enviar (solo al
/// recibir) -- así que para que el protocolo funcione de punta a punta, el cliente real tampoco
/// puede estar esperando esa capa en los paquetes que le llegan del servidor. Ver notas en
/// GameServer/Net/ClientSession.cs::SendEncryptedAsync (el lado servidor de este mismo contrato).
/// </summary>
public sealed class FakeMuClient
{
    public sealed record DecodedPacket(byte Type, byte Head, byte SubHead, byte[] Full);

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly GameStreamCipher _streamCipher;
    private readonly PacketCipher _packetCipher; // Enc1=cifra lo que manda el cliente, Dec2=descifra lo que manda el server
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
                _streamCipher.Decrypt(chunk); // descifrado de flujo sobre TODO lo que llega, apenas llega

                Array.Copy(chunk, 0, _recvBuf, _recvSize, n);
                _recvSize += n;

                ExtractPackets();
            }
        }
        catch (Exception)
        {
            // conexión cerrada -- ok para el arnés de pruebas
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
                // Desincronizado -- no debería pasar en un flujo bien formado.
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
                // C3/C4: descifrar por bloques. Sin XorData -- ver comentario de la clase.
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

        // El servidor aplica XorData-deofuscación a TODO paquete recibido, no solo a los C3/C4
        // post-descifrado (ver ExtractPacket/GameClientFramer: el branch C1/C2 también la corre).
        // Descubierto en esta prueba -- Fase 1 nunca lo necesitó porque el único paquete que manda
        // el cliente ahí es el login (C3). Acá hay que replicar la contraparte "de ida" también
        // para cualquier paquete C1/C2 que mande el cliente.
        int headerLen = copy[0] == 0xC1 ? 2 : 3;
        MuServer.Shared.Crypto.PacketCipher.ObfuscateInPlace(copy, copy.Length, headerLen);

        _streamCipher.Encrypt(copy);
        await _socket.SendAsync(copy, SocketFlags.None);
    }

    public Task SendMoveViewportEnableAsync() => SendRawAsync(PacketBuilder.BuildC1Sub(0xF3, 0x12, Array.Empty<byte>()));

    /// <summary>PMSG_CHARACTER_LIST_RECV, C1:F3:00 -- sin cuerpo. El cliente real lo manda apenas
    /// el login da result=1, antes de elegir personaje (0xF3:0x03).</summary>
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

    /// <summary>Igual que <see cref="SendMoveAsync"/> pero mandando el path TRUNCADO a 1 solo byte
    /// (en vez de los 8 completos) -- reproduce exactamente lo que manda el cliente real (confirmado
    /// con logs de producción: PMSG_MOVE_RECV declara path[8] fijo en el struct C++ pero el cliente
    /// solo manda los bytes de path que realmente necesita según la cantidad de pasos, no los 8
    /// siempre). Sirve para probar el fix de MoveRecv.Parse contra un paquete corto real.</summary>
    public Task SendMoveShortPathAsync(byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte((byte)(dir << 4)); // un solo byte de path, no los 8 completos
        return SendRawAsync(PacketBuilder.BuildC1(0xD7, w.ToArray()));
    }

    /// <summary>PMSG_ITEM_MOVE_RECV, C1:24: SourceFlag/SourceSlot/ItemInfo[5](ignorado por el
    /// servidor)/TargetFlag/TargetSlot. flag=0 en ambos lados = Inventory (único container
    /// soportado por el servidor en esta pasada de la Fase 3). Paquete de 12 bytes total
    /// (ItemManager.h:63-71) -- ver el doc-comment de ItemMoveRecv.Parse en WorldPackets.cs.</summary>
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

    /// <summary>PMSG_CHAT_RECV, C1:00 -- chat público (name=nombre propio, verificado por el
    /// servidor contra el real).</summary>
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

    /// <summary>PMSG_PARTY_REQUEST_RESULT_RECV, C1:41 -- responder a una invitación (result=1 acepta).</summary>
    public Task SendPartyRequestResultAsync(byte result, int inviterIndex)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte((byte)((inviterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(inviterIndex & 0xFF));
        return SendRawAsync(PacketBuilder.BuildC1(0x41, w.ToArray()));
    }

    /// <summary>PMSG_PARTY_DEL_MEMBER_RECV, C1:43 -- salir (number=slot propio) o expulsar (líder).</summary>
    public Task SendPartyDelMemberAsync(byte number)
    {
        return SendRawAsync(PacketBuilder.BuildC1(0x43, new[] { number }));
    }

    /// <summary>PMSG_ATTACK_RECV (Attack.h:13-19), C1:D9 -- ataque cuerpo a cuerpo básico, sin skill.</summary>
    public Task SendAttackAsync(int targetIndex, byte action, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        w.WriteByte(action);
        w.WriteByte(dir);
        return SendRawAsync(PacketBuilder.BuildC1(0xD9, w.ToArray()));
    }

    /// <summary>PMSG_SKILL_ATTACK_RECV (SkillManager.h:102-108), C3:19 en el original -- acá mandado
    /// como C1 lógico igual que el resto de los paquetes de este arnés de pruebas (el servidor lo
    /// procesa idéntico: GameClientFramer sintetiza cualquier C3 real a este mismo formato antes de
    /// llegar al dispatcher, ver GameClientFramer.cs).</summary>
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

    /// <summary>PMSG_NPC_TALK_RECV, C1:30 -- index del NPC (índice de gObj[]/MonsterRegistry).</summary>
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

    /// <summary>PMSG_ITEM_GET_RECV, C1:22 -- índice del item de piso (dentro del array de 300 slots
    /// del mapa, ver GroundItem.Index), NO un índice global de objeto.</summary>
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
