using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de cobertura COMPLETA de <c>GameServerInfo - ChaosMix.dat</c> (parte de <c>CServerInfo</c>,
/// ServerInfo.h/.cpp del árbol correcto -- ver doc-comment de <see cref="ServerInfoConfig"/> para la
/// explicación completa de por qué ese es el árbol correcto). A diferencia de las clases "curadas"
/// existentes (<see cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, que solo cargan
/// los campos con un consumidor real ya portado), esta clase carga TODOS los 19 campos de
/// este archivo (100 claves de .ini, algunos son arrays por AccountLevel 0-3 o por clase
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
public sealed class GameServerInfoChaosMix
{
    private const string Section = "GameServerInfo";

    public int[] ChaosItemMixRate { get; } = new int[4];
    public int[] DevilSquareMixRate1 { get; } = new int[4];
    public int[] DevilSquareMixRate2 { get; } = new int[4];
    public int[] DevilSquareMixRate3 { get; } = new int[4];
    public int[] DevilSquareMixRate4 { get; } = new int[4];
    public int[,] PlusCommonItemLevelMixRate { get; } = new int[4, 4];
    public int[,] PlusExcSetItemLevelMixRate { get; } = new int[4, 4];
    public int[] DinorantMixRate { get; } = new int[4];
    public int[] FruitMixRate { get; } = new int[4];
    public int[] Wing2MixRate { get; } = new int[4];
    public int[] BloodCastleMixRate1 { get; } = new int[4];
    public int[] BloodCastleMixRate2 { get; } = new int[4];
    public int[] BloodCastleMixRate3 { get; } = new int[4];
    public int[] BloodCastleMixRate4 { get; } = new int[4];
    public int[] BloodCastleMixRate5 { get; } = new int[4];
    public int[] BloodCastleMixRate6 { get; } = new int[4];
    public int[] BloodCastleMixRate7 { get; } = new int[4];
    public int[] Wing1MixRate { get; } = new int[4];
    public int[] PetMixRate { get; } = new int[4];

    public static GameServerInfoChaosMix Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoChaosMix();

        cfg.ChaosItemMixRate[0] = ini.GetInt(Section, "ChaosItemMixRate_AL0", 0);
        cfg.ChaosItemMixRate[1] = ini.GetInt(Section, "ChaosItemMixRate_AL1", 0);
        cfg.ChaosItemMixRate[2] = ini.GetInt(Section, "ChaosItemMixRate_AL2", 0);
        cfg.ChaosItemMixRate[3] = ini.GetInt(Section, "ChaosItemMixRate_AL3", 0);

        cfg.DevilSquareMixRate1[0] = ini.GetInt(Section, "DevilSquareMixRate1_AL0", 0);
        cfg.DevilSquareMixRate1[1] = ini.GetInt(Section, "DevilSquareMixRate1_AL1", 0);
        cfg.DevilSquareMixRate1[2] = ini.GetInt(Section, "DevilSquareMixRate1_AL2", 0);
        cfg.DevilSquareMixRate1[3] = ini.GetInt(Section, "DevilSquareMixRate1_AL3", 0);

        cfg.DevilSquareMixRate2[0] = ini.GetInt(Section, "DevilSquareMixRate2_AL0", 0);
        cfg.DevilSquareMixRate2[1] = ini.GetInt(Section, "DevilSquareMixRate2_AL1", 0);
        cfg.DevilSquareMixRate2[2] = ini.GetInt(Section, "DevilSquareMixRate2_AL2", 0);
        cfg.DevilSquareMixRate2[3] = ini.GetInt(Section, "DevilSquareMixRate2_AL3", 0);

        cfg.DevilSquareMixRate3[0] = ini.GetInt(Section, "DevilSquareMixRate3_AL0", 0);
        cfg.DevilSquareMixRate3[1] = ini.GetInt(Section, "DevilSquareMixRate3_AL1", 0);
        cfg.DevilSquareMixRate3[2] = ini.GetInt(Section, "DevilSquareMixRate3_AL2", 0);
        cfg.DevilSquareMixRate3[3] = ini.GetInt(Section, "DevilSquareMixRate3_AL3", 0);

        cfg.DevilSquareMixRate4[0] = ini.GetInt(Section, "DevilSquareMixRate4_AL0", 0);
        cfg.DevilSquareMixRate4[1] = ini.GetInt(Section, "DevilSquareMixRate4_AL1", 0);
        cfg.DevilSquareMixRate4[2] = ini.GetInt(Section, "DevilSquareMixRate4_AL2", 0);
        cfg.DevilSquareMixRate4[3] = ini.GetInt(Section, "DevilSquareMixRate4_AL3", 0);

        cfg.PlusCommonItemLevelMixRate[0, 0] = ini.GetInt(Section, "PlusCommonItemLevelMixRate1_AL0", 0);
        cfg.PlusCommonItemLevelMixRate[0, 1] = ini.GetInt(Section, "PlusCommonItemLevelMixRate1_AL1", 0);
        cfg.PlusCommonItemLevelMixRate[0, 2] = ini.GetInt(Section, "PlusCommonItemLevelMixRate1_AL2", 0);
        cfg.PlusCommonItemLevelMixRate[0, 3] = ini.GetInt(Section, "PlusCommonItemLevelMixRate1_AL3", 0);
        cfg.PlusCommonItemLevelMixRate[1, 0] = ini.GetInt(Section, "PlusCommonItemLevelMixRate2_AL0", 0);
        cfg.PlusCommonItemLevelMixRate[1, 1] = ini.GetInt(Section, "PlusCommonItemLevelMixRate2_AL1", 0);
        cfg.PlusCommonItemLevelMixRate[1, 2] = ini.GetInt(Section, "PlusCommonItemLevelMixRate2_AL2", 0);
        cfg.PlusCommonItemLevelMixRate[1, 3] = ini.GetInt(Section, "PlusCommonItemLevelMixRate2_AL3", 0);
        cfg.PlusCommonItemLevelMixRate[2, 0] = ini.GetInt(Section, "PlusCommonItemLevelMixRate3_AL0", 0);
        cfg.PlusCommonItemLevelMixRate[2, 1] = ini.GetInt(Section, "PlusCommonItemLevelMixRate3_AL1", 0);
        cfg.PlusCommonItemLevelMixRate[2, 2] = ini.GetInt(Section, "PlusCommonItemLevelMixRate3_AL2", 0);
        cfg.PlusCommonItemLevelMixRate[2, 3] = ini.GetInt(Section, "PlusCommonItemLevelMixRate3_AL3", 0);
        cfg.PlusCommonItemLevelMixRate[3, 0] = ini.GetInt(Section, "PlusCommonItemLevelMixRate4_AL0", 0);
        cfg.PlusCommonItemLevelMixRate[3, 1] = ini.GetInt(Section, "PlusCommonItemLevelMixRate4_AL1", 0);
        cfg.PlusCommonItemLevelMixRate[3, 2] = ini.GetInt(Section, "PlusCommonItemLevelMixRate4_AL2", 0);
        cfg.PlusCommonItemLevelMixRate[3, 3] = ini.GetInt(Section, "PlusCommonItemLevelMixRate4_AL3", 0);

        cfg.PlusExcSetItemLevelMixRate[0, 0] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate1_AL0", 0);
        cfg.PlusExcSetItemLevelMixRate[0, 1] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate1_AL1", 0);
        cfg.PlusExcSetItemLevelMixRate[0, 2] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate1_AL2", 0);
        cfg.PlusExcSetItemLevelMixRate[0, 3] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate1_AL3", 0);
        cfg.PlusExcSetItemLevelMixRate[1, 0] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate2_AL0", 0);
        cfg.PlusExcSetItemLevelMixRate[1, 1] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate2_AL1", 0);
        cfg.PlusExcSetItemLevelMixRate[1, 2] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate2_AL2", 0);
        cfg.PlusExcSetItemLevelMixRate[1, 3] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate2_AL3", 0);
        cfg.PlusExcSetItemLevelMixRate[2, 0] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate3_AL0", 0);
        cfg.PlusExcSetItemLevelMixRate[2, 1] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate3_AL1", 0);
        cfg.PlusExcSetItemLevelMixRate[2, 2] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate3_AL2", 0);
        cfg.PlusExcSetItemLevelMixRate[2, 3] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate3_AL3", 0);
        cfg.PlusExcSetItemLevelMixRate[3, 0] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate4_AL0", 0);
        cfg.PlusExcSetItemLevelMixRate[3, 1] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate4_AL1", 0);
        cfg.PlusExcSetItemLevelMixRate[3, 2] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate4_AL2", 0);
        cfg.PlusExcSetItemLevelMixRate[3, 3] = ini.GetInt(Section, "PlusExcSetItemLevelMixRate4_AL3", 0);

        cfg.DinorantMixRate[0] = ini.GetInt(Section, "DinorantMixRate_AL0", 0);
        cfg.DinorantMixRate[1] = ini.GetInt(Section, "DinorantMixRate_AL1", 0);
        cfg.DinorantMixRate[2] = ini.GetInt(Section, "DinorantMixRate_AL2", 0);
        cfg.DinorantMixRate[3] = ini.GetInt(Section, "DinorantMixRate_AL3", 0);

        cfg.FruitMixRate[0] = ini.GetInt(Section, "FruitMixRate_AL0", 0);
        cfg.FruitMixRate[1] = ini.GetInt(Section, "FruitMixRate_AL1", 0);
        cfg.FruitMixRate[2] = ini.GetInt(Section, "FruitMixRate_AL2", 0);
        cfg.FruitMixRate[3] = ini.GetInt(Section, "FruitMixRate_AL3", 0);

        cfg.Wing2MixRate[0] = ini.GetInt(Section, "Wing2MixRate_AL0", 0);
        cfg.Wing2MixRate[1] = ini.GetInt(Section, "Wing2MixRate_AL1", 0);
        cfg.Wing2MixRate[2] = ini.GetInt(Section, "Wing2MixRate_AL2", 0);
        cfg.Wing2MixRate[3] = ini.GetInt(Section, "Wing2MixRate_AL3", 0);

        cfg.BloodCastleMixRate1[0] = ini.GetInt(Section, "BloodCastleMixRate1_AL0", 0);
        cfg.BloodCastleMixRate1[1] = ini.GetInt(Section, "BloodCastleMixRate1_AL1", 0);
        cfg.BloodCastleMixRate1[2] = ini.GetInt(Section, "BloodCastleMixRate1_AL2", 0);
        cfg.BloodCastleMixRate1[3] = ini.GetInt(Section, "BloodCastleMixRate1_AL3", 0);

        cfg.BloodCastleMixRate2[0] = ini.GetInt(Section, "BloodCastleMixRate2_AL0", 0);
        cfg.BloodCastleMixRate2[1] = ini.GetInt(Section, "BloodCastleMixRate2_AL1", 0);
        cfg.BloodCastleMixRate2[2] = ini.GetInt(Section, "BloodCastleMixRate2_AL2", 0);
        cfg.BloodCastleMixRate2[3] = ini.GetInt(Section, "BloodCastleMixRate2_AL3", 0);

        cfg.BloodCastleMixRate3[0] = ini.GetInt(Section, "BloodCastleMixRate3_AL0", 0);
        cfg.BloodCastleMixRate3[1] = ini.GetInt(Section, "BloodCastleMixRate3_AL1", 0);
        cfg.BloodCastleMixRate3[2] = ini.GetInt(Section, "BloodCastleMixRate3_AL2", 0);
        cfg.BloodCastleMixRate3[3] = ini.GetInt(Section, "BloodCastleMixRate3_AL3", 0);

        cfg.BloodCastleMixRate4[0] = ini.GetInt(Section, "BloodCastleMixRate4_AL0", 0);
        cfg.BloodCastleMixRate4[1] = ini.GetInt(Section, "BloodCastleMixRate4_AL1", 0);
        cfg.BloodCastleMixRate4[2] = ini.GetInt(Section, "BloodCastleMixRate4_AL2", 0);
        cfg.BloodCastleMixRate4[3] = ini.GetInt(Section, "BloodCastleMixRate4_AL3", 0);

        cfg.BloodCastleMixRate5[0] = ini.GetInt(Section, "BloodCastleMixRate5_AL0", 0);
        cfg.BloodCastleMixRate5[1] = ini.GetInt(Section, "BloodCastleMixRate5_AL1", 0);
        cfg.BloodCastleMixRate5[2] = ini.GetInt(Section, "BloodCastleMixRate5_AL2", 0);
        cfg.BloodCastleMixRate5[3] = ini.GetInt(Section, "BloodCastleMixRate5_AL3", 0);

        cfg.BloodCastleMixRate6[0] = ini.GetInt(Section, "BloodCastleMixRate6_AL0", 0);
        cfg.BloodCastleMixRate6[1] = ini.GetInt(Section, "BloodCastleMixRate6_AL1", 0);
        cfg.BloodCastleMixRate6[2] = ini.GetInt(Section, "BloodCastleMixRate6_AL2", 0);
        cfg.BloodCastleMixRate6[3] = ini.GetInt(Section, "BloodCastleMixRate6_AL3", 0);

        cfg.BloodCastleMixRate7[0] = ini.GetInt(Section, "BloodCastleMixRate7_AL0", 0);
        cfg.BloodCastleMixRate7[1] = ini.GetInt(Section, "BloodCastleMixRate7_AL1", 0);
        cfg.BloodCastleMixRate7[2] = ini.GetInt(Section, "BloodCastleMixRate7_AL2", 0);
        cfg.BloodCastleMixRate7[3] = ini.GetInt(Section, "BloodCastleMixRate7_AL3", 0);

        cfg.Wing1MixRate[0] = ini.GetInt(Section, "Wing1MixRate_AL0", 0);
        cfg.Wing1MixRate[1] = ini.GetInt(Section, "Wing1MixRate_AL1", 0);
        cfg.Wing1MixRate[2] = ini.GetInt(Section, "Wing1MixRate_AL2", 0);
        cfg.Wing1MixRate[3] = ini.GetInt(Section, "Wing1MixRate_AL3", 0);

        cfg.PetMixRate[0] = ini.GetInt(Section, "PetMixRate_AL0", 0);
        cfg.PetMixRate[1] = ini.GetInt(Section, "PetMixRate_AL1", 0);
        cfg.PetMixRate[2] = ini.GetInt(Section, "PetMixRate_AL2", 0);
        cfg.PetMixRate[3] = ini.GetInt(Section, "PetMixRate_AL3", 0);

        return cfg;
    }
}
