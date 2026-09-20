using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto de SKILL_INFO (SkillManager.h) -- una fila de balance de Data/Skill/SkillList.txt, más los
/// dos campos derivados que arma <c>CSkill::Set</c> (Skill.cpp) a partir de la única columna "Damage"
/// del archivo: <see cref="DamageMin"/> = Damage, <see cref="DamageMax"/> = Damage + Damage/2 (división
/// entera) -- NO son dos columnas separadas en el archivo, es una fórmula fija 1.5x aplicada en
/// tiempo de ejecución.
/// </summary>
public sealed class SkillInfo
{
    public required int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Damage { get; init; }
    public int Mana { get; init; }
    public int BP { get; init; }
    public int Range { get; init; }
    public int Radio { get; init; }
    public int Delay { get; init; } // milisegundos entre casteos del mismo skill (CheckSkillDelay)
    public int Type { get; init; } // tipo elemental -- no usado en esta pasada (sin resistencias de skill)
    public int Effect { get; init; } // índice a EffectList.txt (buff/debuff) -- no portado, ver World/SkillInfo.cs cabecera
    public int RequireLevel { get; init; }
    public int RequireEnergy { get; init; }
    public int RequireLeadership { get; init; }
    public int RequireKillCount { get; init; }
    public int RequireGuildStatus { get; init; }

    /// <summary>DW,DK,FE,MG,DL en ese orden -- 0 = esa clase no puede usar el skill, N>0 = tier de
    /// "ChangeUp" mínimo requerido (ChangeUp+1 >= N). Este puerto no trackea ChangeUp (siempre 0,
    /// ver PlayerObject.ChangeUp), así que en la práctica solo los skills con RequireClass==1 para la
    /// clase del jugador son alcanzables -- documentado como limitación conocida.</summary>
    public int[] RequireClass { get; init; } = new int[5];

    public int DamageMin => Damage;
    public int DamageMax => Damage + (Damage / 2);

    public bool CanUse(int classIndex, int changeUp)
    {
        if (classIndex is < 0 or > 4)
        {
            return false;
        }

        int required = RequireClass[classIndex];
        return required != 0 && (changeUp + 1) >= required;
    }
}

/// <summary>
/// Puerto de CSkillManager::Load (SkillManager.cpp:80-114) -- lee Data/Skill/SkillList.txt (formato
/// MemScript, SIN secciones -- a diferencia de Item.txt/MonsterList.txt, es una sola lista plana de
/// filas hasta "end", mismo patrón que MonsterInfoTable).
/// </summary>
public sealed class SkillInfoTable
{
    private readonly Dictionary<int, SkillInfo> _byIndex = new();

    public int Count => _byIndex.Count;

    public SkillInfo? Get(int index) => _byIndex.GetValueOrDefault(index);

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[SkillInfoTable] {0}", script.GetLastError());
            return 0;
        }

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

            int index = script.GetNumber();
            string name = script.GetAsString();
            int damage = script.GetAsNumber();
            int mana = script.GetAsNumber();
            int bp = script.GetAsNumber();
            int range = script.GetAsNumber();
            int radio = script.GetAsNumber();
            int delay = script.GetAsNumber();
            int type = script.GetAsNumber();
            int effect = script.GetAsNumber();
            int reqLevel = script.GetAsNumber();
            int reqEnergy = script.GetAsNumber();
            int reqLeadership = script.GetAsNumber();
            int reqKillCount = script.GetAsNumber();
            int reqGuildStatus = script.GetAsNumber();

            var reqClass = new int[5];
            for (int n = 0; n < 5; n++)
            {
                reqClass[n] = script.GetAsNumber();
            }

            _byIndex[index] = new SkillInfo
            {
                Index = index, Name = name, Damage = damage, Mana = mana, BP = bp, Range = range,
                Radio = radio, Delay = delay, Type = type, Effect = effect, RequireLevel = reqLevel,
                RequireEnergy = reqEnergy, RequireLeadership = reqLeadership, RequireKillCount = reqKillCount,
                RequireGuildStatus = reqGuildStatus, RequireClass = reqClass,
            };
        }

        Log.Add(LogColor.Blue, "[SkillInfoTable] {0} skills cargados desde {1}", _byIndex.Count, path);
        return _byIndex.Count;
    }
}

/// <summary>
/// Puerto de CSkillDamage::GetDamage (SkillDamage.cpp:87-100) -- tabla opcional de Data/Skill/
/// SkillDamage.txt (2 columnas: SkillIndex, DamageRate[0~10000]) que multiplica el daño final de un
/// skill como último paso (damage = damage*Rate/100). El archivo real shippeado con este build NO
/// tiene filas de datos (solo encabezado/comentario), así que hoy es un no-op para cualquier skill --
/// se implementa igual por si un despliegue distinto trae datos.
/// </summary>
public sealed class SkillDamageTable
{
    private readonly Dictionary<int, int> _rateByIndex = new();

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[SkillDamageTable] {0}", script.GetLastError());
            return 0;
        }

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

            int index = script.GetNumber();
            int rate = script.GetAsNumber();
            _rateByIndex[index] = rate;
        }

        Log.Add(LogColor.Blue, "[SkillDamageTable] {0} entrada(s) cargadas desde {1}", _rateByIndex.Count, path);
        return _rateByIndex.Count;
    }

    public int Apply(int skillIndex, int damage)
    {
        if (!_rateByIndex.TryGetValue(skillIndex, out var rate))
        {
            return damage;
        }

        return (damage * rate) / 100;
    }
}
