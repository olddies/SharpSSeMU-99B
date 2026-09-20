using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary> Port with FULL coverage of <c>GameServerInfo - Custom.dat</c> -- unlike the other 6 files (part of
/// <c>CServerInfo</c>), this one is read by 3 classes of SSeMU's own: <c>CCustomArena::ReadCustomArenaInfo</c>
/// (CustomArena.cpp:63-70), <c>CCustomAttack:: ReadCustomAttackInfo</c> (CustomAttack.cpp) and
/// <c>CCustomPick::ReadCustomPickInfo</c> (CustomPick.cpp), all 3 pointing at the same physical file. See the
/// doc-comment of <see cref="GameServerInfoCommon"/> for the general explanation of this full-coverage layer
/// (33 fields here, all with a real default of 0 in <c>GetPrivateProfileInt</c> -- the factory values live in
/// the shipped .dat, not in the code). None of the 3 systems (Custom Arena/Attack/Pick) is ported yet -- this
/// class is only the data layer. </summary>
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
