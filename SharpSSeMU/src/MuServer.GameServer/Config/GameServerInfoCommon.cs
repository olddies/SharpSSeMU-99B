using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de cobertura COMPLETA de <c>GameServerInfo - Common.dat</c> (parte de <c>CServerInfo</c>,
/// ServerInfo.h/.cpp del árbol correcto -- ver doc-comment de <see cref="ServerInfoConfig"/> para la
/// explicación completa de por qué ese es el árbol correcto). A diferencia de las clases "curadas"
/// existentes (<see cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, que solo cargan
/// los campos con un consumidor real ya portado), esta clase carga TODOS los 132 campos de
/// este archivo (202 claves de .ini, algunos son arrays por AccountLevel 0-3 o por clase
/// DW/DK/FE/MG/DL) -- incluyendo los que todavía no tiene ningún sistema portado detrás. El objetivo
/// es que ningún valor de este archivo quede hardcodeado en C#: si el .dat real está presente en
/// <c>Data/</c>, todo lo que contenga se lee tal cual (mismos nombres de clave que
/// <c>GetPrivateProfileInt</c> usa en el original); si falta el archivo, cada campo cae al default 0,
/// que es EXACTAMENTE el default que usa <c>GetPrivateProfileInt(section,"Clave",0,path)</c> en el
/// C++ real (confirmado leyendo ServerInfo.cpp completo -- todos los defaults ahí son literalmente 0,
/// los valores "reales" de fábrica viven enteramente en el .dat shippeado, no en el código fuente).
///
/// Nombres de propiedad = nombre del campo real de <c>CServerInfo</c> sin el prefijo <c>m_</c> (para
/// poder diffear 1:1 contra ServerInfo.h). Arrays <c>[MAX_ACCOUNT_LEVEL=4]</c> quedan como
/// <c>int[4]</c> indexado 0-3 (AL0..AL3); arrays por clase quedan <c>int[5]</c> indexado
/// DW=0,DK=1,FE=2,MG=3,DL=4 (mismo orden que <see cref="World.PlayerObject"/>); las pocas matrices
/// <c>[clase][clase]</c> o <c>[nivel][AL]</c> quedan como <c>int[,]</c>.
///
/// Un campo estando cargado acá NO implica que el sistema que lo usaría en el original ya esté
/// portado -- ver el catálogo de sistemas faltantes documentado en <see cref="ServerInfoConfig"/> y en
/// el README. Esta clase es la capa de datos; conectarla a un sistema de juego nuevo es trabajo
/// aparte, sistema por sistema.
/// </summary>
public sealed class GameServerInfoCommon
{
    private const string Section = "GameServerInfo";

    public int ServerCode { get; private set; }
    public int ServerLock { get; private set; }
    public int ServerPort { get; private set; }
    public int ServerMaxUserNumber { get; private set; }
    public int ServerEncDecKey1 { get; private set; }
    public int ServerEncDecKey2 { get; private set; }
    public int DataServerPort { get; private set; }
    public int JoinServerPort { get; private set; }
    public int ConnectServerPort { get; private set; }
    public int WriteChatLog { get; private set; }
    public int WriteCommandLog { get; private set; }
    public int WriteTradeLog { get; private set; }
    public int WriteConnectLog { get; private set; }
    public int WriteHackLog { get; private set; }
    public int WriteChaosMixLog { get; private set; }
    public int WriteScriptLog { get; private set; }
    public int ExperienceMultiplierConstA { get; private set; }
    public int ExperienceMultiplierConstB { get; private set; }
    public int PetExperienceMultiplierConstA { get; private set; }
    public int MaxLevel { get; private set; }
    public int PetMaxLevel { get; private set; }
    public int MaxConnectionIdle { get; private set; }
    public int MaxConnectionPerIP { get; private set; }
    public int MaxConnectionPerHID { get; private set; }
    public int MaxPacketPerSecond { get; private set; }
    public int MaxTimeConnectionVerify { get; private set; }
    public int ChaosMixPlusItemAnnounce { get; private set; }
    public int MaxItemOption { get; private set; }
    public int PersonalCodeCheck { get; private set; }
    public int ConnectMemberCheck { get; private set; }
    public int TeleportAttackCheck { get; private set; }
    public int EffectOverwriteMode { get; private set; }
    public int MonsterMaxLifeRate { get; private set; }
    public int MonsterDefenseRate { get; private set; }
    public int MonsterDefenseSuccessRateRate { get; private set; }
    public int MonsterPhysiDamageRate { get; private set; }
    public int MonsterAttackSuccessRateRate { get; private set; }
    public int MonsterHealthBarSwitch { get; private set; }
    public int MonsterGetTopHitDamageUserMaxTime { get; private set; }
    public int NonPK { get; private set; }
    public int PKLimitFree { get; private set; }
    public int PKLimitShop { get; private set; }
    public int PKLimitMove { get; private set; }
    public int PKLimitMoveSummon { get; private set; }
    public int PKLimitEventEntry { get; private set; }
    public int PKDeathAnnounce { get; private set; }
    public int PKDownPlusTimePoint { get; private set; }
    public int PKDownPlusKillPoint { get; private set; }
    public int PKDownRequirePoint1 { get; private set; }
    public int PKDownRequirePoint2 { get; private set; }
    public int PKDownRequirePoint3 { get; private set; }
    public int PKDownRequirePoint4 { get; private set; }
    public int PKItemDropRatePvP1 { get; private set; }
    public int PKItemDropRatePvP2 { get; private set; }
    public int PKItemDropRatePvP3 { get; private set; }
    public int PKItemDropRatePvM1 { get; private set; }
    public int PKItemDropRatePvM2 { get; private set; }
    public int PKItemDropRatePvM3 { get; private set; }
    public int PKItemDropMaxLevel { get; private set; }
    public int PKItemDropPet { get; private set; }
    public int PKItemDropWing { get; private set; }
    public int PKItemDropExc { get; private set; }
    public int PKItemDropSet { get; private set; }
    public int TradeSwitch { get; private set; }
    public int PersonalShopSwitch { get; private set; }
    public int DuelSwitch { get; private set; }
    public int DuelAnnounceSwitch { get; private set; }
    public int DuelMaxScore { get; private set; }
    public int DuelMaxTime { get; private set; }
    public int GuildCreateSwitch { get; private set; }
    public int GuildDeleteSwitch { get; private set; }
    public int[] GuildCreateMinLevel { get; } = new int[4];
    public int[] GuildCreateMinReset { get; } = new int[4];
    public int PetExperienceRateDivisor { get; private set; }
    public int[] AddExperienceRate { get; } = new int[4];
    public int[] AddEventExperienceRate { get; } = new int[4];
    public int ItemDropTime { get; private set; }
    public int[] ItemDropRate { get; } = new int[4];
    public int MoneyDropTime { get; private set; }
    public int[] MoneyAmountDropRate { get; } = new int[4];
    public int WeaponDurabilityRate { get; private set; }
    public int ArmorDurabilityRate { get; private set; }
    public int WingDurabilityRate { get; private set; }
    public int GuardianDurabilityRate { get; private set; }
    public int PendantDurabilityRate { get; private set; }
    public int RingDurabilityRate { get; private set; }
    public int PetDurabilityRate { get; private set; }
    public int TradeItemBlock { get; private set; }
    public int TradeItemBlockExc { get; private set; }
    public int TradeItemBlockSet { get; private set; }
    public int TradeItemBlockSell { get; private set; }
    public int MaxLevelUp { get; private set; }
    public int MaxLevelUpEvent { get; private set; }
    public int[] MaxStatPoint { get; } = new int[4];
    public int[,] LevelUpPoint { get; } = new int[5, 4];
    public int PlusStatPoint { get; private set; }
    public int PlusStatMinLevel { get; private set; }
    public int CharacterCreateSwitch { get; private set; }
    public int[] MGCreateLevel { get; } = new int[4];
    public int[] DLCreateLevel { get; } = new int[4];
    public int CharacterDeleteSwitch { get; private set; }
    public int CharacterDeleteMaxLevel { get; private set; }
    public int PartyReconnectTime { get; private set; }
    public int PartyMoneyDistribute { get; private set; }
    public int PartyDisableKillBetweenMembers { get; private set; }
    public int[] PartyGeneralExperience { get; } = new int[5];
    public int[] PartySpecialExperience { get; } = new int[5];
    public int PartyMaxGapLevel { get; private set; }
    public int[] SoulSuccessRate { get; } = new int[4];
    public int[] LifeSuccessRate { get; } = new int[4];
    public int[] AddLuckSuccessRate1 { get; } = new int[4];
    public int[] AddLuckSuccessRate2 { get; } = new int[4];
    public int FruitAddPointMin { get; private set; }
    public int FruitAddPointMax { get; private set; }
    public int[] FruitAddPointSuccessRate { get; } = new int[4];
    public int QuestMonsterItemDropParty { get; private set; }
    public int CheckSpeedHack { get; private set; }
    public int CheckSpeedHackTolerance { get; private set; }
    public int CheckSpeedHackAction { get; private set; }
    public int CheckLatencyHack { get; private set; }
    public int CheckLatencyHackTolerance { get; private set; }
    public int CheckLatencyHackAction { get; private set; }
    public int CheckAutoPotionHack { get; private set; }
    public int CheckAutoPotionHackTolerance { get; private set; }
    public int CheckAutoPotionHackAction { get; private set; }
    public int CheckAutoComboHack { get; private set; }
    public int CheckAutoComboHackTolerance { get; private set; }
    public int CheckAutoComboHackAction { get; private set; }
    public int CheckMoveHack { get; private set; }
    public int CheckMoveHackMaxDelay { get; private set; }
    public int CheckMoveHackMaxCount { get; private set; }
    public int CheckMoveHackAction { get; private set; }

    public static GameServerInfoCommon Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoCommon();

        cfg.ServerCode = ini.GetInt(Section, "ServerCode", 0);

        cfg.ServerLock = ini.GetInt(Section, "ServerLock", 0);

        cfg.ServerPort = ini.GetInt(Section, "ServerPort", 0);

        cfg.ServerMaxUserNumber = ini.GetInt(Section, "ServerMaxUserNumber", 0);

        cfg.ServerEncDecKey1 = ini.GetInt(Section, "ServerEncDecKey1", 0);

        cfg.ServerEncDecKey2 = ini.GetInt(Section, "ServerEncDecKey2", 0);

        cfg.DataServerPort = ini.GetInt(Section, "DataServerPort", 0);

        cfg.JoinServerPort = ini.GetInt(Section, "JoinServerPort", 0);

        cfg.ConnectServerPort = ini.GetInt(Section, "ConnectServerPort", 0);

        cfg.WriteChatLog = ini.GetInt(Section, "WriteChatLog", 0);

        cfg.WriteCommandLog = ini.GetInt(Section, "WriteCommandLog", 0);

        cfg.WriteTradeLog = ini.GetInt(Section, "WriteTradeLog", 0);

        cfg.WriteConnectLog = ini.GetInt(Section, "WriteConnectLog", 0);

        cfg.WriteHackLog = ini.GetInt(Section, "WriteHackLog", 0);

        cfg.WriteChaosMixLog = ini.GetInt(Section, "WriteChaosMixLog", 0);

        cfg.WriteScriptLog = ini.GetInt(Section, "WriteScriptLog", 0);

        cfg.ExperienceMultiplierConstA = ini.GetInt(Section, "ExperienceMultiplierConstA", 0);

        cfg.ExperienceMultiplierConstB = ini.GetInt(Section, "ExperienceMultiplierConstB", 0);

        cfg.PetExperienceMultiplierConstA = ini.GetInt(Section, "PetExperienceMultiplierConstA", 0);

        cfg.MaxLevel = ini.GetInt(Section, "MaxLevel", 0);

        cfg.PetMaxLevel = ini.GetInt(Section, "MaxPetLevel", 0);

        cfg.MaxConnectionIdle = ini.GetInt(Section, "MaxConnectionIdle", 0);

        cfg.MaxConnectionPerIP = ini.GetInt(Section, "MaxConnectionPerIP", 0);

        cfg.MaxConnectionPerHID = ini.GetInt(Section, "MaxConnectionPerHID", 0);

        cfg.MaxPacketPerSecond = ini.GetInt(Section, "MaxPacketPerSecond", 0);

        cfg.MaxTimeConnectionVerify = ini.GetInt(Section, "MaxTimeConnectionVerify", 0);

        cfg.ChaosMixPlusItemAnnounce = ini.GetInt(Section, "ChaosMixPlusItemAnnounce", 0);

        cfg.MaxItemOption = ini.GetInt(Section, "MaxItemOption", 0);

        cfg.PersonalCodeCheck = ini.GetInt(Section, "PersonalCodeCheck", 0);

        cfg.ConnectMemberCheck = ini.GetInt(Section, "ConnectMemberCheck", 0);

        cfg.TeleportAttackCheck = ini.GetInt(Section, "TeleportAttackCheck", 0);

        cfg.EffectOverwriteMode = ini.GetInt(Section, "EffectOverwriteMode", 0);

        cfg.MonsterMaxLifeRate = ini.GetInt(Section, "MonsterMaxLifeRate", 0);

        cfg.MonsterDefenseRate = ini.GetInt(Section, "MonsterDefenseRate", 0);

        cfg.MonsterDefenseSuccessRateRate = ini.GetInt(Section, "MonsterDefenseSuccessRateRate", 0);

        cfg.MonsterPhysiDamageRate = ini.GetInt(Section, "MonsterPhysiDamageRate", 0);

        cfg.MonsterAttackSuccessRateRate = ini.GetInt(Section, "MonsterAttackSuccessRateRate", 0);

        cfg.MonsterHealthBarSwitch = ini.GetInt(Section, "MonsterHealthBarSwitch", 0);

        cfg.MonsterGetTopHitDamageUserMaxTime = ini.GetInt(Section, "MonsterGetTopHitDamageUserMaxTime", 0);

        cfg.NonPK = ini.GetInt(Section, "NonPK", 0);

        cfg.PKLimitFree = ini.GetInt(Section, "PKLimitFree", 0);

        cfg.PKLimitShop = ini.GetInt(Section, "PKLimitShop", 0);

        cfg.PKLimitMove = ini.GetInt(Section, "PKLimitMove", 0);

        cfg.PKLimitMoveSummon = ini.GetInt(Section, "PKLimitMoveSummon", 0);

        cfg.PKLimitEventEntry = ini.GetInt(Section, "PKLimitEventEntry", 0);

        cfg.PKDeathAnnounce = ini.GetInt(Section, "PKDeathAnnounce", 0);

        cfg.PKDownPlusTimePoint = ini.GetInt(Section, "PKDownPlusTimePoint", 0);

        cfg.PKDownPlusKillPoint = ini.GetInt(Section, "PKDownPlusKillPoint", 0);

        cfg.PKDownRequirePoint1 = ini.GetInt(Section, "PKDownRequirePoint1", 0);

        cfg.PKDownRequirePoint2 = ini.GetInt(Section, "PKDownRequirePoint2", 0);

        cfg.PKDownRequirePoint3 = ini.GetInt(Section, "PKDownRequirePoint3", 0);

        cfg.PKDownRequirePoint4 = ini.GetInt(Section, "PKDownRequirePoint4", 0);

        cfg.PKItemDropRatePvP1 = ini.GetInt(Section, "PKItemDropRatePvP1", 0);

        cfg.PKItemDropRatePvP2 = ini.GetInt(Section, "PKItemDropRatePvP2", 0);

        cfg.PKItemDropRatePvP3 = ini.GetInt(Section, "PKItemDropRatePvP3", 0);

        cfg.PKItemDropRatePvM1 = ini.GetInt(Section, "PKItemDropRatePvM1", 0);

        cfg.PKItemDropRatePvM2 = ini.GetInt(Section, "PKItemDropRatePvM2", 0);

        cfg.PKItemDropRatePvM3 = ini.GetInt(Section, "PKItemDropRatePvM3", 0);

        cfg.PKItemDropMaxLevel = ini.GetInt(Section, "PKItemDropMaxLevel", 0);

        cfg.PKItemDropPet = ini.GetInt(Section, "PKItemDropPet", 0);

        cfg.PKItemDropWing = ini.GetInt(Section, "PKItemDropWing", 0);

        cfg.PKItemDropExc = ini.GetInt(Section, "PKItemDropExc", 0);

        cfg.PKItemDropSet = ini.GetInt(Section, "PKItemDropSet", 0);

        cfg.TradeSwitch = ini.GetInt(Section, "TradeSwitch", 0);

        cfg.PersonalShopSwitch = ini.GetInt(Section, "PersonalShopSwitch", 0);

        cfg.DuelSwitch = ini.GetInt(Section, "DuelSwitch", 0);

        cfg.DuelAnnounceSwitch = ini.GetInt(Section, "DuelAnnounceSwitch", 0);

        cfg.DuelMaxScore = ini.GetInt(Section, "DuelMaxScore", 0);

        cfg.DuelMaxTime = ini.GetInt(Section, "DuelMaxTime", 0);

        cfg.GuildCreateSwitch = ini.GetInt(Section, "GuildCreateSwitch", 0);

        cfg.GuildDeleteSwitch = ini.GetInt(Section, "GuildDeleteSwitch", 0);

        cfg.GuildCreateMinLevel[0] = ini.GetInt(Section, "GuildCreateMinLevel_AL0", 0);
        cfg.GuildCreateMinLevel[1] = ini.GetInt(Section, "GuildCreateMinLevel_AL1", 0);
        cfg.GuildCreateMinLevel[2] = ini.GetInt(Section, "GuildCreateMinLevel_AL2", 0);
        cfg.GuildCreateMinLevel[3] = ini.GetInt(Section, "GuildCreateMinLevel_AL3", 0);

        cfg.GuildCreateMinReset[0] = ini.GetInt(Section, "GuildCreateMinReset_AL0", 0);
        cfg.GuildCreateMinReset[1] = ini.GetInt(Section, "GuildCreateMinReset_AL1", 0);
        cfg.GuildCreateMinReset[2] = ini.GetInt(Section, "GuildCreateMinReset_AL2", 0);
        cfg.GuildCreateMinReset[3] = ini.GetInt(Section, "GuildCreateMinReset_AL3", 0);

        cfg.PetExperienceRateDivisor = ini.GetInt(Section, "PetExperienceRateDivisor", 0);

        cfg.AddExperienceRate[0] = ini.GetInt(Section, "AddExperienceRate_AL0", 0);
        cfg.AddExperienceRate[1] = ini.GetInt(Section, "AddExperienceRate_AL1", 0);
        cfg.AddExperienceRate[2] = ini.GetInt(Section, "AddExperienceRate_AL2", 0);
        cfg.AddExperienceRate[3] = ini.GetInt(Section, "AddExperienceRate_AL3", 0);

        cfg.AddEventExperienceRate[0] = ini.GetInt(Section, "AddEventExperienceRate_AL0", 0);
        cfg.AddEventExperienceRate[1] = ini.GetInt(Section, "AddEventExperienceRate_AL1", 0);
        cfg.AddEventExperienceRate[2] = ini.GetInt(Section, "AddEventExperienceRate_AL2", 0);
        cfg.AddEventExperienceRate[3] = ini.GetInt(Section, "AddEventExperienceRate_AL3", 0);

        cfg.ItemDropTime = ini.GetInt(Section, "ItemDropTime", 0);

        cfg.ItemDropRate[0] = ini.GetInt(Section, "ItemDropRate_AL0", 0);
        cfg.ItemDropRate[1] = ini.GetInt(Section, "ItemDropRate_AL1", 0);
        cfg.ItemDropRate[2] = ini.GetInt(Section, "ItemDropRate_AL2", 0);
        cfg.ItemDropRate[3] = ini.GetInt(Section, "ItemDropRate_AL3", 0);

        cfg.MoneyDropTime = ini.GetInt(Section, "MoneyDropTime", 0);

        cfg.MoneyAmountDropRate[0] = ini.GetInt(Section, "MoneyAmountDropRate_AL0", 0);
        cfg.MoneyAmountDropRate[1] = ini.GetInt(Section, "MoneyAmountDropRate_AL1", 0);
        cfg.MoneyAmountDropRate[2] = ini.GetInt(Section, "MoneyAmountDropRate_AL2", 0);
        cfg.MoneyAmountDropRate[3] = ini.GetInt(Section, "MoneyAmountDropRate_AL3", 0);

        cfg.WeaponDurabilityRate = ini.GetInt(Section, "WeaponDurabilityRate", 0);

        cfg.ArmorDurabilityRate = ini.GetInt(Section, "ArmorDurabilityRate", 0);

        cfg.WingDurabilityRate = ini.GetInt(Section, "WingDurabilityRate", 0);

        cfg.GuardianDurabilityRate = ini.GetInt(Section, "GuardianDurabilityRate", 0);

        cfg.PendantDurabilityRate = ini.GetInt(Section, "PendantDurabilityRate", 0);

        cfg.RingDurabilityRate = ini.GetInt(Section, "RingDurabilityRate", 0);

        cfg.PetDurabilityRate = ini.GetInt(Section, "PetDurabilityRate", 0);

        cfg.TradeItemBlock = ini.GetInt(Section, "TradeItemBlock", 0);

        cfg.TradeItemBlockExc = ini.GetInt(Section, "TradeItemBlockExc", 0);

        cfg.TradeItemBlockSet = ini.GetInt(Section, "TradeItemBlockSet", 0);

        cfg.TradeItemBlockSell = ini.GetInt(Section, "TradeItemBlockSell", 0);

        cfg.MaxLevelUp = ini.GetInt(Section, "MaxLevelUp", 0);

        cfg.MaxLevelUpEvent = ini.GetInt(Section, "MaxLevelUpEvent", 0);

        cfg.MaxStatPoint[0] = ini.GetInt(Section, "MaxStatPoint_AL0", 0);
        cfg.MaxStatPoint[1] = ini.GetInt(Section, "MaxStatPoint_AL1", 0);
        cfg.MaxStatPoint[2] = ini.GetInt(Section, "MaxStatPoint_AL2", 0);
        cfg.MaxStatPoint[3] = ini.GetInt(Section, "MaxStatPoint_AL3", 0);

        cfg.LevelUpPoint[0, 0] = ini.GetInt(Section, "DWLevelUpPoint_AL0", 0);
        cfg.LevelUpPoint[0, 1] = ini.GetInt(Section, "DWLevelUpPoint_AL1", 0);
        cfg.LevelUpPoint[0, 2] = ini.GetInt(Section, "DWLevelUpPoint_AL2", 0);
        cfg.LevelUpPoint[0, 3] = ini.GetInt(Section, "DWLevelUpPoint_AL3", 0);
        cfg.LevelUpPoint[1, 0] = ini.GetInt(Section, "DKLevelUpPoint_AL0", 0);
        cfg.LevelUpPoint[1, 1] = ini.GetInt(Section, "DKLevelUpPoint_AL1", 0);
        cfg.LevelUpPoint[1, 2] = ini.GetInt(Section, "DKLevelUpPoint_AL2", 0);
        cfg.LevelUpPoint[1, 3] = ini.GetInt(Section, "DKLevelUpPoint_AL3", 0);
        cfg.LevelUpPoint[2, 0] = ini.GetInt(Section, "FELevelUpPoint_AL0", 0);
        cfg.LevelUpPoint[2, 1] = ini.GetInt(Section, "FELevelUpPoint_AL1", 0);
        cfg.LevelUpPoint[2, 2] = ini.GetInt(Section, "FELevelUpPoint_AL2", 0);
        cfg.LevelUpPoint[2, 3] = ini.GetInt(Section, "FELevelUpPoint_AL3", 0);
        cfg.LevelUpPoint[3, 0] = ini.GetInt(Section, "MGLevelUpPoint_AL0", 0);
        cfg.LevelUpPoint[3, 1] = ini.GetInt(Section, "MGLevelUpPoint_AL1", 0);
        cfg.LevelUpPoint[3, 2] = ini.GetInt(Section, "MGLevelUpPoint_AL2", 0);
        cfg.LevelUpPoint[3, 3] = ini.GetInt(Section, "MGLevelUpPoint_AL3", 0);
        cfg.LevelUpPoint[4, 0] = ini.GetInt(Section, "DLLevelUpPoint_AL0", 0);
        cfg.LevelUpPoint[4, 1] = ini.GetInt(Section, "DLLevelUpPoint_AL1", 0);
        cfg.LevelUpPoint[4, 2] = ini.GetInt(Section, "DLLevelUpPoint_AL2", 0);
        cfg.LevelUpPoint[4, 3] = ini.GetInt(Section, "DLLevelUpPoint_AL3", 0);

        cfg.PlusStatPoint = ini.GetInt(Section, "PlusStatPoint", 0);

        cfg.PlusStatMinLevel = ini.GetInt(Section, "PlusStatMinLevel", 0);

        cfg.CharacterCreateSwitch = ini.GetInt(Section, "CharacterCreateSwitch", 0);

        cfg.MGCreateLevel[0] = ini.GetInt(Section, "MGCreateLevel_AL0", 0);
        cfg.MGCreateLevel[1] = ini.GetInt(Section, "MGCreateLevel_AL1", 0);
        cfg.MGCreateLevel[2] = ini.GetInt(Section, "MGCreateLevel_AL2", 0);
        cfg.MGCreateLevel[3] = ini.GetInt(Section, "MGCreateLevel_AL3", 0);

        cfg.DLCreateLevel[0] = ini.GetInt(Section, "DLCreateLevel_AL0", 0);
        cfg.DLCreateLevel[1] = ini.GetInt(Section, "DLCreateLevel_AL1", 0);
        cfg.DLCreateLevel[2] = ini.GetInt(Section, "DLCreateLevel_AL2", 0);
        cfg.DLCreateLevel[3] = ini.GetInt(Section, "DLCreateLevel_AL3", 0);

        cfg.CharacterDeleteSwitch = ini.GetInt(Section, "CharacterDeleteSwitch", 0);

        cfg.CharacterDeleteMaxLevel = ini.GetInt(Section, "CharacterDeleteMaxLevel", 0);

        cfg.PartyReconnectTime = ini.GetInt(Section, "PartyReconnectTime", 0);

        cfg.PartyMoneyDistribute = ini.GetInt(Section, "PartyMoneyDistribute", 0);

        cfg.PartyDisableKillBetweenMembers = ini.GetInt(Section, "PartyDisableKillBetweenMembers", 0);

        cfg.PartyGeneralExperience[0] = ini.GetInt(Section, "PartyGeneralExperience1", 0);
        cfg.PartyGeneralExperience[1] = ini.GetInt(Section, "PartyGeneralExperience2", 0);
        cfg.PartyGeneralExperience[2] = ini.GetInt(Section, "PartyGeneralExperience3", 0);
        cfg.PartyGeneralExperience[3] = ini.GetInt(Section, "PartyGeneralExperience4", 0);
        cfg.PartyGeneralExperience[4] = ini.GetInt(Section, "PartyGeneralExperience5", 0);

        cfg.PartySpecialExperience[0] = ini.GetInt(Section, "PartySpecialExperience1", 0);
        cfg.PartySpecialExperience[1] = ini.GetInt(Section, "PartySpecialExperience2", 0);
        cfg.PartySpecialExperience[2] = ini.GetInt(Section, "PartySpecialExperience3", 0);
        cfg.PartySpecialExperience[3] = ini.GetInt(Section, "PartySpecialExperience4", 0);
        cfg.PartySpecialExperience[4] = ini.GetInt(Section, "PartySpecialExperience5", 0);

        cfg.PartyMaxGapLevel = ini.GetInt(Section, "PartyMaxGapLevel", 0);

        cfg.SoulSuccessRate[0] = ini.GetInt(Section, "SoulSuccessRate_AL0", 0);
        cfg.SoulSuccessRate[1] = ini.GetInt(Section, "SoulSuccessRate_AL1", 0);
        cfg.SoulSuccessRate[2] = ini.GetInt(Section, "SoulSuccessRate_AL2", 0);
        cfg.SoulSuccessRate[3] = ini.GetInt(Section, "SoulSuccessRate_AL3", 0);

        cfg.LifeSuccessRate[0] = ini.GetInt(Section, "LifeSuccessRate_AL0", 0);
        cfg.LifeSuccessRate[1] = ini.GetInt(Section, "LifeSuccessRate_AL1", 0);
        cfg.LifeSuccessRate[2] = ini.GetInt(Section, "LifeSuccessRate_AL2", 0);
        cfg.LifeSuccessRate[3] = ini.GetInt(Section, "LifeSuccessRate_AL3", 0);

        cfg.AddLuckSuccessRate1[0] = ini.GetInt(Section, "AddLuckSuccessRate1_AL0", 0);
        cfg.AddLuckSuccessRate1[1] = ini.GetInt(Section, "AddLuckSuccessRate1_AL1", 0);
        cfg.AddLuckSuccessRate1[2] = ini.GetInt(Section, "AddLuckSuccessRate1_AL2", 0);
        cfg.AddLuckSuccessRate1[3] = ini.GetInt(Section, "AddLuckSuccessRate1_AL3", 0);

        cfg.AddLuckSuccessRate2[0] = ini.GetInt(Section, "AddLuckSuccessRate2_AL0", 0);
        cfg.AddLuckSuccessRate2[1] = ini.GetInt(Section, "AddLuckSuccessRate2_AL1", 0);
        cfg.AddLuckSuccessRate2[2] = ini.GetInt(Section, "AddLuckSuccessRate2_AL2", 0);
        cfg.AddLuckSuccessRate2[3] = ini.GetInt(Section, "AddLuckSuccessRate2_AL3", 0);

        cfg.FruitAddPointMin = ini.GetInt(Section, "FruitAddPointMin", 0);

        cfg.FruitAddPointMax = ini.GetInt(Section, "FruitAddPointMax", 0);

        cfg.FruitAddPointSuccessRate[0] = ini.GetInt(Section, "FruitAddPointSuccessRate_AL0", 0);
        cfg.FruitAddPointSuccessRate[1] = ini.GetInt(Section, "FruitAddPointSuccessRate_AL1", 0);
        cfg.FruitAddPointSuccessRate[2] = ini.GetInt(Section, "FruitAddPointSuccessRate_AL2", 0);
        cfg.FruitAddPointSuccessRate[3] = ini.GetInt(Section, "FruitAddPointSuccessRate_AL3", 0);

        cfg.QuestMonsterItemDropParty = ini.GetInt(Section, "QuestMonsterItemDropParty", 0);

        cfg.CheckSpeedHack = ini.GetInt(Section, "CheckSpeedHack", 0);

        cfg.CheckSpeedHackTolerance = ini.GetInt(Section, "CheckSpeedHackTolerance", 0);

        cfg.CheckSpeedHackAction = ini.GetInt(Section, "CheckSpeedHackAction", 0);

        cfg.CheckLatencyHack = ini.GetInt(Section, "CheckLatencyHack", 0);

        cfg.CheckLatencyHackTolerance = ini.GetInt(Section, "CheckLatencyHackTolerance", 0);

        cfg.CheckLatencyHackAction = ini.GetInt(Section, "CheckLatencyHackAction", 0);

        cfg.CheckAutoPotionHack = ini.GetInt(Section, "CheckAutoPotionHack", 0);

        cfg.CheckAutoPotionHackTolerance = ini.GetInt(Section, "CheckAutoPotionHackTolerance", 0);

        cfg.CheckAutoPotionHackAction = ini.GetInt(Section, "CheckAutoPotionHackAction", 0);

        cfg.CheckAutoComboHack = ini.GetInt(Section, "CheckAutoComboHack", 0);

        cfg.CheckAutoComboHackTolerance = ini.GetInt(Section, "CheckAutoComboHackTolerance", 0);

        cfg.CheckAutoComboHackAction = ini.GetInt(Section, "CheckAutoComboHackAction", 0);

        cfg.CheckMoveHack = ini.GetInt(Section, "CheckMoveHack", 0);

        cfg.CheckMoveHackMaxDelay = ini.GetInt(Section, "CheckMoveHackMaxDelay", 0);

        cfg.CheckMoveHackMaxCount = ini.GetInt(Section, "CheckMoveHackMaxCount", 0);

        cfg.CheckMoveHackAction = ini.GetInt(Section, "CheckMoveHackAction", 0);

        return cfg;
    }
}
