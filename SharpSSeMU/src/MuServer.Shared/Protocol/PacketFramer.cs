namespace MuServer.Shared.Protocol;

/// <summary>
/// Reimplementación del algoritmo de "DataRecv" del ConnectServer/GameServer original: recibe
/// bytes crudos del socket (que pueden traer 0, 1 o varios paquetes, y pueden cortar un paquete
/// a la mitad) y va emitiendo paquetes completos a medida que se completan, conservando el resto
/// en un buffer interno para la próxima llamada — igual que hacía el "memmove" del original.
/// </summary>
public sealed class PacketFramer
{
    private readonly byte[] _buffer;
    private int _size;

    public PacketFramer(int maxPacketSize)
    {
        _buffer = new byte[maxPacketSize];
        _size = 0;
    }

    /// <summary>
    /// Agrega bytes recién leídos del socket y devuelve los paquetes completos ya armados.
    /// Lanza InvalidDataException ante cabecera/tamaño inválido (igual que el original, que
    /// en ese caso desconectaba al cliente).
    /// </summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        if (_size + data.Length > _buffer.Length)
        {
            throw new InvalidDataException("Buffer overflow: el peer envió más datos de los permitidos sin completar un paquete válido.");
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
                throw new InvalidDataException($"Cabecera de protocolo inválida: 0x{type:X2}");
            }

            if (size < 3 || size > _buffer.Length)
            {
                throw new InvalidDataException($"Tamaño de paquete inválido: {size}");
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
                break; // paquete incompleto: esperar más datos
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
