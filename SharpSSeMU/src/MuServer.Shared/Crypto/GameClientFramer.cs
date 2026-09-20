namespace MuServer.Shared.Crypto;

/// <summary>
/// Framer completo del protocolo cliente-real del GameServer original (mucho más complejo que
/// PacketFramer.cs, que solo sirve para ConnectServer/JoinServer/DataServer). Replica exactamente
/// CSocketManager::OnRecv + DataRecv + CPacketManager::ExtractPacket:
///
///   1) Los bytes crudos recién leídos del socket se descifran en el lugar con GameStreamCipher
///      (cifrado de flujo sin estado, byte a byte — no importa en qué chunk llegan).
///   2) Se separan paquetes por cabecera C1/C2 (planos) o C3/C4 (cifrados por bloques).
///   3) Los paquetes C3/C4 se descifran con PacketCipher (bloques de 11→8 bytes) y se "sintetizan"
///      de vuelta a un paquete lógico C1/C2 (el primer byte descifrado es un número de serie, no
///      el head real — se descarta/registra pero no se valida todavía, ver nota HackCheck).
///   4) A TODOS los paquetes (ya sean C1/C2 originales o el resultado sintetizado de C3/C4) se les
///      aplica XorData: una des-ofuscación XOR encadenada sobre los bytes después de la cabecera.
///
/// Solo C1 y C3 están realmente en uso en este paquete original (no hay ningún struct C2/C4 del
/// lado cliente-servidor en Protocol.h) — el soporte C2/C4 se implementa por completitud pero no
/// tiene el mismo nivel de verificación que C1/C3.
/// </summary>
public sealed class GameClientFramer
{
    public sealed record DecodedPacket(byte[] Data, int Serial, bool WasEncrypted);

    private readonly GameStreamCipher _streamCipher;
    private readonly PacketCipher _packetCipher;
    private readonly byte[] _buffer;
    private int _size;

    public GameClientFramer(GameStreamCipher streamCipher, PacketCipher packetCipher, int maxPacketSize)
    {
        _streamCipher = streamCipher;
        _packetCipher = packetCipher;
        _buffer = new byte[maxPacketSize];
        _size = 0;
    }

    public List<DecodedPacket> Feed(ReadOnlySpan<byte> rawData)
    {
        if (_size + rawData.Length > _buffer.Length)
        {
            throw new InvalidDataException("Buffer overflow: el cliente envió más datos de los permitidos sin completar un paquete válido.");
        }

        var dest = _buffer.AsSpan(_size, rawData.Length);
        rawData.CopyTo(dest);
        _streamCipher.Decrypt(dest);
        _size += rawData.Length;

        var packets = new List<DecodedPacket>();
        int count = 0;

        while (true)
        {
            if (_size - count < 3)
            {
                break;
            }

            byte type = _buffer[count];
            int size;
            int headerLen;

            if (type == 0xC1 || type == 0xC3)
            {
                size = _buffer[count + 1];
                headerLen = 2;
            }
            else if (type == 0xC2 || type == 0xC4)
            {
                if (_size - count < 4)
                {
                    break;
                }

                size = (_buffer[count + 1] << 8) | _buffer[count + 2];
                headerLen = 3;
            }
            else
            {
                throw new InvalidDataException($"Cabecera de protocolo inválida: 0x{type:X2}");
            }

            if (size < 3 || size > _buffer.Length)
            {
                throw new InvalidDataException($"Tamaño de paquete inválido: {size}");
            }

            if (count + size > _size)
            {
                break; // paquete incompleto, esperar más datos
            }

            if (type == 0xC1 || type == 0xC2)
            {
                var logical = new byte[size];
                Array.Copy(_buffer, count, logical, 0, size);
                PacketCipher.DeobfuscateInPlace(logical, size, headerLen);
                packets.Add(new DecodedPacket(logical, -1, false));
            }
            else
            {
                // El cifrado por bloques arranca justo después de type+size (igual que DataRecv original:
                // Decrypt(&DecBuff[1],&lpMsg[count+2],(size-2)) para C3, offset+3/size-3 para C4 -- SIN
                // bytes extra de por medio). El primer byte YA DESCIFRADO es el "serial", no el head real.
                int cipherOffset = count + headerLen;
                int cipherLen = size - headerLen;

                var plainWithSerial = _packetCipher.Decrypt(_buffer.AsSpan(cipherOffset, cipherLen));

                if (plainWithSerial == null || plainWithSerial.Length < 1)
                {
                    throw new InvalidDataException("Checksum de bloque cifrado inválido (paquete corrupto o hack).");
                }

                int serial = plainWithSerial[0];
                int payloadLen = plainWithSerial.Length - 1; // sin el byte de serial
                int logicalSize = headerLen + payloadLen;

                var logical = new byte[logicalSize];
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
                PacketCipher.DeobfuscateInPlace(logical, logicalSize, headerLen);

                packets.Add(new DecodedPacket(logical, serial, true));
            }

            count += size;

            if (count >= _size)
            {
                break;
            }
        }

        int remaining = _size - count;

        if (remaining > 0 && count > 0)
        {
            Array.Copy(_buffer, count, _buffer, 0, remaining);
        }

        _size = remaining;

        return packets;
    }
}
