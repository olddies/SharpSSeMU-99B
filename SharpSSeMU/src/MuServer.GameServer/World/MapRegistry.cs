using System.Collections.Concurrent;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto simplificado de CMapManager + CServerInfo::ReadMapInfo (carga todos los "Terrain&lt;N&gt;.att"
/// que encuentre bajo Data/Terrain, N = número de mapa + 1). El original tiene un archivo de config
/// por mapa (rango de vista, PK habilitado, etc.) -- acá solo se porta el rango de vista (ViewRange,
/// default 12 según CMapManager::GetMapViewRange) porque es lo único que necesita el viewport de
/// esta fase; el resto de la config de mapa se agrega cuando la fase correspondiente lo necesite.
/// </summary>
public sealed class MapRegistry
{
    private const int DefaultViewRange = 12;

    private readonly ConcurrentDictionary<int, GameMap> _maps = new();

    public int LoadAll(string terrainDir)
    {
        if (!Directory.Exists(terrainDir))
        {
            Log.Add(LogColor.Red, "[MapRegistry] Terrain folder does not exist: {0}", terrainDir);
            return 0;
        }

        int loaded = 0;

        foreach (var file in Directory.GetFiles(terrainDir, "Terrain*.att"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // "Terrain1", "Terrain10", ...
            var numPart = name.Replace("Terrain", "", StringComparison.OrdinalIgnoreCase);

            if (!int.TryParse(numPart, out var fileNumber) || fileNumber < 1)
            {
                continue;
            }

            int mapNumber = fileNumber - 1; // TerrainN.att -> mapa (N-1), ver Map.cpp/ServerInfo.cpp

            var map = GameMap.Load(mapNumber, file);

            if (map == null)
            {
                Log.Add(LogColor.Red, "[MapRegistry] Could not load {0}", file);
                continue;
            }

            _maps[mapNumber] = map;
            loaded++;
        }

        Log.Add(LogColor.Blue, "[MapRegistry] {0} maps loaded from {1}", loaded, terrainDir);
        return loaded;
    }

    public GameMap? GetMap(int mapNumber) => _maps.GetValueOrDefault(mapNumber);

    public bool IsValidMap(int mapNumber) => _maps.ContainsKey(mapNumber);

    public int GetViewRange(int mapNumber) => DefaultViewRange;
}
