namespace MuServer.GameServer.World;

/// <summary> Port of CMap (Map.h/Map.cpp): a flat grid of attribute bytes per tile, loaded from
/// "Terrain&lt;N&gt;.att" (N = map number + 1). File format, confirmed byte by byte against the package's real
/// .att files (65539 bytes = 3 of header + 256*256 of grid): BYTE head (unused, ignored just like the original)
/// BYTE width  (real width = width+1) BYTE height (real height = height+1) BYTE attr[width*height]  -- indexed
/// [y*height + x] (same as the original, see Map.cpp:124 etc; it is only correct because the maps are square,
/// replicated as is) Attribute bits (no named enum in the original, they are used as literals at each call
/// site): 1 = safe zone/town (little used, only a spawn fallback) 2 = "occupied" -- a dynamic bit that is
/// turned on/off at runtime (SetStandAttr/DelStandAttr), NOT recorded in the file 4 = block (wall/obstacle) --
/// recorded in the file 8 = second block bit (water/map limit in other MU forks) -- treated the same as 4 to
/// validate movement in this phase </summary>
public sealed class GameMap
{
    public int MapNumber { get; }
    public int Width { get; }
    public int Height { get; }

    private readonly byte[] _attr;

    private GameMap(int mapNumber, int width, int height, byte[] attr)
    {
        MapNumber = mapNumber;
        Width = width;
        Height = height;
        _attr = attr;
    }

    public static GameMap? Load(int mapNumber, string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);

        if (bytes.Length < 3)
        {
            return null;
        }

        int width = bytes[1] + 1;
        int height = bytes[2] + 1;

        if (width <= 0 || height <= 0 || width > 256 || height > 256)
        {
            return null;
        }

        int expected = 3 + (width * height);

        if (bytes.Length < expected)
        {
            return null;
        }

        var attr = new byte[width * height];
        Array.Copy(bytes, 3, attr, 0, attr.Length);

        return new GameMap(mapNumber, width, height, attr);
    }

    private int IndexOf(int x, int y) => (y * Height) + x;

    public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    public byte GetAttr(int x, int y) => InBounds(x, y) ? _attr[IndexOf(x, y)] : (byte)4;

    public bool CheckAttr(int x, int y, byte mask) => (GetAttr(x, y) & mask) != 0;

    /// <summary>Puerto de CMap::SetStandAttr -- marca el tile como ocupado (bit 2).</summary>
    public void SetStandAttr(int x, int y)
    {
        if (InBounds(x, y))
        {
            _attr[IndexOf(x, y)] |= 2;
        }
    }

    /// <summary>Puerto de CMap::DelStandAttr -- libera el tile (bit 2).</summary>
    public void DelStandAttr(int x, int y)
    {
        if (InBounds(x, y))
        {
            _attr[IndexOf(x, y)] &= 0xFD;
        }
    }

    /// <summary>Un tile es "pisable" si no tiene bloqueo (4/8) -- usado para validar movimiento.</summary>
    public bool IsBlocked(int x, int y) => CheckAttr(x, y, 4) || CheckAttr(x, y, 8);

    /// <summary>Un tile es zona segura/ciudad si tiene activo el bit 1.</summary>
    public bool IsSafeZone(int x, int y) => CheckAttr(x, y, 1);
}
