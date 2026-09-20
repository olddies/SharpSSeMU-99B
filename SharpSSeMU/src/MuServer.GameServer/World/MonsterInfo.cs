using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> Port of MONSTER_INFO (MonsterManager.h:11-39) -- balance row of a monster type, as read from
/// MonsterList.txt. <see cref="Type"/> decides whether this row is a real monster (0) or an NPC (any other
/// value, e.g. vendors/quest-givers) -- for now the monster registry (Phase 4) only instantiates the Type==0
/// rows (see MonsterRegistry.SpawnAll), NPCs are left for a later phase (they have no combat AI in the original
/// either). </summary>
public sealed class MonsterInfo
{
    public int Index { get; init; }
    public int Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Level { get; init; }
    public int MaxLife { get; init; }
    public int MaxMana { get; init; }
    public int DamageMin { get; init; }
    public int DamageMax { get; init; }
    public int Defense { get; init; }
    public int MagicDefense { get; init; }
    public int AttackRate { get; init; } // AttackSuccessRate
    public int DefenseRate { get; init; } // DefenseSuccessRate
    public int MoveRange { get; init; }
    public int AttackType { get; init; }
    public int AttackRange { get; init; }
    public int ViewRange { get; init; }
    public int MoveSpeed { get; init; }
    public int AttackSpeed { get; init; }
    public int RegenTime { get; init; } // segundos (MaxRegenTime = RegenTime*1000, ver Monster.cpp:264)
    public int Attribute { get; init; }
    public int ItemRate { get; init; }
    public int MoneyRate { get; init; }
    public int MaxItemLevel { get; init; }
    public int MonsterSkill { get; init; }
    public int[] Resistance { get; init; } = new int[7];
}

/// <summary> Port of CMonsterManager::Load/SetInfo (MonsterManager.cpp:54-183) -- reads
/// Data/Monster/MonsterList.txt (MemScript format, columns confirmed against the real file, see the header
/// comment in the .txt itself). The global server multipliers (m_MonsterMaxLifeRate, etc. -- SetInfo:166-183)
/// are not ported yet (they live in the ~500 balance columns of CServerInfo that Phase 1 did not read); the
/// table's raw values are used as they are, equivalent to having all those rates at 100 (the default of an
/// untouched package). </summary>
public sealed class MonsterInfoTable
{
    private readonly Dictionary<int, MonsterInfo> _byIndex = new();

    public int Count => _byIndex.Count;

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MonsterInfoTable] {0}", script.GetLastError());
            return 0;
        }

        int loaded = 0;

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

            var index = script.GetNumber();
            var type = script.GetAsNumber();
            var name = script.GetAsString();
            var level = script.GetAsNumber();
            var maxLife = script.GetAsNumber();
            var maxMana = script.GetAsNumber();
            var damageMin = script.GetAsNumber();
            var damageMax = script.GetAsNumber();
            var defense = script.GetAsNumber();
            var magicDefense = script.GetAsNumber();
            var attackRate = script.GetAsNumber();
            var defenseRate = script.GetAsNumber();
            var moveRange = script.GetAsNumber();
            var attackType = script.GetAsNumber();
            var attackRange = script.GetAsNumber();
            var viewRange = script.GetAsNumber();
            var moveSpeed = script.GetAsNumber();
            var attackSpeed = script.GetAsNumber();
            var regenTime = script.GetAsNumber();
            var attribute = script.GetAsNumber();
            var itemRate = script.GetAsNumber();
            var moneyRate = script.GetAsNumber();
            var maxItemLevel = script.GetAsNumber();
            var monsterSkill = script.GetAsNumber();

            var resistance = new int[7];

            for (int n = 0; n < 7; n++)
            {
                resistance[n] = script.GetAsNumber();
            }

            _byIndex[index] = new MonsterInfo
            {
                Index = index, Type = type, Name = name, Level = level, MaxLife = maxLife, MaxMana = maxMana,
                DamageMin = damageMin, DamageMax = damageMax, Defense = defense, MagicDefense = magicDefense,
                AttackRate = attackRate, DefenseRate = defenseRate, MoveRange = moveRange, AttackType = attackType,
                AttackRange = attackRange, ViewRange = viewRange, MoveSpeed = moveSpeed, AttackSpeed = attackSpeed,
                RegenTime = regenTime, Attribute = attribute, ItemRate = itemRate, MoneyRate = moneyRate,
                MaxItemLevel = maxItemLevel, MonsterSkill = monsterSkill, Resistance = resistance,
            };

            loaded++;
        }

        Log.Add(LogColor.Blue, "[MonsterInfoTable] {0} monster types loaded from {1}", loaded, path);
        return loaded;
    }

    public MonsterInfo? Get(int index) => _byIndex.GetValueOrDefault(index);
}
