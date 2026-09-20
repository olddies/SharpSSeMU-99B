using System.Collections.Concurrent;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

/// <summary> Simplified port of CMapManager + CServerInfo::ReadMapInfo (loads all the "Terrain&lt;N&gt;.att" it
/// finds under Data/Terrain, N = map number + 1). The original has a config file per map (view range, PK
/// enabled, etc.) -- here only the view range is ported (ViewRange, default 12 according to
/// CMapManager::GetMapViewRange) because it is the only thing this phase's viewport needs; the rest of the map
/// config is added when the corresponding phase needs it. </summary>
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
