using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary> Port with FULL coverage of <c>GameServerInfo - Event.dat</c> (part of <c>CServerInfo</c>,
/// ServerInfo.h/.cpp of the correct tree -- see the doc-comment of <see cref="ServerInfoConfig"/> for the full
/// explanation of why that is the correct tree). Unlike the existing "curated" classes (<see
/// cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, which only load the fields with a real
/// consumer already ported), this class loads ALL 16 fields of this file (25 .ini keys, some are arrays per
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
public sealed class GameServerInfoEvent
{
    private const string Section = "GameServerInfo";

    public int BloodCastleEvent { get; private set; }
    public int BloodCastleMaxUser { get; private set; }
    public int[] BloodCastleMaxEntryCount { get; } = new int[4];
    public int BloodCastleNPCWithoutEntrance { get; private set; }
    public int BonusManagerSwitch { get; private set; }
    public int ChaosCastleEvent { get; private set; }
    public int ChaosCastleMinUser { get; private set; }
    public int ChaosCastleBlowUserRate { get; private set; }
    public int ChaosCastleMoneyRate { get; private set; }
    public int[] ChaosCastleMaxEntryCount { get; } = new int[4];
    public int DevilSquareEvent { get; private set; }
    public int DevilSquareMaxUser { get; private set; }
    public int[] DevilSquareMaxEntryCount { get; } = new int[4];
    public int DevilSquareNPCWithoutEntrance { get; private set; }
    public int DropEventSwitch { get; private set; }
    public int InvasionManagerSwitch { get; private set; }

    public static GameServerInfoEvent Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoEvent();

        cfg.BloodCastleEvent = ini.GetInt(Section, "BloodCastleEvent", 0);

        cfg.BloodCastleMaxUser = ini.GetInt(Section, "BloodCastleMaxUser", 0);

        cfg.BloodCastleMaxEntryCount[0] = ini.GetInt(Section, "BloodCastleMaxEntryCount_AL0", 0);
        cfg.BloodCastleMaxEntryCount[1] = ini.GetInt(Section, "BloodCastleMaxEntryCount_AL1", 0);
        cfg.BloodCastleMaxEntryCount[2] = ini.GetInt(Section, "BloodCastleMaxEntryCount_AL2", 0);
        cfg.BloodCastleMaxEntryCount[3] = ini.GetInt(Section, "BloodCastleMaxEntryCount_AL3", 0);

        cfg.BloodCastleNPCWithoutEntrance = ini.GetInt(Section, "BloodCastleNPCWithoutEntrance", 0);

        cfg.BonusManagerSwitch = ini.GetInt(Section, "BonusManagerSwitch", 0);

        cfg.ChaosCastleEvent = ini.GetInt(Section, "ChaosCastleEvent", 0);

        cfg.ChaosCastleMinUser = ini.GetInt(Section, "ChaosCastleMinUser", 0);

        cfg.ChaosCastleBlowUserRate = ini.GetInt(Section, "ChaosCastleBlowUserRate", 0);

        cfg.ChaosCastleMoneyRate = ini.GetInt(Section, "ChaosCastleMoneyRate", 0);

        cfg.ChaosCastleMaxEntryCount[0] = ini.GetInt(Section, "ChaosCastleMaxEntryCount_AL0", 0);
        cfg.ChaosCastleMaxEntryCount[1] = ini.GetInt(Section, "ChaosCastleMaxEntryCount_AL1", 0);
        cfg.ChaosCastleMaxEntryCount[2] = ini.GetInt(Section, "ChaosCastleMaxEntryCount_AL2", 0);
        cfg.ChaosCastleMaxEntryCount[3] = ini.GetInt(Section, "ChaosCastleMaxEntryCount_AL3", 0);

        cfg.DevilSquareEvent = ini.GetInt(Section, "DevilSquareEvent", 0);

        cfg.DevilSquareMaxUser = ini.GetInt(Section, "DevilSquareMaxUser", 0);

        cfg.DevilSquareMaxEntryCount[0] = ini.GetInt(Section, "DevilSquareMaxEntryCount_AL0", 0);
        cfg.DevilSquareMaxEntryCount[1] = ini.GetInt(Section, "DevilSquareMaxEntryCount_AL1", 0);
        cfg.DevilSquareMaxEntryCount[2] = ini.GetInt(Section, "DevilSquareMaxEntryCount_AL2", 0);
        cfg.DevilSquareMaxEntryCount[3] = ini.GetInt(Section, "DevilSquareMaxEntryCount_AL3", 0);

        cfg.DevilSquareNPCWithoutEntrance = ini.GetInt(Section, "DevilSquareNPCWithoutEntrance", 0);

        cfg.DropEventSwitch = ini.GetInt(Section, "DropEventSwitch", 0);

        cfg.InvasionManagerSwitch = ini.GetInt(Section, "InvasionManagerSwitch", 0);

        return cfg;
    }
}
