using MuServer.Shared.Localization;
namespace MuServer.Shared.Crypto;

/// <summary> Full framer of the real-client protocol of the original GameServer (much more complex than
/// PacketFramer.cs, which only serves ConnectServer/JoinServer/DataServer). It exactly replicates
/// CSocketManager::OnRecv + DataRecv + CPacketManager::ExtractPacket: 1) The raw bytes just read from the
/// socket are decrypted in place with GameStreamCipher (stateless stream cipher, byte by byte — it does not
/// matter in which chunk they arrive). 2) Packets are separated by header C1/C2 (plain) or C3/C4
/// (block-encrypted). 3) C3/C4 packets are decrypted with PacketCipher (11→8 byte blocks) and "synthesised"
/// back into a logical C1/C2 packet (the first decrypted byte is a serial number, not the real head — it is
/// discarded/recorded but not validated yet, see the HackCheck note). 4) To ALL packets (whether original C1/C2
/// or the synthesised result of C3/C4) XorData is applied: a chained XOR de-obfuscation over the bytes after
/// the header. Only C1 and C3 are really in use in this original package (there is no C2/C4 struct on the
/// client-server side in Protocol.h) — C2/C4 support is implemented for completeness but does not have the same
/// level of verification as C1/C3. </summary>
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
            throw new InvalidDataException(Loc.T("Buffer overflow: the client sent more data than allowed without completing a valid packet."));
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
                throw new InvalidDataException(Loc.F("Invalid protocol header: 0x{0:X2}", type));
            }

            if (size < 3 || size > _buffer.Length)
            {
                throw new InvalidDataException(Loc.F("Invalid packet size: {0}", size));
            }

            if (count + size > _size)
            {
                break; // incomplete packet, wait for more data
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
                // The block cipher starts right after type+size (same as the original DataRecv:
                // Decrypt(&DecBuff[1],&lpMsg[count+2],(size-2)) for C3, offset+3/size-3 for C4 -- WITHOUT extra
                // bytes in between). The ALREADY DECRYPTED first byte is the "serial", not the real head.
                int cipherOffset = count + headerLen;
                int cipherLen = size - headerLen;

                var plainWithSerial = _packetCipher.Decrypt(_buffer.AsSpan(cipherOffset, cipherLen));

                if (plainWithSerial == null || plainWithSerial.Length < 1)
                {
                    throw new InvalidDataException(Loc.T("Invalid encrypted-block checksum (corrupt packet or hack)."));
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
