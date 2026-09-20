using System.Net.Sockets;
using MuServer.Shared.Crypto;
using MuServer.GameServer.World;

namespace MuServer.GameServer.Net;

/// <summary>Puerto de la porción "conexión de cliente real" de OBJECTSTRUCT (User.h) — solo lo
/// necesario para el ciclo de vida de la conexión y el login (Fase 1). Los ~300 campos de
/// personaje/inventario/combate de OBJECTSTRUCT se agregan en fases posteriores.</summary>
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

    /// <summary>Nivel de cuenta (0-3, AL0..AL3) que devuelve JoinServer al conectar
    /// (JoinAccountResultRecv.AccountLevel, puerto de WZ_GetAccountLevel -- ver
    /// JoinServerProtocolHandler.OnConnectAccountAsync). Se guarda acá porque llega ANTES de que
    /// exista el PlayerObject (el login todavía no eligió personaje); World/PlayerObject.AccountLevel
    /// lo copia recién al entrar al mundo.</summary>
    public int AccountLevel { get; set; }

    /// <summary>No nulo una vez que el jugador entra al mundo (tras 0xF3:03) -- ver World/PlayerObject.cs.</summary>
    public PlayerObject? Player { get; set; }

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private byte _sendSerial;

    public async Task SendAsync(byte[] logicalPacket, CancellationToken ct)
    {
        // El GameServer original nunca aplica XorData/serial al enviar paquetes C1/C2 (ver
        // PacketCipher.cs) -- solo el cifrado de flujo de socket (GameStreamCipher) envuelve la
        // salida. Para C3/C4 usar SendEncryptedAsync.
        var copy = (byte[])logicalPacket.Clone();
        StreamCipher.Encrypt(copy);

        await RawSendAsync(copy, ct);
    }

    /// <summary>
    /// Puerto exacto de CSocketManager::DataSend para el caso C3 (SocketManager.cpp:437-451):
    /// el paquete "lógico" ya viene con type=0xC3 puesto -- se toma el byte de tamaño (offset 1) y
    /// se reemplaza TEMPORALMENTE por un número de serie antes de cifrar por bloques (el original
    /// hace exactamente este intercambio y lo restaura después, pero acá como es un buffer nuevo no
    /// hace falta restaurar nada). A diferencia de la recepción, el envío NUNCA aplica XorData --
    /// eso está confirmado leyendo el DataSend real, no es una suposición.
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

    /// <summary>
    /// Variante C4 (tamaño de 2 bytes big-endian) de <see cref="SendEncryptedAsync"/> -- mismo
    /// contrato (cifrado por bloques + serial reemplazando el primer byte del plaintext, sin
    /// XorData de salida), pero con cabecera de 3 bytes (type+size_hi+size_lo) en vez de 2. Se
    /// necesita a partir de la Fase 3 porque PMSG_ITEM_LIST_SEND (inventario completo, hasta 108
    /// slots) puede superar los 255 bytes que entran en el tamaño de 1 byte de C3. El paquete
    /// "lógico" de entrada debe venir armado con PacketBuilder.BuildC2Sub (cabecera de 2 bytes de
    /// tamaño) -- el byte de tipo real (0xC4) lo pone esta función, igual que SendEncryptedAsync
    /// hace con C3.
    /// </summary>
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
