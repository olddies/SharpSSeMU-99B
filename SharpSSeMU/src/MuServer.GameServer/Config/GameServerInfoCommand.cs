using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary> Port with FULL coverage of <c>GameServerInfo - Command.dat</c> (part of <c>CServerInfo</c>,
/// ServerInfo.h/.cpp of the correct tree -- see the doc-comment of <see cref="ServerInfoConfig"/> for the full
/// explanation of why that is the correct tree). Unlike the existing "curated" classes (<see
/// cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, which only load the fields with a real
/// consumer already ported), this class loads ALL 51 fields of this file (164 .ini keys, some are arrays per
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
public sealed class GameServerInfoCommand
{
    private const string Section = "GameServerInfo";

    public int CommandPostType { get; private set; }
    public int CommandPKClearType { get; private set; }
    public int[] CommandPKClearMoney { get; } = new int[4];
    public int[] CommandAddPointAutoEnable { get; } = new int[4];
    public int[] CommandChangeLimit { get; } = new int[4];
    public int[] CommandWareNumber { get; } = new int[4];
    public int CommandResetType { get; private set; }
    public int CommandResetKeepStrength { get; private set; }
    public int CommandResetKeepDexterity { get; private set; }
    public int CommandResetKeepVitality { get; private set; }
    public int CommandResetKeepEnergy { get; private set; }
    public int CommandResetKeepLeadership { get; private set; }
    public int[] CommandResetAutoEnable { get; } = new int[4];
    public int[] CommandResetCheckItem { get; } = new int[4];
    public int[] CommandResetMove { get; } = new int[4];
    public int[] CommandResetClearQuest { get; } = new int[4];
    public int[] CommandResetClearSkill { get; } = new int[4];
    public int[] CommandResetClearParty { get; } = new int[4];
    public int[] CommandResetLevel { get; } = new int[4];
    public int[] CommandResetMoney { get; } = new int[4];
    public int[] CommandResetCount { get; } = new int[4];
    public int[] CommandResetLimit { get; } = new int[4];
    public int[] CommandResetLimitDay { get; } = new int[4];
    public int[] CommandResetLimitWek { get; } = new int[4];
    public int[] CommandResetLimitMon { get; } = new int[4];
    public int[] CommandResetStartLevel { get; } = new int[4];
    public int[] CommandResetPoint { get; } = new int[4];
    public int[] CommandResetPointRate { get; } = new int[5];
    public int CommandMasterResetType { get; private set; }
    public int CommandMasterResetKeepStrength { get; private set; }
    public int CommandMasterResetKeepDexterity { get; private set; }
    public int CommandMasterResetKeepVitality { get; private set; }
    public int CommandMasterResetKeepEnergy { get; private set; }
    public int CommandMasterResetKeepLeadership { get; private set; }
    public int[] CommandMasterResetCheckItem { get; } = new int[4];
    public int[] CommandMasterResetMove { get; } = new int[4];
    public int[] CommandMasterResetClearQuest { get; } = new int[4];
    public int[] CommandMasterResetClearSkill { get; } = new int[4];
    public int[] CommandMasterResetClearParty { get; } = new int[4];
    public int[] CommandMasterResetLevel { get; } = new int[4];
    public int[] CommandMasterResetReset { get; } = new int[4];
    public int[] CommandMasterResetMoney { get; } = new int[4];
    public int[] CommandMasterResetCount { get; } = new int[4];
    public int[] CommandMasterResetLimit { get; } = new int[4];
    public int[] CommandMasterResetLimitDay { get; } = new int[4];
    public int[] CommandMasterResetLimitWek { get; } = new int[4];
    public int[] CommandMasterResetLimitMon { get; } = new int[4];
    public int[] CommandMasterResetStartLevel { get; } = new int[4];
    public int[] CommandMasterResetStartReset { get; } = new int[4];
    public int[] CommandMasterResetPoint { get; } = new int[4];
    public int[] CommandMasterResetPointRate { get; } = new int[5];

    public static GameServerInfoCommand Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoCommand();

        cfg.CommandPostType = ini.GetInt(Section, "CommandPostType", 0);

        cfg.CommandPKClearType = ini.GetInt(Section, "CommandPKClearType", 0);

        cfg.CommandPKClearMoney[0] = ini.GetInt(Section, "CommandPKClearMoney_AL0", 0);
        cfg.CommandPKClearMoney[1] = ini.GetInt(Section, "CommandPKClearMoney_AL1", 0);
        cfg.CommandPKClearMoney[2] = ini.GetInt(Section, "CommandPKClearMoney_AL2", 0);
        cfg.CommandPKClearMoney[3] = ini.GetInt(Section, "CommandPKClearMoney_AL3", 0);

        cfg.CommandAddPointAutoEnable[0] = ini.GetInt(Section, "CommandAddPointAutoEnable_AL0", 0);
        cfg.CommandAddPointAutoEnable[1] = ini.GetInt(Section, "CommandAddPointAutoEnable_AL1", 0);
        cfg.CommandAddPointAutoEnable[2] = ini.GetInt(Section, "CommandAddPointAutoEnable_AL2", 0);
        cfg.CommandAddPointAutoEnable[3] = ini.GetInt(Section, "CommandAddPointAutoEnable_AL3", 0);

        cfg.CommandChangeLimit[0] = ini.GetInt(Section, "CommandChangeLimit_AL0", 0);
        cfg.CommandChangeLimit[1] = ini.GetInt(Section, "CommandChangeLimit_AL1", 0);
        cfg.CommandChangeLimit[2] = ini.GetInt(Section, "CommandChangeLimit_AL2", 0);
        cfg.CommandChangeLimit[3] = ini.GetInt(Section, "CommandChangeLimit_AL3", 0);

        cfg.CommandWareNumber[0] = ini.GetInt(Section, "CommandWareNumber_AL0", 0);
        cfg.CommandWareNumber[1] = ini.GetInt(Section, "CommandWareNumber_AL1", 0);
        cfg.CommandWareNumber[2] = ini.GetInt(Section, "CommandWareNumber_AL2", 0);
        cfg.CommandWareNumber[3] = ini.GetInt(Section, "CommandWareNumber_AL3", 0);

        cfg.CommandResetType = ini.GetInt(Section, "CommandResetType", 0);

        cfg.CommandResetKeepStrength = ini.GetInt(Section, "CommandResetKeepStrength", 0);

        cfg.CommandResetKeepDexterity = ini.GetInt(Section, "CommandResetKeepDexterity", 0);

        cfg.CommandResetKeepVitality = ini.GetInt(Section, "CommandResetKeepVitality", 0);

        cfg.CommandResetKeepEnergy = ini.GetInt(Section, "CommandResetKeepEnergy", 0);

        cfg.CommandResetKeepLeadership = ini.GetInt(Section, "CommandResetKeepLeadership", 0);

        cfg.CommandResetAutoEnable[0] = ini.GetInt(Section, "CommandResetAutoEnable_AL0", 0);
        cfg.CommandResetAutoEnable[1] = ini.GetInt(Section, "CommandResetAutoEnable_AL1", 0);
        cfg.CommandResetAutoEnable[2] = ini.GetInt(Section, "CommandResetAutoEnable_AL2", 0);
        cfg.CommandResetAutoEnable[3] = ini.GetInt(Section, "CommandResetAutoEnable_AL3", 0);

        cfg.CommandResetCheckItem[0] = ini.GetInt(Section, "CommandResetCheckItem_AL0", 0);
        cfg.CommandResetCheckItem[1] = ini.GetInt(Section, "CommandResetCheckItem_AL1", 0);
        cfg.CommandResetCheckItem[2] = ini.GetInt(Section, "CommandResetCheckItem_AL2", 0);
        cfg.CommandResetCheckItem[3] = ini.GetInt(Section, "CommandResetCheckItem_AL3", 0);

        cfg.CommandResetMove[0] = ini.GetInt(Section, "CommandResetMove_AL0", 0);
        cfg.CommandResetMove[1] = ini.GetInt(Section, "CommandResetMove_AL1", 0);
        cfg.CommandResetMove[2] = ini.GetInt(Section, "CommandResetMove_AL2", 0);
        cfg.CommandResetMove[3] = ini.GetInt(Section, "CommandResetMove_AL3", 0);

        cfg.CommandResetClearQuest[0] = ini.GetInt(Section, "CommandResetClearQuest_AL0", 0);
        cfg.CommandResetClearQuest[1] = ini.GetInt(Section, "CommandResetClearQuest_AL1", 0);
        cfg.CommandResetClearQuest[2] = ini.GetInt(Section, "CommandResetClearQuest_AL2", 0);
        cfg.CommandResetClearQuest[3] = ini.GetInt(Section, "CommandResetClearQuest_AL3", 0);

        cfg.CommandResetClearSkill[0] = ini.GetInt(Section, "CommandResetClearSkill_AL0", 0);
        cfg.CommandResetClearSkill[1] = ini.GetInt(Section, "CommandResetClearSkill_AL1", 0);
        cfg.CommandResetClearSkill[2] = ini.GetInt(Section, "CommandResetClearSkill_AL2", 0);
        cfg.CommandResetClearSkill[3] = ini.GetInt(Section, "CommandResetClearSkill_AL3", 0);

        cfg.CommandResetClearParty[0] = ini.GetInt(Section, "CommandResetClearParty_AL0", 0);
        cfg.CommandResetClearParty[1] = ini.GetInt(Section, "CommandResetClearParty_AL1", 0);
        cfg.CommandResetClearParty[2] = ini.GetInt(Section, "CommandResetClearParty_AL2", 0);
        cfg.CommandResetClearParty[3] = ini.GetInt(Section, "CommandResetClearParty_AL3", 0);

        cfg.CommandResetLevel[0] = ini.GetInt(Section, "CommandResetLevel_AL0", 0);
        cfg.CommandResetLevel[1] = ini.GetInt(Section, "CommandResetLevel_AL1", 0);
        cfg.CommandResetLevel[2] = ini.GetInt(Section, "CommandResetLevel_AL2", 0);
        cfg.CommandResetLevel[3] = ini.GetInt(Section, "CommandResetLevel_AL3", 0);

        cfg.CommandResetMoney[0] = ini.GetInt(Section, "CommandResetMoney_AL0", 0);
        cfg.CommandResetMoney[1] = ini.GetInt(Section, "CommandResetMoney_AL1", 0);
        cfg.CommandResetMoney[2] = ini.GetInt(Section, "CommandResetMoney_AL2", 0);
        cfg.CommandResetMoney[3] = ini.GetInt(Section, "CommandResetMoney_AL3", 0);

        cfg.CommandResetCount[0] = ini.GetInt(Section, "CommandResetCount_AL0", 0);
        cfg.CommandResetCount[1] = ini.GetInt(Section, "CommandResetCount_AL1", 0);
        cfg.CommandResetCount[2] = ini.GetInt(Section, "CommandResetCount_AL2", 0);
        cfg.CommandResetCount[3] = ini.GetInt(Section, "CommandResetCount_AL3", 0);

        cfg.CommandResetLimit[0] = ini.GetInt(Section, "CommandResetLimit_AL0", 0);
        cfg.CommandResetLimit[1] = ini.GetInt(Section, "CommandResetLimit_AL1", 0);
        cfg.CommandResetLimit[2] = ini.GetInt(Section, "CommandResetLimit_AL2", 0);
        cfg.CommandResetLimit[3] = ini.GetInt(Section, "CommandResetLimit_AL3", 0);

        cfg.CommandResetLimitDay[0] = ini.GetInt(Section, "CommandResetLimitDay_AL0", 0);
        cfg.CommandResetLimitDay[1] = ini.GetInt(Section, "CommandResetLimitDay_AL1", 0);
        cfg.CommandResetLimitDay[2] = ini.GetInt(Section, "CommandResetLimitDay_AL2", 0);
        cfg.CommandResetLimitDay[3] = ini.GetInt(Section, "CommandResetLimitDay_AL3", 0);

        cfg.CommandResetLimitWek[0] = ini.GetInt(Section, "CommandResetLimitWek_AL0", 0);
        cfg.CommandResetLimitWek[1] = ini.GetInt(Section, "CommandResetLimitWek_AL1", 0);
        cfg.CommandResetLimitWek[2] = ini.GetInt(Section, "CommandResetLimitWek_AL2", 0);
        cfg.CommandResetLimitWek[3] = ini.GetInt(Section, "CommandResetLimitWek_AL3", 0);

        cfg.CommandResetLimitMon[0] = ini.GetInt(Section, "CommandResetLimitMon_AL0", 0);
        cfg.CommandResetLimitMon[1] = ini.GetInt(Section, "CommandResetLimitMon_AL1", 0);
        cfg.CommandResetLimitMon[2] = ini.GetInt(Section, "CommandResetLimitMon_AL2", 0);
        cfg.CommandResetLimitMon[3] = ini.GetInt(Section, "CommandResetLimitMon_AL3", 0);

        cfg.CommandResetStartLevel[0] = ini.GetInt(Section, "CommandResetStartLevel_AL0", 0);
        cfg.CommandResetStartLevel[1] = ini.GetInt(Section, "CommandResetStartLevel_AL1", 0);
        cfg.CommandResetStartLevel[2] = ini.GetInt(Section, "CommandResetStartLevel_AL2", 0);
        cfg.CommandResetStartLevel[3] = ini.GetInt(Section, "CommandResetStartLevel_AL3", 0);

        cfg.CommandResetPoint[0] = ini.GetInt(Section, "CommandResetPoint_AL0", 0);
        cfg.CommandResetPoint[1] = ini.GetInt(Section, "CommandResetPoint_AL1", 0);
        cfg.CommandResetPoint[2] = ini.GetInt(Section, "CommandResetPoint_AL2", 0);
        cfg.CommandResetPoint[3] = ini.GetInt(Section, "CommandResetPoint_AL3", 0);

        cfg.CommandResetPointRate[0] = ini.GetInt(Section, "CommandResetPointRateDW", 0);
        cfg.CommandResetPointRate[1] = ini.GetInt(Section, "CommandResetPointRateDK", 0);
        cfg.CommandResetPointRate[2] = ini.GetInt(Section, "CommandResetPointRateFE", 0);
        cfg.CommandResetPointRate[3] = ini.GetInt(Section, "CommandResetPointRateMG", 0);
        cfg.CommandResetPointRate[4] = ini.GetInt(Section, "CommandResetPointRateDL", 0);

        cfg.CommandMasterResetType = ini.GetInt(Section, "CommandMasterResetType", 0);

        cfg.CommandMasterResetKeepStrength = ini.GetInt(Section, "CommandMasterResetKeepStrength", 0);

        cfg.CommandMasterResetKeepDexterity = ini.GetInt(Section, "CommandMasterResetKeepDexterity", 0);

        cfg.CommandMasterResetKeepVitality = ini.GetInt(Section, "CommandMasterResetKeepVitality", 0);

        cfg.CommandMasterResetKeepEnergy = ini.GetInt(Section, "CommandMasterResetKeepEnergy", 0);

        cfg.CommandMasterResetKeepLeadership = ini.GetInt(Section, "CommandMasterResetKeepLeadership", 0);

        cfg.CommandMasterResetCheckItem[0] = ini.GetInt(Section, "CommandMasterResetCheckItem_AL0", 0);
        cfg.CommandMasterResetCheckItem[1] = ini.GetInt(Section, "CommandMasterResetCheckItem_AL1", 0);
        cfg.CommandMasterResetCheckItem[2] = ini.GetInt(Section, "CommandMasterResetCheckItem_AL2", 0);
        cfg.CommandMasterResetCheckItem[3] = ini.GetInt(Section, "CommandMasterResetCheckItem_AL3", 0);

        cfg.CommandMasterResetMove[0] = ini.GetInt(Section, "CommandMasterResetMove_AL0", 0);
        cfg.CommandMasterResetMove[1] = ini.GetInt(Section, "CommandMasterResetMove_AL1", 0);
        cfg.CommandMasterResetMove[2] = ini.GetInt(Section, "CommandMasterResetMove_AL2", 0);
        cfg.CommandMasterResetMove[3] = ini.GetInt(Section, "CommandMasterResetMove_AL3", 0);

        cfg.CommandMasterResetClearQuest[0] = ini.GetInt(Section, "CommandMasterResetClearQuest_AL0", 0);
        cfg.CommandMasterResetClearQuest[1] = ini.GetInt(Section, "CommandMasterResetClearQuest_AL1", 0);
        cfg.CommandMasterResetClearQuest[2] = ini.GetInt(Section, "CommandMasterResetClearQuest_AL2", 0);
        cfg.CommandMasterResetClearQuest[3] = ini.GetInt(Section, "CommandMasterResetClearQuest_AL3", 0);

        cfg.CommandMasterResetClearSkill[0] = ini.GetInt(Section, "CommandMasterResetClearSkill_AL0", 0);
        cfg.CommandMasterResetClearSkill[1] = ini.GetInt(Section, "CommandMasterResetClearSkill_AL1", 0);
        cfg.CommandMasterResetClearSkill[2] = ini.GetInt(Section, "CommandMasterResetClearSkill_AL2", 0);
        cfg.CommandMasterResetClearSkill[3] = ini.GetInt(Section, "CommandMasterResetClearSkill_AL3", 0);

        cfg.CommandMasterResetClearParty[0] = ini.GetInt(Section, "CommandMasterResetClearParty_AL0", 0);
        cfg.CommandMasterResetClearParty[1] = ini.GetInt(Section, "CommandMasterResetClearParty_AL0", 0);
        cfg.CommandMasterResetClearParty[2] = ini.GetInt(Section, "CommandMasterResetClearParty_AL0", 0);
        cfg.CommandMasterResetClearParty[3] = ini.GetInt(Section, "CommandMasterResetClearParty_AL0", 0);

        cfg.CommandMasterResetLevel[0] = ini.GetInt(Section, "CommandMasterResetLevel_AL0", 0);
        cfg.CommandMasterResetLevel[1] = ini.GetInt(Section, "CommandMasterResetLevel_AL1", 0);
        cfg.CommandMasterResetLevel[2] = ini.GetInt(Section, "CommandMasterResetLevel_AL2", 0);
        cfg.CommandMasterResetLevel[3] = ini.GetInt(Section, "CommandMasterResetLevel_AL3", 0);

        cfg.CommandMasterResetReset[0] = ini.GetInt(Section, "CommandMasterResetReset_AL0", 0);
        cfg.CommandMasterResetReset[1] = ini.GetInt(Section, "CommandMasterResetReset_AL1", 0);
        cfg.CommandMasterResetReset[2] = ini.GetInt(Section, "CommandMasterResetReset_AL2", 0);
        cfg.CommandMasterResetReset[3] = ini.GetInt(Section, "CommandMasterResetReset_AL3", 0);

        cfg.CommandMasterResetMoney[0] = ini.GetInt(Section, "CommandMasterResetMoney_AL0", 0);
        cfg.CommandMasterResetMoney[1] = ini.GetInt(Section, "CommandMasterResetMoney_AL1", 0);
        cfg.CommandMasterResetMoney[2] = ini.GetInt(Section, "CommandMasterResetMoney_AL2", 0);
        cfg.CommandMasterResetMoney[3] = ini.GetInt(Section, "CommandMasterResetMoney_AL3", 0);

        cfg.CommandMasterResetCount[0] = ini.GetInt(Section, "CommandMasterResetCount_AL0", 0);
        cfg.CommandMasterResetCount[1] = ini.GetInt(Section, "CommandMasterResetCount_AL1", 0);
        cfg.CommandMasterResetCount[2] = ini.GetInt(Section, "CommandMasterResetCount_AL2", 0);
        cfg.CommandMasterResetCount[3] = ini.GetInt(Section, "CommandMasterResetCount_AL3", 0);

        cfg.CommandMasterResetLimit[0] = ini.GetInt(Section, "CommandMasterResetLimit_AL0", 0);
        cfg.CommandMasterResetLimit[1] = ini.GetInt(Section, "CommandMasterResetLimit_AL1", 0);
        cfg.CommandMasterResetLimit[2] = ini.GetInt(Section, "CommandMasterResetLimit_AL2", 0);
        cfg.CommandMasterResetLimit[3] = ini.GetInt(Section, "CommandMasterResetLimit_AL3", 0);

        cfg.CommandMasterResetLimitDay[0] = ini.GetInt(Section, "CommandMasterResetLimitDay_AL0", 0);
        cfg.CommandMasterResetLimitDay[1] = ini.GetInt(Section, "CommandMasterResetLimitDay_AL1", 0);
        cfg.CommandMasterResetLimitDay[2] = ini.GetInt(Section, "CommandMasterResetLimitDay_AL2", 0);
        cfg.CommandMasterResetLimitDay[3] = ini.GetInt(Section, "CommandMasterResetLimitDay_AL3", 0);

        cfg.CommandMasterResetLimitWek[0] = ini.GetInt(Section, "CommandMasterResetLimitWek_AL0", 0);
        cfg.CommandMasterResetLimitWek[1] = ini.GetInt(Section, "CommandMasterResetLimitWek_AL1", 0);
        cfg.CommandMasterResetLimitWek[2] = ini.GetInt(Section, "CommandMasterResetLimitWek_AL2", 0);
        cfg.CommandMasterResetLimitWek[3] = ini.GetInt(Section, "CommandMasterResetLimitWek_AL3", 0);

        cfg.CommandMasterResetLimitMon[0] = ini.GetInt(Section, "CommandMasterResetLimitMon_AL0", 0);
        cfg.CommandMasterResetLimitMon[1] = ini.GetInt(Section, "CommandMasterResetLimitMon_AL1", 0);
        cfg.CommandMasterResetLimitMon[2] = ini.GetInt(Section, "CommandMasterResetLimitMon_AL2", 0);
        cfg.CommandMasterResetLimitMon[3] = ini.GetInt(Section, "CommandMasterResetLimitMon_AL3", 0);

        cfg.CommandMasterResetStartLevel[0] = ini.GetInt(Section, "CommandMasterResetStartLevel_AL0", 0);
        cfg.CommandMasterResetStartLevel[1] = ini.GetInt(Section, "CommandMasterResetStartLevel_AL1", 0);
        cfg.CommandMasterResetStartLevel[2] = ini.GetInt(Section, "CommandMasterResetStartLevel_AL2", 0);
        cfg.CommandMasterResetStartLevel[3] = ini.GetInt(Section, "CommandMasterResetStartLevel_AL3", 0);

        cfg.CommandMasterResetStartReset[0] = ini.GetInt(Section, "CommandMasterResetStartReset_AL0", 0);
        cfg.CommandMasterResetStartReset[1] = ini.GetInt(Section, "CommandMasterResetStartReset_AL1", 0);
        cfg.CommandMasterResetStartReset[2] = ini.GetInt(Section, "CommandMasterResetStartReset_AL2", 0);
        cfg.CommandMasterResetStartReset[3] = ini.GetInt(Section, "CommandMasterResetStartReset_AL3", 0);

        cfg.CommandMasterResetPoint[0] = ini.GetInt(Section, "CommandMasterResetPoint_AL0", 0);
        cfg.CommandMasterResetPoint[1] = ini.GetInt(Section, "CommandMasterResetPoint_AL1", 0);
        cfg.CommandMasterResetPoint[2] = ini.GetInt(Section, "CommandMasterResetPoint_AL2", 0);
        cfg.CommandMasterResetPoint[3] = ini.GetInt(Section, "CommandMasterResetPoint_AL3", 0);

        cfg.CommandMasterResetPointRate[0] = ini.GetInt(Section, "CommandMasterResetPointRateDW", 0);
        cfg.CommandMasterResetPointRate[1] = ini.GetInt(Section, "CommandMasterResetPointRateDK", 0);
        cfg.CommandMasterResetPointRate[2] = ini.GetInt(Section, "CommandMasterResetPointRateFE", 0);
        cfg.CommandMasterResetPointRate[3] = ini.GetInt(Section, "CommandMasterResetPointRateMG", 0);
        cfg.CommandMasterResetPointRate[4] = ini.GetInt(Section, "CommandMasterResetPointRateDL", 0);

        return cfg;
    }
}
