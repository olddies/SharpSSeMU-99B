using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto de MONSTER_SET_BASE_INFO (MonsterSetBase.h:9-20) -- una fila de la tabla de spawn de un
/// mapa. <see cref="Type"/> es la sección del archivo (0=punto fijo, 1=caja+cantidad, 2=punto fijo
/// con jitter ±3, ya resuelto a X/Y en <see cref="Load"/>). Los tipos 3/4 (variantes de NPC, ver
/// MonsterSetBase.cpp) se leen pero no se instancian (MonsterRegistry.SpawnAll solo arma monstruos
/// de combate reales, MONSTER_INFO.Type==0).
/// </summary>
public sealed class MonsterSpawnEntry
{
    public required int Type { get; init; }
    public required int MonsterClass { get; init; }
    public required int Map { get; init; }
    public int Dis { get; init; } // radio de patrulla/leash -- IA no portada todavía en esta fase (Fase 4 primera pasada = monstruos estáticos), se guarda para cuando se agregue.
    public int X { get; init; }
    public int Y { get; init; }
    public int TX { get; init; } // esquina opuesta de la caja (Type==1)
    public int TY { get; init; }
    public int Dir { get; init; }
}

/// <summary>
/// Puerto de CMonsterSetBase::LoadSpawn/GetPosition/GetBoxPosition (MonsterSetBase.cpp:28-217) --
/// lee Data/Monster/Spawn/"NNN - NombreDeMapa.txt" (el número de mapa sale del nombre del archivo,
/// no de adentro). Formato MemScript: cada archivo tiene una o más secciones (encabezado = número de
/// tipo), cada sección termina en "end", el archivo entero termina en EOF.
/// </summary>
public static class MonsterSpawnTable
{
    public static List<MonsterSpawnEntry> LoadAll(string spawnDir)
    {
        var entries = new List<MonsterSpawnEntry>();

        if (!Directory.Exists(spawnDir))
        {
            Log.Add(LogColor.Red, "[MonsterSpawnTable] No existe la carpeta de spawn: {0}", spawnDir);
            return entries;
        }

        int filesLoaded = 0;

        foreach (var file in Directory.GetFiles(spawnDir, "*.txt"))
        {
            var name = Path.GetFileNameWithoutExtension(file);

            // Puerto de MonsterSetBase.cpp:50-52: los primeros 3 chars deben ser dígitos, seguidos
            // de " - " -- el número de mapa es el prefijo, el resto del nombre es solo descriptivo.
            if (name.Length < 6 || !char.IsDigit(name[0]) || !char.IsDigit(name[1]) || !char.IsDigit(name[2])
                || name[3] != ' ' || name[4] != '-' || name[5] != ' ')
            {
                continue;
            }

            if (!int.TryParse(name[..3], out var map))
            {
                continue;
            }

            LoadFile(file, map, entries);
            filesLoaded++;
        }

        Log.Add(LogColor.Blue, "[MonsterSpawnTable] {0} archivo(s) de spawn cargados ({1} fila(s) de spawn en total)", filesLoaded, entries.Count);
        return entries;
    }

