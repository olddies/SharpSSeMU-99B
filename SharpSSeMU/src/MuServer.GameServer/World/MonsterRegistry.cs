using System.Collections.Concurrent;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto simplificado del recorte "monstruos" de gObj[10000] (índices 0-7999,
/// OBJECT_START_MONSTER..MAX_OBJECT_MONSTER, ver User.h:8-13) -- reemplaza gObjAddMonster/
/// gObjSetPosMonster/gObjSetMonster (Monster.cpp:169-365, todos invocados desde
/// CMonsterManager::SetMonsterData en el arranque, GameMain.cpp:35) con un diccionario de
/// monstruos vivos + un allocator de índices lineal (igual de simple que el original, que
/// también usa un allocator de free-list/scan lineal, ver Monster.cpp:645-687).
/// </summary>
public sealed class MonsterRegistry
{
    private const int StartIndex = 0;
    private const int MaxIndex = 8000; // MAX_OBJECT_MONSTER

    private readonly ConcurrentDictionary<int, Monster> _monsters = new();
    private readonly object _allocLock = new();
    private int _nextIndex = StartIndex;

    public IEnumerable<Monster> All => _monsters.Values;

    public bool TryGet(int index, out Monster monster) => _monsters.TryGetValue(index, out monster!);

    /// <summary>Puerto de CMonsterManager::SetMonsterData (MonsterManager.cpp:281-325) -- instancia
    /// un Monster por cada fila de spawn cuya MONSTER_INFO exista y sea de tipo "monstruo real"
    /// (Type==0; NPCs no se instancian todavía, ver comentario de MonsterInfo.cs). Devuelve la
    /// cantidad de monstruos efectivamente creados.</summary>
    public int SpawnAll(IReadOnlyList<MonsterSpawnEntry> entries, MonsterInfoTable infoTable, MapRegistry maps)
    {
        int spawned = 0;

        foreach (var entry in entries)
        {
            if (entry.Type == 4)
            {
                continue; // pool de posiciones de evento (Type==4, ver MonsterSpawnTable) -- NO se
                          // instancia al arrancar, solo lo consume DevilSquareManager en runtime
                          // (Fase 6), igual que el original (CMonsterSetBase no las carga en SetMonsterData).
            }

            var info = infoTable.Get(entry.MonsterClass);

            if (info == null)
            {
                continue; // clase desconocida
            }

            var map = maps.GetMap(entry.Map);
            var (x, y) = MonsterSpawnTable.ResolvePosition(entry, map);

            int index = AllocateIndex();

            if (index < 0)
            {
                Log.Add(LogColor.Red, "[MonsterRegistry] Sin índices libres -- se alcanzó el límite de {0} monstruos", MaxIndex);
                break;
            }

            float scaledMaxLife = ScaleRate(info.MaxLife);

            var monster = new Monster
            {
                Index = index,
                MonsterClass = entry.MonsterClass,
                SpawnEntry = entry,
                Name = info.Name,
                Level = info.Level,
                MaxLife = scaledMaxLife,
                Life = scaledMaxLife,
                Map = (byte)entry.Map,
                X = (byte)x,
                Y = (byte)y,
                StartX = (byte)x,
                StartY = (byte)y,
                TX = (byte)x,
                TY = (byte)y,
                Dir = (byte)Math.Clamp(entry.Dir, 0, 7),
                DamageMin = ScaleRate(info.DamageMin, WorldPacketBuilder.ServerInfo.MonsterPhysiDamageRate),
                DamageMax = ScaleRate(info.DamageMax, WorldPacketBuilder.ServerInfo.MonsterPhysiDamageRate),
                Defense = ScaleRate(info.Defense, WorldPacketBuilder.ServerInfo.MonsterDefenseRate),
                AttackSuccessRate = ScaleRate(info.AttackRate, WorldPacketBuilder.ServerInfo.MonsterAttackSuccessRateRate),
                DefenseSuccessRate = ScaleRate(info.DefenseRate, WorldPacketBuilder.ServerInfo.MonsterDefenseSuccessRateRate),
                AttackRange = info.AttackRange,
                AttackType = info.AttackType,
                MonsterSkill = info.MonsterSkill,
                ViewRange = info.ViewRange,
                AttackSpeed = info.AttackSpeed,
                ItemRate = info.ItemRate,
                MoneyRate = info.MoneyRate,
                MaxRegenMillis = info.RegenTime * 1000,
                ShopNumber = (info.Type != 0) ? entry.MonsterClass : null,
            };

            map?.SetStandAttr(x, y);
            _monsters[index] = monster;
            spawned++;
        }

        Log.Add(LogColor.Blue, "[MonsterRegistry] {0} monstruo(s) instanciados", spawned);
        return spawned;
    }

    /// <summary>Puerto de los multiplicadores globales de <c>CMonsterManager::SetInfo</c>
    /// (MonsterManager.cpp:166-183) -- aplicados una sola vez al spawnear, sobre las columnas crudas
    /// de MonsterList.txt (ver <see cref="MuServer.GameServer.Config.ServerInfoConfig"/>). Con los
    /// valores reales del pack (todos =100) esto es un no-op; el mecanismo de escalado en sí no
    /// estaba enchufado antes de portar <c>GameServerInfo - Common.dat</c>.</summary>
    private static int ScaleRate(int value, int ratePercent) => (int)((long)value * ratePercent / 100);

    private static float ScaleRate(float value) => value * WorldPacketBuilder.ServerInfo.MonsterMaxLifeRate / 100f;

    /// <summary>Puerto de gObjMonsterRegen (Monster.cpp:367-428) -- revive un monstruo en su punto
    /// de spawn original (re-resolviendo la posición si era una caja aleatoria), llamado desde el
    /// tick de respawn (ver ClientProtocolHandler.MonsterRespawnTickAsync).</summary>
    public void Respawn(Monster monster, MapRegistry maps)
    {
        var map = maps.GetMap(monster.Map);
        var (x, y) = MonsterSpawnTable.ResolvePosition(monster.SpawnEntry, map);

        monster.X = (byte)x;
        monster.Y = (byte)y;
        monster.StartX = (byte)x;
        monster.StartY = (byte)y;
        monster.TX = (byte)x;
        monster.TY = (byte)y;
        monster.Life = monster.MaxLife;
        monster.Live = true;
        monster.DiedAt = null;
        monster.DamageByAttacker.Clear();
        map?.SetStandAttr(x, y);
    }

    /// <summary>
    /// Puerto de la porción "spawn dinámico de un solo monstruo" de CDevilSquare::SetMonster
    /// (DevilSquare.cpp:977-1017) -- a diferencia de <see cref="SpawnAll"/> (todo el mapa, al
    /// arrancar, desde MonsterSpawnTable), esto crea UN monstruo en runtime a partir de una entrada
    /// de posición ya elegida (normalmente del pool Type==4, ver MonsterSpawnTable) y una clase dada
    /// (no necesariamente la de <paramref name="entry"/> -- el pool es solo posiciones, la clase la
    /// decide el llamador según EventStageSpawn.dat). Devuelve null si no hay índices libres o si la
    /// clase no existe en <paramref name="infoTable"/>.
    /// </summary>
    public Monster? SpawnOne(int monsterClass, MonsterSpawnEntry entry, MonsterInfoTable infoTable, MapRegistry maps)
    {
        var info = infoTable.Get(monsterClass);

        if (info == null)
        {
            return null;
        }

        var map = maps.GetMap(entry.Map);
        var (x, y) = MonsterSpawnTable.ResolvePosition(entry, map);

        int index = AllocateIndex();

        if (index < 0)
        {
            Log.Add(LogColor.Red, "[MonsterRegistry] Sin índices libres -- se alcanzó el límite de {0} monstruos", MaxIndex);
            return null;
        }

        float scaledMaxLifeOne = ScaleRate(info.MaxLife);

        var monster = new Monster
        {
            Index = index,
            MonsterClass = monsterClass,
            SpawnEntry = entry,
            Name = info.Name,
            Level = info.Level,
            MaxLife = scaledMaxLifeOne,
            Life = scaledMaxLifeOne,
            Map = (byte)entry.Map,
            X = (byte)x,
            Y = (byte)y,
            StartX = (byte)x,
            StartY = (byte)y,
            TX = (byte)x,
            TY = (byte)y,
            Dir = (byte)Math.Clamp(entry.Dir, 0, 7),
            DamageMin = ScaleRate(info.DamageMin, WorldPacketBuilder.ServerInfo.MonsterPhysiDamageRate),
            DamageMax = ScaleRate(info.DamageMax, WorldPacketBuilder.ServerInfo.MonsterPhysiDamageRate),
            Defense = ScaleRate(info.Defense, WorldPacketBuilder.ServerInfo.MonsterDefenseRate),
            AttackSuccessRate = ScaleRate(info.AttackRate, WorldPacketBuilder.ServerInfo.MonsterAttackSuccessRateRate),
            DefenseSuccessRate = ScaleRate(info.DefenseRate, WorldPacketBuilder.ServerInfo.MonsterDefenseSuccessRateRate),
            AttackRange = info.AttackRange,
            AttackType = info.AttackType,
            MonsterSkill = info.MonsterSkill,
            ViewRange = info.ViewRange,
            ItemRate = info.ItemRate,
            MoneyRate = info.MoneyRate,
            MaxRegenMillis = info.RegenTime * 1000,
        };

        map?.SetStandAttr(x, y);
        _monsters[index] = monster;
        return monster;
    }

    /// <summary>Spawnea un NPC de tienda (ShopManager.txt) como un <see cref="Monster"/> más --
    /// reusa el mismo mecanismo de índices/viewport que un monstruo de combate real (ver comentario
    /// de <see cref="Monster.ShopNumber"/>), pero con vida "infinita" (no hay forma de reducirla, ya
    /// que OnAttackAsync/OnSkillAttackAsync rechazan atacar cualquier objeto con ShopNumber seteado)
    /// y sin depender de MonsterInfoTable (el nombre/clase de un NPC no vive en MonsterList.txt en
    /// este puerto, ver ShopManagerTable). Puerto simplificado de la porción "spawn de NPCs" de
    /// CShopManager::ReloadShop (ShopManager.cpp:105-150).</summary>
    public Monster? SpawnNpc(ShopInfo shop, MapRegistry? maps = null)
    {
        int index = AllocateIndex();

        if (index < 0)
        {
            Log.Add(LogColor.Red, "[MonsterRegistry] Sin índices libres -- no se pudo spawnear el NPC de tienda {0}", shop.Name);
            return null;
        }

        var spawnEntry = new MonsterSpawnEntry
        {
            Type = 0, MonsterClass = shop.NpcClass, Map = shop.Map, Dis = 0, X = shop.X, Y = shop.Y, Dir = shop.Dir,
        };

        var npc = new Monster
        {
            Index = index,
            MonsterClass = shop.NpcClass,
            SpawnEntry = spawnEntry,
            Name = shop.Name,
            Level = 0,
            MaxLife = 1,
            Life = 1,
            Map = (byte)shop.Map,
            X = (byte)shop.X,
            Y = (byte)shop.Y,
            StartX = (byte)shop.X,
            StartY = (byte)shop.Y,
            TX = (byte)shop.X,
            TY = (byte)shop.Y,
            Dir = (byte)Math.Clamp(shop.Dir, 0, 7),
            ShopNumber = shop.NpcClass,
        };

        maps?.GetMap(shop.Map)?.SetStandAttr(shop.X, shop.Y);
        _monsters[index] = npc;
        return npc;
    }

    /// <summary>Puerto de CDevilSquare::ClearMonster/DelMonster (DevilSquare.cpp) -- saca un monstruo
    /// del registro por completo (a diferencia de la muerte normal, que lo deja Live=false esperando
    /// respawn). El próximo tick de ViewportTicker lo va a sacar del VisibleMonsters de quien lo
    /// tuviera a la vista de forma natural (deja de estar en <see cref="All"/>), sin necesidad de
    /// mandar un 0x14 explícito acá.</summary>
    public bool Remove(int index) => _monsters.TryRemove(index, out _);

    private int AllocateIndex()
    {
        lock (_allocLock)
        {
            for (int tries = 0; tries < MaxIndex; tries++)
            {
                int candidate = _nextIndex;
                _nextIndex = _nextIndex + 1 >= MaxIndex ? StartIndex : _nextIndex + 1;

                if (!_monsters.ContainsKey(candidate))
                {
                    return candidate;
                }
            }

            return -1;
        }
    }
}
