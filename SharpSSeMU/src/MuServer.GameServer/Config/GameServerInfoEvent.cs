using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de cobertura COMPLETA de <c>GameServerInfo - Event.dat</c> (parte de <c>CServerInfo</c>,
/// ServerInfo.h/.cpp del árbol correcto -- ver doc-comment de <see cref="ServerInfoConfig"/> para la
/// explicación completa de por qué ese es el árbol correcto). A diferencia de las clases "curadas"
/// existentes (<see cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, que solo cargan
/// los campos con un consumidor real ya portado), esta clase carga TODOS los 16 campos de
/// este archivo (25 claves de .ini, algunos son arrays por AccountLevel 0-3 o por clase
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
