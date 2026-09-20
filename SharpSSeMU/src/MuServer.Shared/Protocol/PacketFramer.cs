using MuServer.Shared.Localization;
namespace MuServer.Shared.Protocol;

/// <summary> Reimplementation of the "DataRecv" algorithm of the original ConnectServer/GameServer: it receives
/// raw bytes from the socket (which may bring 0, 1 or several packets, and may cut a packet in half) and emits
/// complete packets as they are completed, keeping the rest in an internal buffer for the next call — the same
/// as the original's "memmove" did. </summary>
public sealed class PacketFramer
{
    private readonly byte[] _buffer;
    private int _size;

    public PacketFramer(int maxPacketSize)
    {
        _buffer = new byte[maxPacketSize];
        _size = 0;
    }

    /// <summary> Adds bytes just read from the socket and returns the already built complete packets. Throws
    /// InvalidDataException on an invalid header/size (like the original, which disconnected the client in that
    /// case). </summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        if (_size + data.Length > _buffer.Length)
        {
            throw new InvalidDataException(Loc.T("Buffer overflow: the peer sent more data than allowed without completing a valid packet."));
        }

        data.CopyTo(_buffer.AsSpan(_size));
        _size += data.Length;

        var packets = new List<byte[]>();
        int count = 0;

        while (true)
        {
            if (_size - count < 3)
            {
                break;
            }

            byte type = _buffer[count];
            int size;

            if (type == PacketType.C1 || type == PacketType.C3)
            {
                size = _buffer[count + 1];
            }
            else if (type == PacketType.C2 || type == PacketType.C4)
            {
                if (_size - count < 4)
                {
                    break;
                }

                size = (_buffer[count + 1] << 8) | _buffer[count + 2];
            }
            else
            {
                throw new InvalidDataException(Loc.F("Invalid protocol header: 0x{0:X2}", type));
            }

            if (size < 3 || size > _buffer.Length)
            {
                throw new InvalidDataException(Loc.F("Invalid packet size: {0}", size));
            }

            if (count + size <= _size)
            {
                var packet = new byte[size];
                Array.Copy(_buffer, count, packet, 0, size);
                packets.Add(packet);
                count += size;

                if (count >= _size)
                {
                    break;
                }
            }
            else
            {
                break; // incomplete packet: wait for more data
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
