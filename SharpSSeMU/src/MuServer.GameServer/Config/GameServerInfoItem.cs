using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary> Port with FULL coverage of <c>GameServerInfo - Item.dat</c> (part of <c>CServerInfo</c>,
/// ServerInfo.h/.cpp of the correct tree -- see the doc-comment of <see cref="ServerInfoConfig"/> for the full
/// explanation of why that is the correct tree). Unlike the existing "curated" classes (<see
/// cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, which only load the fields with a real
/// consumer already ported), this class loads ALL 26 fields of this file (54 .ini keys, some are arrays per
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
public sealed class GameServerInfoItem
{
    private const string Section = "GameServerInfo";

    public int TransformationRing1 { get; private set; }
    public int TransformationRing2 { get; private set; }
    public int TransformationRing3 { get; private set; }
    public int TransformationRing4 { get; private set; }
    public int TransformationRing5 { get; private set; }
    public int TransformationRing6 { get; private set; }
    public int SatanIncDamageConstA { get; private set; }
    public int DinorantIncDamageConstA { get; private set; }
    public int AngelDecDamageConstA { get; private set; }
    public int DinorantDecDamageConstA { get; private set; }
    public int DinorantDecDamageConstB { get; private set; }
    public int DarkHorseDecDamageConstA { get; private set; }
    public int DarkHorseDecDamageConstB { get; private set; }
    public int[] ApplePotionRate { get; } = new int[5];
    public int[] SmallLifePotionRate { get; } = new int[5];
    public int[] MidleLifePotionRate { get; } = new int[5];
    public int[] LargeLifePotionRate { get; } = new int[5];
    public int[] SmallManaPotionRate { get; } = new int[5];
    public int[] MidleManaPotionRate { get; } = new int[5];
    public int[] LargeManaPotionRate { get; } = new int[5];
    public int AleIncSpeed { get; private set; }
    public int AleIncSpeedTime { get; private set; }
    public int OliveOfLoveIncSpeed { get; private set; }
    public int OliveOfLoveIncSpeedTime { get; private set; }
    public int RemedyOfLoveIncDamage { get; private set; }
    public int RemedyOfLoveIncDamageTime { get; private set; }

    public static GameServerInfoItem Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoItem();

        cfg.TransformationRing1 = ini.GetInt(Section, "TransformationRing1", 0);

        cfg.TransformationRing2 = ini.GetInt(Section, "TransformationRing2", 0);

        cfg.TransformationRing3 = ini.GetInt(Section, "TransformationRing3", 0);

        cfg.TransformationRing4 = ini.GetInt(Section, "TransformationRing4", 0);

        cfg.TransformationRing5 = ini.GetInt(Section, "TransformationRing5", 0);

        cfg.TransformationRing6 = ini.GetInt(Section, "TransformationRing6", 0);

        cfg.SatanIncDamageConstA = ini.GetInt(Section, "SatanIncDamageConstA", 0);

        cfg.DinorantIncDamageConstA = ini.GetInt(Section, "DinorantIncDamageConstA", 0);

        cfg.AngelDecDamageConstA = ini.GetInt(Section, "AngelDecDamageConstA", 0);

        cfg.DinorantDecDamageConstA = ini.GetInt(Section, "DinorantDecDamageConstA", 0);

        cfg.DinorantDecDamageConstB = ini.GetInt(Section, "DinorantDecDamageConstB", 0);

        cfg.DarkHorseDecDamageConstA = ini.GetInt(Section, "DarkHorseDecDamageConstA", 0);

        cfg.DarkHorseDecDamageConstB = ini.GetInt(Section, "DarkHorseDecDamageConstB", 0);

        cfg.ApplePotionRate[0] = ini.GetInt(Section, "DWApplePotionRate", 0);
        cfg.ApplePotionRate[1] = ini.GetInt(Section, "DKApplePotionRate", 0);
        cfg.ApplePotionRate[2] = ini.GetInt(Section, "FEApplePotionRate", 0);
        cfg.ApplePotionRate[3] = ini.GetInt(Section, "MGApplePotionRate", 0);
        cfg.ApplePotionRate[4] = ini.GetInt(Section, "DLApplePotionRate", 0);

        cfg.SmallLifePotionRate[0] = ini.GetInt(Section, "DWSmallLifePotionRate", 0);
        cfg.SmallLifePotionRate[1] = ini.GetInt(Section, "DKSmallLifePotionRate", 0);
        cfg.SmallLifePotionRate[2] = ini.GetInt(Section, "FESmallLifePotionRate", 0);
        cfg.SmallLifePotionRate[3] = ini.GetInt(Section, "MGSmallLifePotionRate", 0);
        cfg.SmallLifePotionRate[4] = ini.GetInt(Section, "DLSmallLifePotionRate", 0);

        cfg.MidleLifePotionRate[0] = ini.GetInt(Section, "DWMidleLifePotionRate", 0);
        cfg.MidleLifePotionRate[1] = ini.GetInt(Section, "DKMidleLifePotionRate", 0);
        cfg.MidleLifePotionRate[2] = ini.GetInt(Section, "FEMidleLifePotionRate", 0);
        cfg.MidleLifePotionRate[3] = ini.GetInt(Section, "MGMidleLifePotionRate", 0);
        cfg.MidleLifePotionRate[4] = ini.GetInt(Section, "DLMidleLifePotionRate", 0);

        cfg.LargeLifePotionRate[0] = ini.GetInt(Section, "DWLargeLifePotionRate", 0);
        cfg.LargeLifePotionRate[1] = ini.GetInt(Section, "DKLargeLifePotionRate", 0);
        cfg.LargeLifePotionRate[2] = ini.GetInt(Section, "FELargeLifePotionRate", 0);
        cfg.LargeLifePotionRate[3] = ini.GetInt(Section, "MGLargeLifePotionRate", 0);
        cfg.LargeLifePotionRate[4] = ini.GetInt(Section, "DLLargeLifePotionRate", 0);

        cfg.SmallManaPotionRate[0] = ini.GetInt(Section, "DWSmallManaPotionRate", 0);
        cfg.SmallManaPotionRate[1] = ini.GetInt(Section, "DKSmallManaPotionRate", 0);
        cfg.SmallManaPotionRate[2] = ini.GetInt(Section, "FESmallManaPotionRate", 0);
        cfg.SmallManaPotionRate[3] = ini.GetInt(Section, "MGSmallManaPotionRate", 0);
        cfg.SmallManaPotionRate[4] = ini.GetInt(Section, "DLSmallManaPotionRate", 0);

        cfg.MidleManaPotionRate[0] = ini.GetInt(Section, "DWMidleManaPotionRate", 0);
        cfg.MidleManaPotionRate[1] = ini.GetInt(Section, "DKMidleManaPotionRate", 0);
        cfg.MidleManaPotionRate[2] = ini.GetInt(Section, "FEMidleManaPotionRate", 0);
        cfg.MidleManaPotionRate[3] = ini.GetInt(Section, "MGMidleManaPotionRate", 0);
        cfg.MidleManaPotionRate[4] = ini.GetInt(Section, "DLMidleManaPotionRate", 0);

        cfg.LargeManaPotionRate[0] = ini.GetInt(Section, "DWLargeManaPotionRate", 0);
        cfg.LargeManaPotionRate[1] = ini.GetInt(Section, "DKLargeManaPotionRate", 0);
        cfg.LargeManaPotionRate[2] = ini.GetInt(Section, "FELargeManaPotionRate", 0);
        cfg.LargeManaPotionRate[3] = ini.GetInt(Section, "MGLargeManaPotionRate", 0);
        cfg.LargeManaPotionRate[4] = ini.GetInt(Section, "DLLargeManaPotionRate", 0);

        cfg.AleIncSpeed = ini.GetInt(Section, "AleIncSpeed", 0);

        cfg.AleIncSpeedTime = ini.GetInt(Section, "AleIncSpeedTime", 0);

        cfg.OliveOfLoveIncSpeed = ini.GetInt(Section, "OliveOfLoveIncSpeed", 0);

        cfg.OliveOfLoveIncSpeedTime = ini.GetInt(Section, "OliveOfLoveIncSpeedTime", 0);

        cfg.RemedyOfLoveIncDamage = ini.GetInt(Section, "RemedyOfLoveIncDamage", 0);

        cfg.RemedyOfLoveIncDamageTime = ini.GetInt(Section, "RemedyOfLoveIncDamageTime", 0);

        return cfg;
    }
}
