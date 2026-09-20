using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary> Port with FULL coverage of <c>GameServerInfo - Skill.dat</c> (part of <c>CServerInfo</c>,
/// ServerInfo.h/.cpp of the correct tree -- see the doc-comment of <see cref="ServerInfoConfig"/> for the full
/// explanation of why that is the correct tree). Unlike the existing "curated" classes (<see
/// cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, which only load the fields with a real
/// consumer already ported), this class loads ALL 42 fields of this file (61 .ini keys, some are arrays per
/// AccountLevel 0-3 or per class DW/DK/FE/MG/DL) -- including those that no ported system sits behind yet. The
/// goal is that no value from this file stays hardcoded in C#: if the real .dat is present in <c>Data/</c>,
/// whatever it contains is read as is (same key names that <c>GetPrivateProfileInt</c> uses in the original);
/// if the file is missing, each field falls back to the default 0, which is EXACTLY the default
/// <c>GetPrivateProfileInt(section,"Key",0,path)</c> uses in the real C++ (confirmed by reading the whole
/// ServerInfo.cpp -- all the defaults there are literally 0, the "real" factory values live entirely in the
/// shipped .dat, not in the source code). Property names = name of the real <c>CServerInfo</c> field without
/// the <c>m_</c> prefix (so that it can be diffed 1:1 against ServerInfo.h). Arrays
/// <c>[MAX_ACCOUNT_LEVEL=4]</c> stay as <c>int[4]</c> indexed 0-3 (AL0..AL3); per-class arrays stay as
/// <c>int[5]</c> indexed DW=0,DK=1,FE=2,MG=3,DL=4 (same order as <see cref="World.PlayerObject"/>); the few
/// <c>[class][class]</c> or <c>[level][AL]</c> matrices stay as <c>int[,]</c>. A field being loaded here does
/// NOT imply that the system that would use it in the original is already ported -- see the catalogue of
/// missing systems documented in <see cref="ServerInfoConfig"/> and in the README. This class is the data
/// layer; wiring it to a new game system is separate work, system by system. </summary>
public sealed class GameServerInfoSkill
{
    private const string Section = "GameServerInfo";

    public int ManaShieldConstA { get; private set; }
    public int ManaShieldConstB { get; private set; }
    public int ManaShieldConstC { get; private set; }
    public int[] ManaShieldRate { get; } = new int[5];
    public int ManaShieldTimeConstA { get; private set; }
    public int ManaShieldTimeConstB { get; private set; }
    public int ManaShieldMaxRate { get; private set; }
    public int HealConstA { get; private set; }
    public int HealConstB { get; private set; }
    public int GreaterDefenseConstA { get; private set; }
    public int GreaterDefenseConstB { get; private set; }
    public int[] GreaterDefenseRate { get; } = new int[5];
    public int GreaterDefenseTimeConstA { get; private set; }
    public int GreaterDamageConstA { get; private set; }
    public int GreaterDamageConstB { get; private set; }
    public int[] GreaterDamageRate { get; } = new int[5];
    public int GreaterDamageTimeConstA { get; private set; }
    public int SummonMonster1 { get; private set; }
    public int SummonMonster2 { get; private set; }
    public int SummonMonster3 { get; private set; }
    public int SummonMonster4 { get; private set; }
    public int SummonMonster5 { get; private set; }
    public int SummonMonster6 { get; private set; }
    public int SummonMonster7 { get; private set; }
    public int GreaterLifeConstA { get; private set; }
    public int GreaterLifeConstB { get; private set; }
    public int GreaterLifeConstC { get; private set; }
    public int[] GreaterLifeRate { get; } = new int[5];
    public int GreaterLifeTimeConstA { get; private set; }
    public int GreaterLifeTimeConstB { get; private set; }
    public int GreaterLifeMaxRate { get; private set; }
    public int FireSlashConstA { get; private set; }
    public int FireSlashConstB { get; private set; }
    public int FireSlashTimeConstA { get; private set; }
    public int FireSlashMaxRate { get; private set; }
    public int GreaterCriticalDamageConstA { get; private set; }
    public int GreaterCriticalDamageConstB { get; private set; }
    public int GreaterCriticalDamageTimeConstA { get; private set; }
    public int GreaterCriticalDamageTimeConstB { get; private set; }
    public int[] InfinityArrowSwitch { get; } = new int[4];
    public int MagicDamageImmunityTimeConstA { get; private set; }
    public int PhysiDamageImmunityTimeConstA { get; private set; }

    public static GameServerInfoSkill Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoSkill();

        cfg.ManaShieldConstA = ini.GetInt(Section, "ManaShieldConstA", 0);

        cfg.ManaShieldConstB = ini.GetInt(Section, "ManaShieldConstB", 0);

        cfg.ManaShieldConstC = ini.GetInt(Section, "ManaShieldConstC", 0);

        cfg.ManaShieldRate[0] = ini.GetInt(Section, "ManaShieldRateDW", 0);
        cfg.ManaShieldRate[1] = ini.GetInt(Section, "ManaShieldRateDK", 0);
        cfg.ManaShieldRate[2] = ini.GetInt(Section, "ManaShieldRateFE", 0);
        cfg.ManaShieldRate[3] = ini.GetInt(Section, "ManaShieldRateMG", 0);
        cfg.ManaShieldRate[4] = ini.GetInt(Section, "ManaShieldRateDL", 0);

        cfg.ManaShieldTimeConstA = ini.GetInt(Section, "ManaShieldTimeConstA", 0);

        cfg.ManaShieldTimeConstB = ini.GetInt(Section, "ManaShieldTimeConstB", 0);

        cfg.ManaShieldMaxRate = ini.GetInt(Section, "ManaShieldMaxRate", 0);

        cfg.HealConstA = ini.GetInt(Section, "HealConstA", 0);

        cfg.HealConstB = ini.GetInt(Section, "HealConstB", 0);

        cfg.GreaterDefenseConstA = ini.GetInt(Section, "GreaterDefenseConstA", 0);

        cfg.GreaterDefenseConstB = ini.GetInt(Section, "GreaterDefenseConstB", 0);

        cfg.GreaterDefenseRate[0] = ini.GetInt(Section, "GreaterDefenseRateDW", 0);
        cfg.GreaterDefenseRate[1] = ini.GetInt(Section, "GreaterDefenseRateDK", 0);
        cfg.GreaterDefenseRate[2] = ini.GetInt(Section, "GreaterDefenseRateFE", 0);
        cfg.GreaterDefenseRate[3] = ini.GetInt(Section, "GreaterDefenseRateMG", 0);
        cfg.GreaterDefenseRate[4] = ini.GetInt(Section, "GreaterDefenseRateDL", 0);

        cfg.GreaterDefenseTimeConstA = ini.GetInt(Section, "GreaterDefenseTimeConstA", 0);

        cfg.GreaterDamageConstA = ini.GetInt(Section, "GreaterDamageConstA", 0);

        cfg.GreaterDamageConstB = ini.GetInt(Section, "GreaterDamageConstB", 0);

        cfg.GreaterDamageRate[0] = ini.GetInt(Section, "GreaterDamageRateDW", 0);
        cfg.GreaterDamageRate[1] = ini.GetInt(Section, "GreaterDamageRateDK", 0);
        cfg.GreaterDamageRate[2] = ini.GetInt(Section, "GreaterDamageRateFE", 0);
        cfg.GreaterDamageRate[3] = ini.GetInt(Section, "GreaterDamageRateMG", 0);
        cfg.GreaterDamageRate[4] = ini.GetInt(Section, "GreaterDamageRateDL", 0);

        cfg.GreaterDamageTimeConstA = ini.GetInt(Section, "GreaterDamageTimeConstA", 0);

        cfg.SummonMonster1 = ini.GetInt(Section, "SummonMonster1", 0);

        cfg.SummonMonster2 = ini.GetInt(Section, "SummonMonster2", 0);

        cfg.SummonMonster3 = ini.GetInt(Section, "SummonMonster3", 0);

        cfg.SummonMonster4 = ini.GetInt(Section, "SummonMonster4", 0);

        cfg.SummonMonster5 = ini.GetInt(Section, "SummonMonster5", 0);

        cfg.SummonMonster6 = ini.GetInt(Section, "SummonMonster6", 0);

        cfg.SummonMonster7 = ini.GetInt(Section, "SummonMonster7", 0);

        cfg.GreaterLifeConstA = ini.GetInt(Section, "GreaterLifeConstA", 0);

        cfg.GreaterLifeConstB = ini.GetInt(Section, "GreaterLifeConstB", 0);

        cfg.GreaterLifeConstC = ini.GetInt(Section, "GreaterLifeConstC", 0);

        cfg.GreaterLifeRate[0] = ini.GetInt(Section, "GreaterLifeRateDW", 0);
        cfg.GreaterLifeRate[1] = ini.GetInt(Section, "GreaterLifeRateDK", 0);
        cfg.GreaterLifeRate[2] = ini.GetInt(Section, "GreaterLifeRateFE", 0);
        cfg.GreaterLifeRate[3] = ini.GetInt(Section, "GreaterLifeRateMG", 0);
        cfg.GreaterLifeRate[4] = ini.GetInt(Section, "GreaterLifeRateDL", 0);

        cfg.GreaterLifeTimeConstA = ini.GetInt(Section, "GreaterLifeTimeConstA", 0);

        cfg.GreaterLifeTimeConstB = ini.GetInt(Section, "GreaterLifeTimeConstB", 0);

        cfg.GreaterLifeMaxRate = ini.GetInt(Section, "GreaterLifeMaxRate", 0);

        cfg.FireSlashConstA = ini.GetInt(Section, "FireSlashConstA", 0);

        cfg.FireSlashConstB = ini.GetInt(Section, "FireSlashConstB", 0);

        cfg.FireSlashTimeConstA = ini.GetInt(Section, "FireSlashTimeConstA", 0);

        cfg.FireSlashMaxRate = ini.GetInt(Section, "FireSlashMaxRate", 0);

        cfg.GreaterCriticalDamageConstA = ini.GetInt(Section, "GreaterCriticalDamageConstA", 0);

        cfg.GreaterCriticalDamageConstB = ini.GetInt(Section, "GreaterCriticalDamageConstB", 0);

        cfg.GreaterCriticalDamageTimeConstA = ini.GetInt(Section, "GreaterCriticalDamageTimeConstA", 0);

        cfg.GreaterCriticalDamageTimeConstB = ini.GetInt(Section, "GreaterCriticalDamageTimeConstB", 0);

        cfg.InfinityArrowSwitch[0] = ini.GetInt(Section, "InfinityArrowSwitch_AL0", 0);
        cfg.InfinityArrowSwitch[1] = ini.GetInt(Section, "InfinityArrowSwitch_AL1", 0);
        cfg.InfinityArrowSwitch[2] = ini.GetInt(Section, "InfinityArrowSwitch_AL2", 0);
        cfg.InfinityArrowSwitch[3] = ini.GetInt(Section, "InfinityArrowSwitch_AL3", 0);

        cfg.MagicDamageImmunityTimeConstA = ini.GetInt(Section, "MagicDamageImmunityTimeConstA", 0);

        cfg.PhysiDamageImmunityTimeConstA = ini.GetInt(Section, "PhysiDamageImmunityTimeConstA", 0);

        return cfg;
    }
}
