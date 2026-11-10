using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>Port of CCustomComboSkill (CustomComboSkill.cpp): Data/Skill/ComboSkill.txt. For each class, the skills that take part in a
/// combo: stage 0 starts it, stage 1 finishes it. The shipped file gives the Dark Knight's five basic skills (Falling Slash, Lunge,
/// Uppercut, Cyclone, Slash) as the start and Twisting Slash, Rageful Blow and Death Stab as the finish, three seconds each.</summary>
public sealed class ComboSkillTable
{
    private readonly Dictionary<(int Class, int Skill), (int Stage, int Delay)> _skills = new();

    public int Count => _skills.Count;

    public void Add(int cls, int skill, int stage, int delay)
    {
        if (cls is >= 0 and < 5)
        {
            _skills.TryAdd((cls, skill), (stage, delay));
        }
    }

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ComboSkillTable] {0}", script.GetLastError());
            return 0;
        }

        while (script.GetToken() != TokenResult.End)
        {
            if (script.GetString() == "end")
            {
                break;
            }

            int cls = script.GetNumber();
            int skill = script.GetAsNumber();
            int stage = script.GetAsNumber();
            int delay = script.GetAsNumber();
            Add(cls, skill, stage, delay);
        }

        Log.Add(LogColor.Blue, "[ComboSkillTable] {0} combo skill(s) loaded from {1}", Count, path);
        return Count;
    }

    /// <summary>The stage of a skill in the class's combo (0 starts, 1 finishes) and the time in milliseconds the next one has to come
    /// within; null when the skill takes no part.</summary>
    public (int Stage, int Delay)? Get(int cls, int skill) => _skills.TryGetValue((cls, skill), out var info) ? info : null;
}

/// <summary>What a player has done towards a combo (CComboSkill): the skill that started it, the one that followed, and until when the next
/// has to come.</summary>
public sealed class ComboState
{
    private long _time;
    private int _first = -1;
    private int _second = -1;
    private int _index = -1;

    /// <summary>When the last combo landed, in milliseconds (ComboTime): what the anti-cheat check measures against.</summary>
    public long LastComboAt { get; set; }

    /// <summary>Port of CComboSkill::Init: forgets everything.</summary>
    public void Init()
    {
        _time = 0;
        _first = -1;
        _second = -1;
        _index = -1;
    }

    /// <summary>Port of CComboSkill::CheckCombo (ComboSkill.cpp:23): true when this skill is the third blow of a combo -- a starting
    /// skill, then a finishing one within the delay, then another finishing one that is not the same. Any other skill, a late one or
    /// a repeated one breaks the chain.</summary>
    public bool CheckCombo(ComboSkillTable table, int cls, int skill, long nowMs)
    {
        var info = table.Get(cls, skill);

        if (info == null)
        {
            Init();
            return false;
        }

        var (stage, delay) = info.Value;

        if (stage == 0)
        {
            _time = nowMs + delay;
            _first = skill;
            _index = 0;
            return false;
        }

        if (stage == 1)
        {
            if (_time < nowMs || _first == -1)
            {
                Init();
                return false;
            }

            if (_index == 0)
            {
                _time = nowMs + delay;
                _second = skill;
                _index = 1;
                return false;
            }

            if (_index == 1 && _second != skill)
            {
                Init();
                return true;
            }
        }

        Init();
        return false;
    }
}

/// <summary>The combo's settings: the damage it adds for each class (Character.dat) and the check against clients that fire it faster than
/// a hand can (Common.dat).</summary>
public sealed class ComboRules
{
    public static ComboRules Current { get; set; } = new();

    /// <summary>[class]: the blow is doubled, then (strength + agility + energy) / A * B is added, then the whole is scaled by C percent.</summary>
    public int[] DamageConstA { get; init; } = { 2, 2, 2, 2, 2 };
    public int[] DamageConstB { get; init; } = { 1, 1, 1, 1, 1 };
    public int[] DamageConstC { get; init; } = { 100, 100, 100, 100, 100 };

    /// <summary>CheckAutoComboHack: 0 off; otherwise a combo within <see cref="HackTolerance"/> milliseconds of the last is refused and
    /// punished: <see cref="HackAction"/> 1 kills the player, 2 closes his client.</summary>
    public int HackCheck { get; init; }
    public int HackTolerance { get; init; }
    public int HackAction { get; init; }

    /// <summary>Whether a player can combo at all (UseAttackSkill, SkillManager.cpp:786): the Magic Gladiator and the Dark Lord always
    /// can; the others only after finishing their class's quest (the Dark Knight's is quest 3, the rest's quest 2).</summary>
    public static bool CanCombo(PlayerObject player)
    {
        if (player.Class is 3 or 4)
        {
            return true;
        }

        return player.Quest != null && Config.QuestTable.CheckQuestListState(player.Quest, player.Class == 1 ? 3 : 2, QuestFinish);
    }

    private const int QuestFinish = 2;

    /// <summary>The damage a combo adds (Attack.cpp:358-363): the blow doubled, plus a part of the player's stats, scaled.</summary>
    public int Apply(PlayerObject player, int damage)
    {
        int cls = Math.Clamp((int)player.Class, 0, 4);
        damage += damage;

        long stats = (long)(player.Strength + player.AddStrength) + (player.Dexterity + player.AddDexterity) + (player.Energy + player.AddEnergy);
        int divisor = DamageConstA[cls];

        damage += divisor == 0 ? 0 : (int)(stats / divisor) * DamageConstB[cls];
        return damage * DamageConstC[cls] / 100;
    }
}
