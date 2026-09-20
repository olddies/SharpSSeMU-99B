namespace MuServer.GameServer.World;

/// <summary>
/// Puerto de CMap (Map.h/Map.cpp): una grilla plana de bytes de atributo por tile, cargada de
/// "Terrain&lt;N&gt;.att" (N = número de mapa + 1). Formato del archivo, confirmado byte a byte contra
/// los .att reales del paquete (65539 bytes = 3 de cabecera + 256*256 de grilla):
///   BYTE head (sin uso, se ignora igual que el original)
///   BYTE width  (ancho real = width+1)
///   BYTE height (alto real = height+1)
///   BYTE attr[width*height]  -- indexado [y*height + x] (igual que el original, ver Map.cpp:124 etc;
///                                solo es correcto porque los mapas son cuadrados, se replica tal cual)
///
/// Bits de atributo (sin enum con nombre en el original, se usan como literales en cada call site):
///   1 = zona segura/ciudad (poco usado, solo un fallback de spawn)
///   2 = "ocupado" -- bit dinámico que se prende/apaga en runtime (SetStandAttr/DelStandAttr), NO
///       viene grabado en el archivo
///   4 = bloqueo (pared/obstáculo) -- grabado en el archivo
///   8 = segundo bit de bloqueo (agua/límite de mapa en otros forks de MU) -- tratado igual que 4
///       para validar movimiento en esta fase
/// </summary>
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
