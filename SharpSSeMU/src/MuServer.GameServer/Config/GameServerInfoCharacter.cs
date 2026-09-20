using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de cobertura COMPLETA de <c>GameServerInfo - Character.dat</c> (parte de <c>CServerInfo</c>,
/// ServerInfo.h/.cpp del árbol correcto -- ver doc-comment de <see cref="ServerInfoConfig"/> para la
/// explicación completa de por qué ese es el árbol correcto). A diferencia de las clases "curadas"
/// existentes (<see cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, que solo cargan
/// los campos con un consumidor real ya portado), esta clase carga TODOS los 155 campos de
/// este archivo (239 claves de .ini, algunos son arrays por AccountLevel 0-3 o por clase
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
public sealed class GameServerInfoCharacter
{
    private const string Section = "GameServerInfo";

    public int DuelDamageRate { get; private set; }
    public int CustomArenaDamageRate { get; private set; }
    public int ChaosCastleDamageRate { get; private set; }
    public int GeneralDamageRatePvP { get; private set; }
    public int GeneralDamageRatePvM { get; private set; }
    public int ReflectDamageRatePvP { get; private set; }
    public int ReflectDamageRatePvM { get; private set; }
    public int[] DamageRatePvP { get; } = new int[5];
    public int[] DamageRatePvM { get; } = new int[5];
    public int[,] DamageRateTo { get; } = new int[5, 5];
    public int[,] ReflectDamageRateTo { get; } = new int[5, 5];
    public int[] DamageStuckRate { get; } = new int[5];
    public int DamageStuckOnPetUniria { get; private set; }
    public int DamageStuckOnPetDinorant { get; private set; }
    public int DamageStuckOnPetDarkHorse { get; private set; }
    public int DKDamageMultiplierConstA { get; private set; }
    public int DLDamageMultiplierConstA { get; private set; }
    public int DKDamageMultiplierMaxRate { get; private set; }
    public int DLDamageMultiplierMaxRate { get; private set; }
    public int DarkSpiritRangeAttackRate { get; private set; }
    public int DarkSpiritCriticalDamageRate { get; private set; }
    public int DarkSpiritExcellentDamageRate { get; private set; }
    public int DarkSpiritAttackDamageMinConstA { get; private set; }
    public int DarkSpiritAttackDamageMinConstB { get; private set; }
    public int DarkSpiritAttackDamageMinConstC { get; private set; }
    public int DarkSpiritAttackDamageMaxConstA { get; private set; }
    public int DarkSpiritAttackDamageMaxConstB { get; private set; }
    public int DarkSpiritAttackDamageMaxConstC { get; private set; }
    public int DarkSpiritAttackSpeedConstA { get; private set; }
    public int DarkSpiritAttackSpeedConstB { get; private set; }
    public int DarkSpiritAttackSpeedConstC { get; private set; }
    public int DarkSpiritAttackSpeedConstD { get; private set; }
    public int DarkSpiritAttackSuccessRateConstA { get; private set; }
    public int DarkSpiritAttackSuccessRateConstB { get; private set; }
    public int DarkSpiritAttackSuccessRateConstC { get; private set; }
    public int[] ComboDamageConstA { get; } = new int[5];
    public int[] ComboDamageConstB { get; } = new int[5];
    public int[] ComboDamageConstC { get; } = new int[5];
    public int EarthquakeDamageConstA { get; private set; }
    public int EarthquakeDamageConstB { get; private set; }
    public int EarthquakeDamageConstC { get; private set; }
    public int ElectricSparkDamageConstA { get; private set; }
    public int ElectricSparkDamageConstB { get; private set; }
    public int DLSkillDamageConstA { get; private set; }
    public int DLSkillDamageConstB { get; private set; }
    public int NovaDamageConstA { get; private set; }
    public int NovaDamageConstB { get; private set; }
    public int NovaDamageConstC { get; private set; }
    public int[] HPRecoveryRate { get; } = new int[5];
    public int[] MPRecoveryRate { get; } = new int[5];
    public int[] BPRecoveryRate { get; } = new int[5];
    public int DWPhysiDamageMinConstA { get; private set; }
    public int DWPhysiDamageMaxConstA { get; private set; }
    public int DWMagicDamageMinConstA { get; private set; }
    public int DWMagicDamageMaxConstA { get; private set; }
    public int DKPhysiDamageMinConstA { get; private set; }
    public int DKPhysiDamageMaxConstA { get; private set; }
    public int DKMagicDamageMinConstA { get; private set; }
    public int DKMagicDamageMaxConstA { get; private set; }
    public int FEPhysiDamageMinConstA { get; private set; }
    public int FEPhysiDamageMaxConstA { get; private set; }
    public int FEPhysiDamageMinBowConstA { get; private set; }
    public int FEPhysiDamageMinBowConstB { get; private set; }
    public int FEPhysiDamageMaxBowConstA { get; private set; }
    public int FEPhysiDamageMaxBowConstB { get; private set; }
    public int FEMagicDamageMinConstA { get; private set; }
    public int FEMagicDamageMaxConstA { get; private set; }
    public int MGPhysiDamageMinConstA { get; private set; }
    public int MGPhysiDamageMinConstB { get; private set; }
    public int MGPhysiDamageMaxConstA { get; private set; }
    public int MGPhysiDamageMaxConstB { get; private set; }
    public int MGMagicDamageMinConstA { get; private set; }
    public int MGMagicDamageMaxConstA { get; private set; }
    public int DLPhysiDamageMinConstA { get; private set; }
    public int DLPhysiDamageMinConstB { get; private set; }
    public int DLPhysiDamageMaxConstA { get; private set; }
    public int DLPhysiDamageMaxConstB { get; private set; }
    public int DLMagicDamageMinConstA { get; private set; }
    public int DLMagicDamageMaxConstA { get; private set; }
    public int DWAttackSuccessRateConstA { get; private set; }
    public int DWAttackSuccessRateConstB { get; private set; }
    public int DWAttackSuccessRateConstC { get; private set; }
    public int DWAttackSuccessRateConstD { get; private set; }
    public int DKAttackSuccessRateConstA { get; private set; }
    public int DKAttackSuccessRateConstB { get; private set; }
    public int DKAttackSuccessRateConstC { get; private set; }
    public int DKAttackSuccessRateConstD { get; private set; }
    public int FEAttackSuccessRateConstA { get; private set; }
    public int FEAttackSuccessRateConstB { get; private set; }
    public int FEAttackSuccessRateConstC { get; private set; }
    public int FEAttackSuccessRateConstD { get; private set; }
    public int MGAttackSuccessRateConstA { get; private set; }
    public int MGAttackSuccessRateConstB { get; private set; }
    public int MGAttackSuccessRateConstC { get; private set; }
    public int MGAttackSuccessRateConstD { get; private set; }
    public int DLAttackSuccessRateConstA { get; private set; }
    public int DLAttackSuccessRateConstB { get; private set; }
    public int DLAttackSuccessRateConstC { get; private set; }
    public int DLAttackSuccessRateConstD { get; private set; }
    public int DLAttackSuccessRateConstE { get; private set; }
    public int DWAttackSuccessRatePvPConstA { get; private set; }
    public int DWAttackSuccessRatePvPConstB { get; private set; }
    public int DWAttackSuccessRatePvPConstC { get; private set; }
    public int DWAttackSuccessRatePvPConstD { get; private set; }
    public int DKAttackSuccessRatePvPConstA { get; private set; }
    public int DKAttackSuccessRatePvPConstB { get; private set; }
    public int DKAttackSuccessRatePvPConstC { get; private set; }
    public int DKAttackSuccessRatePvPConstD { get; private set; }
    public int FEAttackSuccessRatePvPConstA { get; private set; }
    public int FEAttackSuccessRatePvPConstB { get; private set; }
    public int FEAttackSuccessRatePvPConstC { get; private set; }
    public int FEAttackSuccessRatePvPConstD { get; private set; }
    public int MGAttackSuccessRatePvPConstA { get; private set; }
    public int MGAttackSuccessRatePvPConstB { get; private set; }
    public int MGAttackSuccessRatePvPConstC { get; private set; }
    public int MGAttackSuccessRatePvPConstD { get; private set; }
    public int DLAttackSuccessRatePvPConstA { get; private set; }
    public int DLAttackSuccessRatePvPConstB { get; private set; }
    public int DLAttackSuccessRatePvPConstC { get; private set; }
    public int DLAttackSuccessRatePvPConstD { get; private set; }
    public int DWPhysiSpeedConstA { get; private set; }
    public int DWMagicSpeedConstA { get; private set; }
    public int DKPhysiSpeedConstA { get; private set; }
    public int DKMagicSpeedConstA { get; private set; }
    public int FEPhysiSpeedConstA { get; private set; }
    public int FEMagicSpeedConstA { get; private set; }
    public int MGPhysiSpeedConstA { get; private set; }
    public int MGMagicSpeedConstA { get; private set; }
    public int DLPhysiSpeedConstA { get; private set; }
    public int DLMagicSpeedConstA { get; private set; }
    public int DWDefenseSuccessRateConstA { get; private set; }
    public int DKDefenseSuccessRateConstA { get; private set; }
    public int FEDefenseSuccessRateConstA { get; private set; }
    public int MGDefenseSuccessRateConstA { get; private set; }
    public int DLDefenseSuccessRateConstA { get; private set; }
    public int DWDefenseSuccessRatePvPConstA { get; private set; }
    public int DWDefenseSuccessRatePvPConstB { get; private set; }
    public int DWDefenseSuccessRatePvPConstC { get; private set; }
    public int DKDefenseSuccessRatePvPConstA { get; private set; }
    public int DKDefenseSuccessRatePvPConstB { get; private set; }
    public int DKDefenseSuccessRatePvPConstC { get; private set; }
    public int FEDefenseSuccessRatePvPConstA { get; private set; }
    public int FEDefenseSuccessRatePvPConstB { get; private set; }
    public int FEDefenseSuccessRatePvPConstC { get; private set; }
    public int MGDefenseSuccessRatePvPConstA { get; private set; }
    public int MGDefenseSuccessRatePvPConstB { get; private set; }
    public int MGDefenseSuccessRatePvPConstC { get; private set; }
    public int DLDefenseSuccessRatePvPConstA { get; private set; }
    public int DLDefenseSuccessRatePvPConstB { get; private set; }
    public int DLDefenseSuccessRatePvPConstC { get; private set; }
    public int DWDefenseConstA { get; private set; }
    public int DKDefenseConstA { get; private set; }
    public int FEDefenseConstA { get; private set; }
    public int MGDefenseConstA { get; private set; }
    public int DLDefenseConstA { get; private set; }

    public static GameServerInfoCharacter Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoCharacter();

        cfg.DuelDamageRate = ini.GetInt(Section, "DuelDamageRate", 0);

        cfg.CustomArenaDamageRate = ini.GetInt(Section, "CustomArenaDamageRate", 0);

        cfg.ChaosCastleDamageRate = ini.GetInt(Section, "ChaosCastleDamageRate", 0);

        cfg.GeneralDamageRatePvP = ini.GetInt(Section, "GeneralDamageRatePvP", 0);

        cfg.GeneralDamageRatePvM = ini.GetInt(Section, "GeneralDamageRatePvM", 0);

        cfg.ReflectDamageRatePvP = ini.GetInt(Section, "ReflectDamageRatePvP", 0);

        cfg.ReflectDamageRatePvM = ini.GetInt(Section, "ReflectDamageRatePvM", 0);

        cfg.DamageRatePvP[0] = ini.GetInt(Section, "DWDamageRatePvP", 0);
        cfg.DamageRatePvP[1] = ini.GetInt(Section, "DKDamageRatePvP", 0);
        cfg.DamageRatePvP[2] = ini.GetInt(Section, "FEDamageRatePvP", 0);
        cfg.DamageRatePvP[3] = ini.GetInt(Section, "MGDamageRatePvP", 0);
        cfg.DamageRatePvP[4] = ini.GetInt(Section, "DLDamageRatePvP", 0);

        cfg.DamageRatePvM[0] = ini.GetInt(Section, "DWDamageRatePvM", 0);
        cfg.DamageRatePvM[1] = ini.GetInt(Section, "DKDamageRatePvM", 0);
        cfg.DamageRatePvM[2] = ini.GetInt(Section, "FEDamageRatePvM", 0);
        cfg.DamageRatePvM[3] = ini.GetInt(Section, "MGDamageRatePvM", 0);
        cfg.DamageRatePvM[4] = ini.GetInt(Section, "DLDamageRatePvM", 0);

        cfg.DamageRateTo[0, 0] = ini.GetInt(Section, "DWDamageRateToDW", 0);
        cfg.DamageRateTo[0, 1] = ini.GetInt(Section, "DWDamageRateToDK", 0);
        cfg.DamageRateTo[0, 2] = ini.GetInt(Section, "DWDamageRateToFE", 0);
        cfg.DamageRateTo[0, 3] = ini.GetInt(Section, "DWDamageRateToMG", 0);
        cfg.DamageRateTo[0, 4] = ini.GetInt(Section, "DWDamageRateToDL", 0);
        cfg.DamageRateTo[1, 0] = ini.GetInt(Section, "DKDamageRateToDW", 0);
        cfg.DamageRateTo[1, 1] = ini.GetInt(Section, "DKDamageRateToDK", 0);
        cfg.DamageRateTo[1, 2] = ini.GetInt(Section, "DKDamageRateToFE", 0);
        cfg.DamageRateTo[1, 3] = ini.GetInt(Section, "DKDamageRateToMG", 0);
        cfg.DamageRateTo[1, 4] = ini.GetInt(Section, "DKDamageRateToDL", 0);
        cfg.DamageRateTo[2, 0] = ini.GetInt(Section, "FEDamageRateToDW", 0);
        cfg.DamageRateTo[2, 1] = ini.GetInt(Section, "FEDamageRateToDK", 0);
        cfg.DamageRateTo[2, 2] = ini.GetInt(Section, "FEDamageRateToFE", 0);
        cfg.DamageRateTo[2, 3] = ini.GetInt(Section, "FEDamageRateToMG", 0);
        cfg.DamageRateTo[2, 4] = ini.GetInt(Section, "FEDamageRateToDL", 0);
        cfg.DamageRateTo[3, 0] = ini.GetInt(Section, "MGDamageRateToDW", 0);
        cfg.DamageRateTo[3, 1] = ini.GetInt(Section, "MGDamageRateToDK", 0);
        cfg.DamageRateTo[3, 2] = ini.GetInt(Section, "MGDamageRateToFE", 0);
        cfg.DamageRateTo[3, 3] = ini.GetInt(Section, "MGDamageRateToMG", 0);
        cfg.DamageRateTo[3, 4] = ini.GetInt(Section, "MGDamageRateToDL", 0);
        cfg.DamageRateTo[4, 0] = ini.GetInt(Section, "DLDamageRateToDW", 0);
        cfg.DamageRateTo[4, 1] = ini.GetInt(Section, "DLDamageRateToDK", 0);
        cfg.DamageRateTo[4, 2] = ini.GetInt(Section, "DLDamageRateToFE", 0);
        cfg.DamageRateTo[4, 3] = ini.GetInt(Section, "DLDamageRateToMG", 0);
        cfg.DamageRateTo[4, 4] = ini.GetInt(Section, "DLDamageRateToDL", 0);

        cfg.ReflectDamageRateTo[0, 0] = ini.GetInt(Section, "DWReflectDamageRateToDW", 0);
        cfg.ReflectDamageRateTo[0, 1] = ini.GetInt(Section, "DWReflectDamageRateToDK", 0);
        cfg.ReflectDamageRateTo[0, 2] = ini.GetInt(Section, "DWReflectDamageRateToFE", 0);
        cfg.ReflectDamageRateTo[0, 3] = ini.GetInt(Section, "DWReflectDamageRateToMG", 0);
        cfg.ReflectDamageRateTo[0, 4] = ini.GetInt(Section, "DWReflectDamageRateToDL", 0);
        cfg.ReflectDamageRateTo[1, 0] = ini.GetInt(Section, "DKReflectDamageRateToDW", 0);
        cfg.ReflectDamageRateTo[1, 1] = ini.GetInt(Section, "DKReflectDamageRateToDK", 0);
        cfg.ReflectDamageRateTo[1, 2] = ini.GetInt(Section, "DKReflectDamageRateToFE", 0);
        cfg.ReflectDamageRateTo[1, 3] = ini.GetInt(Section, "DKReflectDamageRateToMG", 0);
        cfg.ReflectDamageRateTo[1, 4] = ini.GetInt(Section, "DKReflectDamageRateToDL", 0);
        cfg.ReflectDamageRateTo[2, 0] = ini.GetInt(Section, "FEReflectDamageRateToDW", 0);
        cfg.ReflectDamageRateTo[2, 1] = ini.GetInt(Section, "FEReflectDamageRateToDK", 0);
        cfg.ReflectDamageRateTo[2, 2] = ini.GetInt(Section, "FEReflectDamageRateToFE", 0);
        cfg.ReflectDamageRateTo[2, 3] = ini.GetInt(Section, "FEReflectDamageRateToMG", 0);
        cfg.ReflectDamageRateTo[2, 4] = ini.GetInt(Section, "FEReflectDamageRateToDL", 0);
        cfg.ReflectDamageRateTo[3, 0] = ini.GetInt(Section, "MGReflectDamageRateToDW", 0);
        cfg.ReflectDamageRateTo[3, 1] = ini.GetInt(Section, "MGReflectDamageRateToDK", 0);
        cfg.ReflectDamageRateTo[3, 2] = ini.GetInt(Section, "MGReflectDamageRateToFE", 0);
        cfg.ReflectDamageRateTo[3, 3] = ini.GetInt(Section, "MGReflectDamageRateToMG", 0);
        cfg.ReflectDamageRateTo[3, 4] = ini.GetInt(Section, "MGReflectDamageRateToDL", 0);
        cfg.ReflectDamageRateTo[4, 0] = ini.GetInt(Section, "DLReflectDamageRateToDW", 0);
        cfg.ReflectDamageRateTo[4, 1] = ini.GetInt(Section, "DLReflectDamageRateToDK", 0);
        cfg.ReflectDamageRateTo[4, 2] = ini.GetInt(Section, "DLReflectDamageRateToFE", 0);
        cfg.ReflectDamageRateTo[4, 3] = ini.GetInt(Section, "DLReflectDamageRateToMG", 0);
        cfg.ReflectDamageRateTo[4, 4] = ini.GetInt(Section, "DLReflectDamageRateToDL", 0);

        cfg.DamageStuckRate[0] = ini.GetInt(Section, "DWDamageStuckRate", 0);
        cfg.DamageStuckRate[1] = ini.GetInt(Section, "DKDamageStuckRate", 0);
        cfg.DamageStuckRate[2] = ini.GetInt(Section, "FEDamageStuckRate", 0);
        cfg.DamageStuckRate[3] = ini.GetInt(Section, "MGDamageStuckRate", 0);
        cfg.DamageStuckRate[4] = ini.GetInt(Section, "DLDamageStuckRate", 0);

        cfg.DamageStuckOnPetUniria = ini.GetInt(Section, "DamageStuckOnPetUniria", 0);

        cfg.DamageStuckOnPetDinorant = ini.GetInt(Section, "DamageStuckOnPetDinorant", 0);

        cfg.DamageStuckOnPetDarkHorse = ini.GetInt(Section, "DamageStuckOnPetDarkHorse", 0);

        cfg.DKDamageMultiplierConstA = ini.GetInt(Section, "DKDamageMultiplierConstA", 0);

        cfg.DLDamageMultiplierConstA = ini.GetInt(Section, "DLDamageMultiplierConstA", 0);

        cfg.DKDamageMultiplierMaxRate = ini.GetInt(Section, "DKDamageMultiplierMaxRate", 0);

        cfg.DLDamageMultiplierMaxRate = ini.GetInt(Section, "DLDamageMultiplierMaxRate", 0);

        cfg.DarkSpiritRangeAttackRate = ini.GetInt(Section, "DarkSpiritRangeAttackRate", 0);

        cfg.DarkSpiritCriticalDamageRate = ini.GetInt(Section, "DarkSpiritCriticalDamageRate", 0);

        cfg.DarkSpiritExcellentDamageRate = ini.GetInt(Section, "DarkSpiritExcellentDamageRate", 0);

        cfg.DarkSpiritAttackDamageMinConstA = ini.GetInt(Section, "DarkSpiritAttackDamageMinConstA", 0);

        cfg.DarkSpiritAttackDamageMinConstB = ini.GetInt(Section, "DarkSpiritAttackDamageMinConstB", 0);

        cfg.DarkSpiritAttackDamageMinConstC = ini.GetInt(Section, "DarkSpiritAttackDamageMinConstC", 0);

        cfg.DarkSpiritAttackDamageMaxConstA = ini.GetInt(Section, "DarkSpiritAttackDamageMaxConstA", 0);

        cfg.DarkSpiritAttackDamageMaxConstB = ini.GetInt(Section, "DarkSpiritAttackDamageMaxConstB", 0);

        cfg.DarkSpiritAttackDamageMaxConstC = ini.GetInt(Section, "DarkSpiritAttackDamageMaxConstC", 0);

        cfg.DarkSpiritAttackSpeedConstA = ini.GetInt(Section, "DarkSpiritAttackSpeedConstA", 0);

        cfg.DarkSpiritAttackSpeedConstB = ini.GetInt(Section, "DarkSpiritAttackSpeedConstB", 0);

        cfg.DarkSpiritAttackSpeedConstC = ini.GetInt(Section, "DarkSpiritAttackSpeedConstC", 0);

        cfg.DarkSpiritAttackSpeedConstD = ini.GetInt(Section, "DarkSpiritAttackSpeedConstD", 0);

        cfg.DarkSpiritAttackSuccessRateConstA = ini.GetInt(Section, "DarkSpiritAttackSuccessRateConstA", 0);

        cfg.DarkSpiritAttackSuccessRateConstB = ini.GetInt(Section, "DarkSpiritAttackSuccessRateConstB", 0);

        cfg.DarkSpiritAttackSuccessRateConstC = ini.GetInt(Section, "DarkSpiritAttackSuccessRateConstC", 0);

        cfg.ComboDamageConstA[0] = ini.GetInt(Section, "DWComboDamageConstA", 0);
        cfg.ComboDamageConstA[1] = ini.GetInt(Section, "DKComboDamageConstA", 0);
        cfg.ComboDamageConstA[2] = ini.GetInt(Section, "FEComboDamageConstA", 0);
        cfg.ComboDamageConstA[3] = ini.GetInt(Section, "MGComboDamageConstA", 0);
        cfg.ComboDamageConstA[4] = ini.GetInt(Section, "DLComboDamageConstA", 0);

        cfg.ComboDamageConstB[0] = ini.GetInt(Section, "DWComboDamageConstB", 0);
        cfg.ComboDamageConstB[1] = ini.GetInt(Section, "DKComboDamageConstB", 0);
        cfg.ComboDamageConstB[2] = ini.GetInt(Section, "FEComboDamageConstB", 0);
        cfg.ComboDamageConstB[3] = ini.GetInt(Section, "MGComboDamageConstB", 0);
        cfg.ComboDamageConstB[4] = ini.GetInt(Section, "DLComboDamageConstB", 0);

        cfg.ComboDamageConstC[0] = ini.GetInt(Section, "DWComboDamageConstC", 0);
        cfg.ComboDamageConstC[1] = ini.GetInt(Section, "DKComboDamageConstC", 0);
        cfg.ComboDamageConstC[2] = ini.GetInt(Section, "FEComboDamageConstC", 0);
        cfg.ComboDamageConstC[3] = ini.GetInt(Section, "MGComboDamageConstC", 0);
        cfg.ComboDamageConstC[4] = ini.GetInt(Section, "DLComboDamageConstC", 0);

        cfg.EarthquakeDamageConstA = ini.GetInt(Section, "EarthquakeDamageConstA", 0);

        cfg.EarthquakeDamageConstB = ini.GetInt(Section, "EarthquakeDamageConstB", 0);

        cfg.EarthquakeDamageConstC = ini.GetInt(Section, "EarthquakeDamageConstC", 0);

        cfg.ElectricSparkDamageConstA = ini.GetInt(Section, "ElectricSparkDamageConstA", 0);

        cfg.ElectricSparkDamageConstB = ini.GetInt(Section, "ElectricSparkDamageConstB", 0);

        cfg.DLSkillDamageConstA = ini.GetInt(Section, "DLSkillDamageConstA", 0);

        cfg.DLSkillDamageConstB = ini.GetInt(Section, "DLSkillDamageConstB", 0);

        cfg.NovaDamageConstA = ini.GetInt(Section, "NovaDamageConstA", 0);

        cfg.NovaDamageConstB = ini.GetInt(Section, "NovaDamageConstB", 0);

        cfg.NovaDamageConstC = ini.GetInt(Section, "NovaDamageConstC", 0);

        cfg.HPRecoveryRate[0] = ini.GetInt(Section, "DWHPRecoveryRate", 0);
        cfg.HPRecoveryRate[1] = ini.GetInt(Section, "DKHPRecoveryRate", 0);
        cfg.HPRecoveryRate[2] = ini.GetInt(Section, "FEHPRecoveryRate", 0);
        cfg.HPRecoveryRate[3] = ini.GetInt(Section, "MGHPRecoveryRate", 0);
        cfg.HPRecoveryRate[4] = ini.GetInt(Section, "DLHPRecoveryRate", 0);

        cfg.MPRecoveryRate[0] = ini.GetInt(Section, "DWMPRecoveryRate", 0);
        cfg.MPRecoveryRate[1] = ini.GetInt(Section, "DKMPRecoveryRate", 0);
        cfg.MPRecoveryRate[2] = ini.GetInt(Section, "FEMPRecoveryRate", 0);
        cfg.MPRecoveryRate[3] = ini.GetInt(Section, "MGMPRecoveryRate", 0);
        cfg.MPRecoveryRate[4] = ini.GetInt(Section, "DLMPRecoveryRate", 0);

        cfg.BPRecoveryRate[0] = ini.GetInt(Section, "DWBPRecoveryRate", 0);
        cfg.BPRecoveryRate[1] = ini.GetInt(Section, "DKBPRecoveryRate", 0);
        cfg.BPRecoveryRate[2] = ini.GetInt(Section, "FEBPRecoveryRate", 0);
        cfg.BPRecoveryRate[3] = ini.GetInt(Section, "MGBPRecoveryRate", 0);
        cfg.BPRecoveryRate[4] = ini.GetInt(Section, "DLBPRecoveryRate", 0);

        cfg.DWPhysiDamageMinConstA = ini.GetInt(Section, "DWPhysiDamageMinConstA", 0);

        cfg.DWPhysiDamageMaxConstA = ini.GetInt(Section, "DWPhysiDamageMaxConstA", 0);

        cfg.DWMagicDamageMinConstA = ini.GetInt(Section, "DWMagicDamageMinConstA", 0);

        cfg.DWMagicDamageMaxConstA = ini.GetInt(Section, "DWMagicDamageMaxConstA", 0);

        cfg.DKPhysiDamageMinConstA = ini.GetInt(Section, "DKPhysiDamageMinConstA", 0);

        cfg.DKPhysiDamageMaxConstA = ini.GetInt(Section, "DKPhysiDamageMaxConstA", 0);

        cfg.DKMagicDamageMinConstA = ini.GetInt(Section, "DKMagicDamageMinConstA", 0);

        cfg.DKMagicDamageMaxConstA = ini.GetInt(Section, "DKMagicDamageMaxConstA", 0);

        cfg.FEPhysiDamageMinConstA = ini.GetInt(Section, "FEPhysiDamageMinConstA", 0);

        cfg.FEPhysiDamageMaxConstA = ini.GetInt(Section, "FEPhysiDamageMaxConstA", 0);

        cfg.FEPhysiDamageMinBowConstA = ini.GetInt(Section, "FEPhysiDamageMinBowConstA", 0);

        cfg.FEPhysiDamageMinBowConstB = ini.GetInt(Section, "FEPhysiDamageMinBowConstB", 0);

        cfg.FEPhysiDamageMaxBowConstA = ini.GetInt(Section, "FEPhysiDamageMaxBowConstA", 0);

        cfg.FEPhysiDamageMaxBowConstB = ini.GetInt(Section, "FEPhysiDamageMaxBowConstB", 0);

        cfg.FEMagicDamageMinConstA = ini.GetInt(Section, "FEMagicDamageMinConstA", 0);

        cfg.FEMagicDamageMaxConstA = ini.GetInt(Section, "FEMagicDamageMaxConstA", 0);

        cfg.MGPhysiDamageMinConstA = ini.GetInt(Section, "MGPhysiDamageMinConstA", 0);

        cfg.MGPhysiDamageMinConstB = ini.GetInt(Section, "MGPhysiDamageMinConstB", 0);

        cfg.MGPhysiDamageMaxConstA = ini.GetInt(Section, "MGPhysiDamageMaxConstA", 0);

        cfg.MGPhysiDamageMaxConstB = ini.GetInt(Section, "MGPhysiDamageMaxConstB", 0);

        cfg.MGMagicDamageMinConstA = ini.GetInt(Section, "MGMagicDamageMinConstA", 0);

        cfg.MGMagicDamageMaxConstA = ini.GetInt(Section, "MGMagicDamageMaxConstA", 0);

        cfg.DLPhysiDamageMinConstA = ini.GetInt(Section, "DLPhysiDamageMinConstA", 0);

        cfg.DLPhysiDamageMinConstB = ini.GetInt(Section, "DLPhysiDamageMinConstB", 0);

        cfg.DLPhysiDamageMaxConstA = ini.GetInt(Section, "DLPhysiDamageMaxConstA", 0);

        cfg.DLPhysiDamageMaxConstB = ini.GetInt(Section, "DLPhysiDamageMaxConstB", 0);

        cfg.DLMagicDamageMinConstA = ini.GetInt(Section, "DLMagicDamageMinConstA", 0);

        cfg.DLMagicDamageMaxConstA = ini.GetInt(Section, "DLMagicDamageMaxConstA", 0);

        cfg.DWAttackSuccessRateConstA = ini.GetInt(Section, "DWAttackSuccessRateConstA", 0);

        cfg.DWAttackSuccessRateConstB = ini.GetInt(Section, "DWAttackSuccessRateConstB", 0);

        cfg.DWAttackSuccessRateConstC = ini.GetInt(Section, "DWAttackSuccessRateConstC", 0);

        cfg.DWAttackSuccessRateConstD = ini.GetInt(Section, "DWAttackSuccessRateConstD", 0);

        cfg.DKAttackSuccessRateConstA = ini.GetInt(Section, "DKAttackSuccessRateConstA", 0);

        cfg.DKAttackSuccessRateConstB = ini.GetInt(Section, "DKAttackSuccessRateConstB", 0);

        cfg.DKAttackSuccessRateConstC = ini.GetInt(Section, "DKAttackSuccessRateConstC", 0);

        cfg.DKAttackSuccessRateConstD = ini.GetInt(Section, "DKAttackSuccessRateConstD", 0);

        cfg.FEAttackSuccessRateConstA = ini.GetInt(Section, "FEAttackSuccessRateConstA", 0);

        cfg.FEAttackSuccessRateConstB = ini.GetInt(Section, "FEAttackSuccessRateConstB", 0);

        cfg.FEAttackSuccessRateConstC = ini.GetInt(Section, "FEAttackSuccessRateConstC", 0);

        cfg.FEAttackSuccessRateConstD = ini.GetInt(Section, "FEAttackSuccessRateConstD", 0);

        cfg.MGAttackSuccessRateConstA = ini.GetInt(Section, "MGAttackSuccessRateConstA", 0);

        cfg.MGAttackSuccessRateConstB = ini.GetInt(Section, "MGAttackSuccessRateConstB", 0);

        cfg.MGAttackSuccessRateConstC = ini.GetInt(Section, "MGAttackSuccessRateConstC", 0);

        cfg.MGAttackSuccessRateConstD = ini.GetInt(Section, "MGAttackSuccessRateConstD", 0);

        cfg.DLAttackSuccessRateConstA = ini.GetInt(Section, "DLAttackSuccessRateConstA", 0);

        cfg.DLAttackSuccessRateConstB = ini.GetInt(Section, "DLAttackSuccessRateConstB", 0);

        cfg.DLAttackSuccessRateConstC = ini.GetInt(Section, "DLAttackSuccessRateConstC", 0);

        cfg.DLAttackSuccessRateConstD = ini.GetInt(Section, "DLAttackSuccessRateConstD", 0);

        cfg.DLAttackSuccessRateConstE = ini.GetInt(Section, "DLAttackSuccessRateConstE", 0);

        cfg.DWAttackSuccessRatePvPConstA = ini.GetInt(Section, "DWAttackSuccessRatePvPConstA", 0);

        cfg.DWAttackSuccessRatePvPConstB = ini.GetInt(Section, "DWAttackSuccessRatePvPConstB", 0);

        cfg.DWAttackSuccessRatePvPConstC = ini.GetInt(Section, "DWAttackSuccessRatePvPConstC", 0);

        cfg.DWAttackSuccessRatePvPConstD = ini.GetInt(Section, "DWAttackSuccessRatePvPConstD", 0);

        cfg.DKAttackSuccessRatePvPConstA = ini.GetInt(Section, "DKAttackSuccessRatePvPConstA", 0);

        cfg.DKAttackSuccessRatePvPConstB = ini.GetInt(Section, "DKAttackSuccessRatePvPConstB", 0);

        cfg.DKAttackSuccessRatePvPConstC = ini.GetInt(Section, "DKAttackSuccessRatePvPConstC", 0);

        cfg.DKAttackSuccessRatePvPConstD = ini.GetInt(Section, "DKAttackSuccessRatePvPConstD", 0);

        cfg.FEAttackSuccessRatePvPConstA = ini.GetInt(Section, "FEAttackSuccessRatePvPConstA", 0);

        cfg.FEAttackSuccessRatePvPConstB = ini.GetInt(Section, "FEAttackSuccessRatePvPConstB", 0);

        cfg.FEAttackSuccessRatePvPConstC = ini.GetInt(Section, "FEAttackSuccessRatePvPConstC", 0);

        cfg.FEAttackSuccessRatePvPConstD = ini.GetInt(Section, "FEAttackSuccessRatePvPConstD", 0);

        cfg.MGAttackSuccessRatePvPConstA = ini.GetInt(Section, "MGAttackSuccessRatePvPConstA", 0);

        cfg.MGAttackSuccessRatePvPConstB = ini.GetInt(Section, "MGAttackSuccessRatePvPConstB", 0);

        cfg.MGAttackSuccessRatePvPConstC = ini.GetInt(Section, "MGAttackSuccessRatePvPConstC", 0);

        cfg.MGAttackSuccessRatePvPConstD = ini.GetInt(Section, "MGAttackSuccessRatePvPConstD", 0);

        cfg.DLAttackSuccessRatePvPConstA = ini.GetInt(Section, "DLAttackSuccessRatePvPConstA", 0);

        cfg.DLAttackSuccessRatePvPConstB = ini.GetInt(Section, "DLAttackSuccessRatePvPConstB", 0);

        cfg.DLAttackSuccessRatePvPConstC = ini.GetInt(Section, "DLAttackSuccessRatePvPConstC", 0);

        cfg.DLAttackSuccessRatePvPConstD = ini.GetInt(Section, "DLAttackSuccessRatePvPConstD", 0);

        cfg.DWPhysiSpeedConstA = ini.GetInt(Section, "DWPhysiSpeedConstA", 0);

        cfg.DWMagicSpeedConstA = ini.GetInt(Section, "DWMagicSpeedConstA", 0);

        cfg.DKPhysiSpeedConstA = ini.GetInt(Section, "DKPhysiSpeedConstA", 0);

        cfg.DKMagicSpeedConstA = ini.GetInt(Section, "DKMagicSpeedConstA", 0);

        cfg.FEPhysiSpeedConstA = ini.GetInt(Section, "FEPhysiSpeedConstA", 0);

        cfg.FEMagicSpeedConstA = ini.GetInt(Section, "FEMagicSpeedConstA", 0);

        cfg.MGPhysiSpeedConstA = ini.GetInt(Section, "MGPhysiSpeedConstA", 0);

        cfg.MGMagicSpeedConstA = ini.GetInt(Section, "MGMagicSpeedConstA", 0);

        cfg.DLPhysiSpeedConstA = ini.GetInt(Section, "DLPhysiSpeedConstA", 0);

        cfg.DLMagicSpeedConstA = ini.GetInt(Section, "DLMagicSpeedConstA", 0);

        cfg.DWDefenseSuccessRateConstA = ini.GetInt(Section, "DWDefenseSuccessRateConstA", 0);

        cfg.DKDefenseSuccessRateConstA = ini.GetInt(Section, "DKDefenseSuccessRateConstA", 0);

        cfg.FEDefenseSuccessRateConstA = ini.GetInt(Section, "FEDefenseSuccessRateConstA", 0);

        cfg.MGDefenseSuccessRateConstA = ini.GetInt(Section, "MGDefenseSuccessRateConstA", 0);

        cfg.DLDefenseSuccessRateConstA = ini.GetInt(Section, "DLDefenseSuccessRateConstA", 0);

        cfg.DWDefenseSuccessRatePvPConstA = ini.GetInt(Section, "DWDefenseSuccessRatePvPConstA", 0);

        cfg.DWDefenseSuccessRatePvPConstB = ini.GetInt(Section, "DWDefenseSuccessRatePvPConstB", 0);

        cfg.DWDefenseSuccessRatePvPConstC = ini.GetInt(Section, "DWDefenseSuccessRatePvPConstC", 0);

        cfg.DKDefenseSuccessRatePvPConstA = ini.GetInt(Section, "DKDefenseSuccessRatePvPConstA", 0);

        cfg.DKDefenseSuccessRatePvPConstB = ini.GetInt(Section, "DKDefenseSuccessRatePvPConstB", 0);

        cfg.DKDefenseSuccessRatePvPConstC = ini.GetInt(Section, "DKDefenseSuccessRatePvPConstC", 0);

        cfg.FEDefenseSuccessRatePvPConstA = ini.GetInt(Section, "FEDefenseSuccessRatePvPConstA", 0);

        cfg.FEDefenseSuccessRatePvPConstB = ini.GetInt(Section, "FEDefenseSuccessRatePvPConstB", 0);

        cfg.FEDefenseSuccessRatePvPConstC = ini.GetInt(Section, "FEDefenseSuccessRatePvPConstC", 0);

        cfg.MGDefenseSuccessRatePvPConstA = ini.GetInt(Section, "MGDefenseSuccessRatePvPConstA", 0);

        cfg.MGDefenseSuccessRatePvPConstB = ini.GetInt(Section, "MGDefenseSuccessRatePvPConstB", 0);

        cfg.MGDefenseSuccessRatePvPConstC = ini.GetInt(Section, "MGDefenseSuccessRatePvPConstC", 0);

        cfg.DLDefenseSuccessRatePvPConstA = ini.GetInt(Section, "DLDefenseSuccessRatePvPConstA", 0);

        cfg.DLDefenseSuccessRatePvPConstB = ini.GetInt(Section, "DLDefenseSuccessRatePvPConstB", 0);

        cfg.DLDefenseSuccessRatePvPConstC = ini.GetInt(Section, "DLDefenseSuccessRatePvPConstC", 0);

        cfg.DWDefenseConstA = ini.GetInt(Section, "DWDefenseConstA", 0);

        cfg.DKDefenseConstA = ini.GetInt(Section, "DKDefenseConstA", 0);

        cfg.FEDefenseConstA = ini.GetInt(Section, "FEDefenseConstA", 0);

        cfg.MGDefenseConstA = ini.GetInt(Section, "MGDefenseConstA", 0);

        cfg.DLDefenseConstA = ini.GetInt(Section, "DLDefenseConstA", 0);

        return cfg;
    }
}