    private static void LoadFile(string path, int map, List<MonsterSpawnEntry> entries)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MonsterSpawnTable] {0}", script.GetLastError());
            return;
        }

        var rand = Random.Shared;

        while (true)
        {
            if (script.GetToken() == TokenResult.End)
            {
                break;
            }

            int section = script.GetNumber();

            while (true)
            {
                if (script.GetToken() == TokenResult.End)
                {
                    break;
                }

                if (script.GetString() == "end")
                {
                    break;
                }

                int monsterClass = script.GetNumber();

                switch (section)
                {
                    case 0:
                    {
                        int dis = script.GetAsNumber();
                        int x = script.GetAsNumber();
                        int y = script.GetAsNumber();
                        int dir = script.GetAsNumber();
                        dir = dir == -1 ? rand.Next(8) : dir;
                        entries.Add(new MonsterSpawnEntry { Type = 0, MonsterClass = monsterClass, Map = map, Dis = dis, X = x, Y = y, Dir = dir });
                        break;
                    }

                    case 2:
                    {
                        // Puerto de MonsterSetBase.cpp:126-128: jitter ±3 resuelto en el momento de carga.
                        int dis = script.GetAsNumber();
                        int x = script.GetAsNumber();
                        int y = script.GetAsNumber();
                        int dir = script.GetAsNumber();
                        dir = dir == -1 ? rand.Next(8) : dir;
                        int jx = (x - 3) + rand.Next(7);
                        int jy = (y - 3) + rand.Next(7);
                        entries.Add(new MonsterSpawnEntry { Type = 0, MonsterClass = monsterClass, Map = map, Dis = dis, X = jx, Y = jy, Dir = dir });
                        break;
                    }

                    case 1:
                    {
                        int dis = script.GetAsNumber();
                        int x = script.GetAsNumber();
                        int y = script.GetAsNumber();
                        int tx = script.GetAsNumber();
                        int ty = script.GetAsNumber();
                        int dir = script.GetAsNumber();
                        dir = dir == -1 ? rand.Next(8) : dir;
                        int count = script.GetAsNumber();

                        for (int n = 0; n < count; n++)
                        {
                            entries.Add(new MonsterSpawnEntry { Type = 1, MonsterClass = monsterClass, Map = map, Dis = dis, X = x, Y = y, TX = tx, TY = ty, Dir = dir });
                        }

                        break;
                    }

                    case 4:
                    {
                        // Puerto de MonsterSetBase.cpp Type==4 ("posición de evento", MonsterSetBase.h)
                        // -- mismo layout de columnas que el tipo 0 (radio, x, y, dirección), pero estas
                        // filas NO se instancian al arrancar el servidor: son el POOL de posiciones
                        // candidatas del que CDevilSquare::SetMonster (y equivalentes de Blood/Chaos
                        // Castle) elige al azar para spawnear monstruos de evento en runtime -- ver
                        // World/DevilSquare.cs (Fase 6, primera pasada) y Monster.SpawnEntry.Type==4.
                        int dis = script.GetAsNumber();
                        int x = script.GetAsNumber();
                        int y = script.GetAsNumber();
                        int dir = script.GetAsNumber();
                        dir = dir == -1 ? rand.Next(8) : dir;
                        entries.Add(new MonsterSpawnEntry { Type = 4, MonsterClass = monsterClass, Map = map, Dis = dis, X = x, Y = y, Dir = dir });
                        break;
                    }

                    default:
                        // Secciones 3/4 (variantes de NPC) u otras no reconocidas: se descartan los
                        // tokens restantes de la fila token por token (sin asumir cantidad de campos)
                        // para no desincronizar el resto del archivo.
                        while (true)
                        {
                            var s = script.GetAsString();

                            if (s == "end" || s.Length == 0)
                            {
                                break;
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary>Puerto de CMonsterSetBase::GetPosition/GetBoxPosition (MonsterSetBase.cpp:168-217)
    /// -- para spawns de tipo caja (Type==1), intenta hasta 100 puntos al azar dentro del rectángulo
    /// y rechaza tiles con atributo bloqueado (1=zona segura, 4/8=bloqueo); si todos fallan, cae al
    /// punto inicial de la caja (mejor que no spawnear nada).</summary>
    public static (int X, int Y) ResolvePosition(MonsterSpawnEntry entry, GameMap? map)
    {
        if (entry.Type != 1)
        {
            return (entry.X, entry.Y);
        }

        var rand = Random.Shared;
        int minX = Math.Min(entry.X, entry.TX);
        int maxX = Math.Max(entry.X, entry.TX);
        int minY = Math.Min(entry.Y, entry.TY);
        int maxY = Math.Max(entry.Y, entry.TY);

        for (int attempt = 0; attempt < 100; attempt++)
        {
            int x = minX + rand.Next(maxX - minX + 1);
            int y = minY + rand.Next(maxY - minY + 1);

            if (map == null || !(map.CheckAttr(x, y, 1) || map.IsBlocked(x, y)))
            {
                return (x, y);
            }
        }

        return (minX, minY);
    }
}
