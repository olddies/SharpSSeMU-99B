using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto de MONSTER_INFO (MonsterManager.h:11-39) -- fila de balance de un tipo de monstruo, tal
/// como se lee de MonsterList.txt. <see cref="Type"/> decide si esta fila es un monstruo real (0)
/// o un NPC (cualquier otro valor, ej. vendedores/quest-givers) -- por ahora el registro de
/// monstruos (Fase 4) solo instancia las filas Type==0 (ver MonsterRegistry.SpawnAll), los NPCs
/// quedan para una fase posterior (no tienen IA de combate en el original tampoco).
/// </summary>
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

/// <summary>
/// Puerto de CMonsterManager::Load/SetInfo (MonsterManager.cpp:54-183) -- lee Data/Monster/MonsterList.txt
/// (formato MemScript, columnas confirmadas contra el archivo real, ver comentario de cabecera en el
/// propio .txt). Los multiplicadores globales de servidor (m_MonsterMaxLifeRate, etc. -- SetInfo:166-183)
/// no están portados todavía (viven en las ~500 columnas de balance de CServerInfo que Fase 1 no leyó);
/// se usan los valores crudos de la tabla tal cual, equivalente a tener todos esos rates en 100 (el
/// default de un paquete sin tocar).
/// </summary>
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
