using System.Collections.Concurrent;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

/// <summary> Simplified port of the "monsters" slice of gObj[10000] (indices 0-7999,
/// OBJECT_START_MONSTER..MAX_OBJECT_MONSTER, see User.h:8-13) -- it replaces gObjAddMonster/
/// gObjSetPosMonster/gObjSetMonster (Monster.cpp:169-365, all invoked from CMonsterManager::SetMonsterData at
/// start-up, GameMain.cpp:35) with a dictionary of live monsters + a linear index allocator (just as simple as
/// the original, which also uses a free-list/linear-scan allocator, see Monster.cpp:645-687). </summary>
public sealed class MonsterRegistry
{
    private const int StartIndex = 0;
    private const int MaxIndex = 8000; // MAX_OBJECT_MONSTER

    private readonly ConcurrentDictionary<int, Monster> _monsters = new();
    private readonly object _allocLock = new();
    private int _nextIndex = StartIndex;

    public IEnumerable<Monster> All => _monsters.Values;

    public bool TryGet(int index, out Monster monster) => _monsters.TryGetValue(index, out monster!);

    /// <summary>Port of CMonsterManager::SetMonsterData (MonsterManager.cpp:281-325) -- instantiates a Monster
    /// for each spawn row whose MONSTER_INFO exists and is of "real monster" type (Type==0; NPCs are not
    /// instantiated yet, see the comment of MonsterInfo.cs). Returns the number of monsters actually
    /// created.</summary>
    public int SpawnAll(IReadOnlyList<MonsterSpawnEntry> entries, MonsterInfoTable infoTable, MapRegistry maps)
    {
        int spawned = 0;

        foreach (var entry in entries)
        {
            if (entry.Type == 4)
            {
                continue; // pool de posiciones de evento (Type==4, ver MonsterSpawnTable) -- NO se
                          // instantiated at start-up, only DevilSquareManager consumes it at runtime (Phase 6),
                          // same as the original (CMonsterSetBase does not load them in SetMonsterData).
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
                Log.Add(LogColor.Red, "[MonsterRegistry] No free indices -- the limit of {0} monsters was reached", MaxIndex);
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

        Log.Add(LogColor.Blue, "[MonsterRegistry] {0} monster(s) instantiated", spawned);
        return spawned;
    }

    /// <summary>Port of the global multipliers of <c>CMonsterManager::SetInfo</c> (MonsterManager.cpp:166-183)
    /// -- applied once when spawning, over the raw MonsterList.txt columns (see <see
    /// cref="MuServer.GameServer.Config.ServerInfoConfig"/>). With the pack's real values (all =100) this is a
    /// no-op; the scaling mechanism itself was not plugged in before porting <c>GameServerInfo -
    /// Common.dat</c>.</summary>
    private static int ScaleRate(int value, int ratePercent) => (int)((long)value * ratePercent / 100);

    private static float ScaleRate(float value) => value * WorldPacketBuilder.ServerInfo.MonsterMaxLifeRate / 100f;

    /// <summary>Port of gObjMonsterRegen (Monster.cpp:367-428) -- revives a monster at its original spawn point
    /// (re-resolving the position if it was a random box), called from the respawn tick (see
    /// ClientProtocolHandler.MonsterRespawnTickAsync).</summary>
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

    /// <summary> Port of the "dynamic spawn of a single monster" portion of CDevilSquare::SetMonster
    /// (DevilSquare.cpp:977-1017) -- unlike <see cref="SpawnAll"/> (the whole map, at start-up, from
    /// MonsterSpawnTable), this creates ONE monster at runtime from an already chosen position entry (normally
    /// from the Type==4 pool, see MonsterSpawnTable) and a given class (not necessarily that of <paramref
    /// name="entry"/> -- the pool is only positions, the class is decided by the caller according to
    /// EventStageSpawn.dat). Returns null if there are no free indices or if the class does not exist in
    /// <paramref name="infoTable"/>. </summary>
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
            Log.Add(LogColor.Red, "[MonsterRegistry] No free indices -- the limit of {0} monsters was reached", MaxIndex);
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

    /// <summary>Spawns a shop NPC (ShopManager.txt) as one more <see cref="Monster"/> -- it reuses the same
    /// index/viewport mechanism as a real combat monster (see the comment of <see cref="Monster.ShopNumber"/>),
    /// but with "infinite" life (there is no way to reduce it, since OnAttackAsync/OnSkillAttackAsync reject
    /// attacking any object with ShopNumber set) and without depending on MonsterInfoTable (an NPC's name/class
    /// does not live in MonsterList.txt in this port, see ShopManagerTable). Simplified port of the "NPC spawn"
    /// portion of CShopManager::ReloadShop (ShopManager.cpp:105-150).</summary>
    public Monster? SpawnNpc(ShopInfo shop, MapRegistry? maps = null)
    {
        int index = AllocateIndex();

        if (index < 0)
        {
            Log.Add(LogColor.Red, "[MonsterRegistry] No free indices -- could not spawn shop NPC {0}", shop.Name);
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

    /// <summary>Port of CDevilSquare::ClearMonster/DelMonster (DevilSquare.cpp) -- removes a monster from the
    /// registry entirely (unlike normal death, which leaves it Live=false waiting for respawn). The next
    /// ViewportTicker tick will remove it from the VisibleMonsters of whoever had it in view naturally (it
    /// stops being in <see cref="All"/>), with no need to send an explicit 0x14 here.</summary>
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
