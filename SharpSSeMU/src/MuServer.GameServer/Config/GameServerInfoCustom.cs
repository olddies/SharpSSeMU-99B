using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de cobertura COMPLETA de <c>GameServerInfo - Custom.dat</c> -- a diferencia de los otros 6
/// archivos (parte de <c>CServerInfo</c>), este lo leen 3 clases propias de SSeMU:
/// <c>CCustomArena::ReadCustomArenaInfo</c> (CustomArena.cpp:63-70), <c>CCustomAttack::
/// ReadCustomAttackInfo</c> (CustomAttack.cpp) y <c>CCustomPick::ReadCustomPickInfo</c>
/// (CustomPick.cpp), las 3 apuntando al mismo archivo físico. Ver doc-comment de
/// <see cref="GameServerInfoCommon"/> para la explicación general de esta capa de cobertura completa
/// (33 campos acá, todos con default real 0 en <c>GetPrivateProfileInt</c> -- los valores de fábrica
/// viven en el .dat shippeado, no en el código). Ninguno de los 3 sistemas (Custom Arena/Attack/Pick)
/// está portado todavía -- esta clase es solo la capa de datos.
/// </summary>
public sealed class GameServerInfoCustom
{
    private const string Section = "GameServerInfo";

    // ---- CCustomArena::ReadCustomArenaInfo ----
    public int CustomArenaSwitch { get; private set; }
    public int CustomArenaMapNumber { get; private set; }
    public int CustomArenaVictimScoreDecrease { get; private set; }
    public int CustomArenaKillerScoreIncrease { get; private set; }

    // ---- CCustomAttack::ReadCustomAttackInfo ----
    public int CustomAttackSwitch { get; private set; }
    public int CustomAttackMapZone { get; private set; }
    public int[] CustomAttackBuffEnable { get; } = new int[4];
    public int[] CustomAttackMaxTimeLimit { get; } = new int[4];
    public int CustomAttackOfflineSwitch { get; private set; }
    public int[] CustomAttackOfflineBuffEnable { get; } = new int[4];
    public int[] CustomAttackOfflineKeepEnable { get; } = new int[4];
    public int[] CustomAttackOfflineMaxTimeLimit { get; } = new int[4];

    // ---- CCustomPick::ReadCustomPickInfo ----
    public int CustomPickSwitch { get; private set; }
    public int CustomPickMapZone { get; private set; }
    public int[] CustomPickMaxTime { get; } = new int[4];

    public static GameServerInfoCustom Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoCustom
        {
            CustomArenaSwitch = ini.GetInt(Section, "CustomArenaSwitch", 0),
            CustomArenaMapNumber = ini.GetInt(Section, "CustomArenaMapNumber", 0),
            CustomArenaVictimScoreDecrease = ini.GetInt(Section, "CustomArenaVictimScoreDecrease", 0),
            CustomArenaKillerScoreIncrease = ini.GetInt(Section, "CustomArenaKillerScoreIncrease", 0),

            CustomAttackSwitch = ini.GetInt(Section, "CustomAttackSwitch", 0),
            CustomAttackMapZone = ini.GetInt(Section, "CustomAttackMapZone", 0),
            CustomAttackOfflineSwitch = ini.GetInt(Section, "CustomAttackOfflineSwitch", 0),

            CustomPickSwitch = ini.GetInt(Section, "CustomPickSwitch", 0),
            CustomPickMapZone = ini.GetInt(Section, "CustomPickMapZone", 0),
        };

        for (int al = 0; al < 4; al++)
        {
            cfg.CustomAttackBuffEnable[al] = ini.GetInt(Section, $"CustomAttackBuffEnable_AL{al}", 0);
            cfg.CustomAttackMaxTimeLimit[al] = ini.GetInt(Section, $"CustomAttackMaxTimeLimit_AL{al}", 0);
            cfg.CustomAttackOfflineBuffEnable[al] = ini.GetInt(Section, $"CustomAttackOfflineBuffEnable_AL{al}", 0);
            cfg.CustomAttackOfflineKeepEnable[al] = ini.GetInt(Section, $"CustomAttackOfflineKeepEnable_AL{al}", 0);
            cfg.CustomAttackOfflineMaxTimeLimit[al] = ini.GetInt(Section, $"CustomAttackOfflineMaxTimeLimit_AL{al}", 0);
            cfg.CustomPickMaxTime[al] = ini.GetInt(Section, $"CustomPickMaxTime_AL{al}", 0);
        }

        return cfg;
    }
}
