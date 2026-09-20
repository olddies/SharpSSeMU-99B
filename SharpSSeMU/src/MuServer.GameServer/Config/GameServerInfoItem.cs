using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de cobertura COMPLETA de <c>GameServerInfo - Item.dat</c> (parte de <c>CServerInfo</c>,
/// ServerInfo.h/.cpp del árbol correcto -- ver doc-comment de <see cref="ServerInfoConfig"/> para la
/// explicación completa de por qué ese es el árbol correcto). A diferencia de las clases "curadas"
/// existentes (<see cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, que solo cargan
/// los campos con un consumidor real ya portado), esta clase carga TODOS los 26 campos de
/// este archivo (54 claves de .ini, algunos son arrays por AccountLevel 0-3 o por clase
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
