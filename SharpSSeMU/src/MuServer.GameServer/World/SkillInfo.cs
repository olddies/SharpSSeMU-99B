using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> Port of SKILL_INFO (SkillManager.h) -- a balance row of Data/Skill/SkillList.txt, plus the two
/// derived fields <c>CSkill::Set</c> (Skill.cpp) builds from the file's single "Damage" column: <see
/// cref="DamageMin"/> = Damage, <see cref="DamageMax"/> = Damage + Damage/2 (integer division) -- they are NOT
/// two separate columns in the file, it is a fixed 1.5x formula applied at runtime. </summary>
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
    public int Effect { get; init; } // index into EffectList.txt (buff/debuff) -- not ported, see the header of World/SkillInfo.cs
    public int RequireLevel { get; init; }
    public int RequireEnergy { get; init; }
    public int RequireLeadership { get; init; }
    public int RequireKillCount { get; init; }
    public int RequireGuildStatus { get; init; }

    /// <summary>DW,DK,FE,MG,DL in that order -- 0 = that class cannot use the skill, N>0 = minimum "ChangeUp"
    /// tier required (ChangeUp+1 >= N). This port does not track ChangeUp (always 0, see
    /// PlayerObject.ChangeUp), so in practice only the skills with RequireClass==1 for the player's class are
    /// reachable -- documented as a known limitation.</summary>
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

/// <summary> Port of CSkillManager::Load (SkillManager.cpp:80-114) -- reads Data/Skill/SkillList.txt (MemScript
/// format, WITHOUT sections -- unlike Item.txt/MonsterList.txt, it is a single flat list of rows up to "end",
/// the same pattern as MonsterInfoTable). </summary>
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

        Log.Add(LogColor.Blue, "[SkillInfoTable] {0} skills loaded from {1}", _byIndex.Count, path);
        return _byIndex.Count;
    }
}

/// <summary> Port of CSkillDamage::GetDamage (SkillDamage.cpp:87-100) -- optional table from Data/Skill/
/// SkillDamage.txt (2 columns: SkillIndex, DamageRate[0~10000]) that multiplies a skill's final damage as the
/// last step (damage = damage*Rate/100). The real file shipped with this build has NO data rows (only
/// header/comment), so today it is a no-op for any skill -- it is implemented anyway in case a different
/// deployment carries data. </summary>
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

        Log.Add(LogColor.Blue, "[SkillDamageTable] {0} entry(ies) loaded from {1}", _rateByIndex.Count, path);
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
