namespace MuServer.Shared.Protocol;

/// <summary>
/// Cursor de lectura secuencial sobre un paquete recibido — evita calcular offsets a mano en
/// structs grandes (como SDHP_CHARACTER_INFO_SAVE_RECV, con ~30 campos). Cada Read* avanza la
/// posición interna, igual que leer campo por campo de un struct C++ empaquetado a 1 byte.
/// </summary>
public sealed class PacketReader
{
    private readonly byte[] _data;
    private int _pos;

    public PacketReader(byte[] data, int start)
    {
        _data = data;
        _pos = start;
    }

    public int Position => _pos;

    public byte ReadByte() => _data[_pos++];

    public ushort ReadUInt16()
    {
        var v = PacketBuilder.ReadUInt16LE(_data.AsSpan(_pos, 2));
        _pos += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        var v = PacketBuilder.ReadUInt32LE(_data.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    public string ReadFixedString(int length)
    {
        int available = Math.Max(_data.Length - _pos, 0);
        int readLen = Math.Min(length, available);
        var v = PacketBuilder.ReadFixedString(_data.AsSpan(_pos, readLen));
        _pos += readLen;
        return v;
    }

    public byte[] ReadBytes(int length)
    {
        var v = _data.AsSpan(_pos, length).ToArray();
        _pos += length;
        return v;
    }

    public void Skip(int length) => _pos += length;
}

/// <summary>Constructor secuencial de paquetes salientes (complemento simétrico de PacketReader).</summary>
public sealed class PacketWriter
{
    private readonly MemoryStream _ms = new();

    public void WriteByte(byte value) => _ms.WriteByte(value);

    public void WriteUInt16(ushort value) => _ms.Write(PacketBuilder.WriteUInt16LE(value));

    public void WriteUInt32(uint value) => _ms.Write(PacketBuilder.WriteUInt32LE(value));

    public void WriteFixedString(string? value, int length) => _ms.Write(PacketBuilder.FixedString(value, length));

    public void WriteBytes(byte[]? value, int length)
    {
        if (value == null)
        {
            _ms.Write(new byte[length]);
            return;
        }

        if (value.Length == length)
        {
            _ms.Write(value);
            return;
        }

        // Ajusta a la longitud exacta esperada por el struct (rellena con 0 o trunca), para no
        // desincronizar el layout binario si el blob guardado tiene un tamaño ligeramente distinto.
        var fixedBuf = new byte[length];
        Array.Copy(value, fixedBuf, Math.Min(value.Length, length));
        _ms.Write(fixedBuf);
    }

    public byte[] ToArray() => _ms.ToArray();
}
