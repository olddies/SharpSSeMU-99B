using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary> Port of the balance constants of "GameServerInfo - Character.dat" that
/// CObjectManager::CharacterCalcAttribute (ObjectManager.cpp:1887-2523) uses to compute damage/hit/ defense per
/// class -- Phase 4 (second pass, real combat balance). The original .dat is a flat INI read with
/// GetPrivateProfileInt (default 0 if the key is missing); here <see cref="IniFile"/> is reused with the same
/// defaults the real file carries (see MuServer99B/GameServer/DATA/GameServerInfo - Character.dat), so a
/// deployment without the file copied still works with the factory balance. Classes indexed 0-4 =
/// DW,DK,FE,MG,DL (same order/raw values as PlayerObject.Class -- see ClientProtocolHandler.ClassFe=2). Only
/// the constants needed for physical damage/ hit/melee defense are loaded (this pass does not touch
/// magic/skills/speed, see the comment of PlayerObject.RecalcCombatStats). </summary>
public sealed class CharacterBalanceConfig
{
    private static readonly string[] Prefixes = { "DW", "DK", "FE", "MG", "DL" };

    public readonly int[] PhysiDamageMinConstA = new int[5];
    public readonly int[] PhysiDamageMinConstB = new int[5]; // solo MG(3)/DL(4) -- el resto queda en 0, sin usar
    public readonly int[] PhysiDamageMaxConstA = new int[5];
    public readonly int[] PhysiDamageMaxConstB = new int[5];

    // Base magic damage (the "skills" phase) -- identical for the 5 classes in the real .dat (9,4), but loaded
    // per class anyway (not hardcoded) in case a deployment customises them.
    public readonly int[] MagicDamageMinConstA = new int[5];
    public readonly int[] MagicDamageMaxConstA = new int[5];

    // Mana/BP regeneration (CharacterAutoRecuperation, ObjectManager.cpp:1729-1758) -- percentage of
    // MaxMana/MaxBP recovered per regeneration tick (see World/ViewportTicker.cs).
    public readonly int[] MpRecoveryRate = new int[5];
    public readonly int[] BpRecoveryRate = new int[5];

    // FE with bow (ObjectManager.cpp:1999-2012) -- alternative formula, not a per-class constant.
    public int FePhysiDamageMinBowConstA;
    public int FePhysiDamageMinBowConstB;
    public int FePhysiDamageMaxBowConstA;
    public int FePhysiDamageMaxBowConstB;

    public readonly int[] AttackSuccessRateConstA = new int[5];
    public readonly int[] AttackSuccessRateConstB = new int[5];
    public readonly int[] AttackSuccessRateConstC = new int[5];
    public readonly int[] AttackSuccessRateConstD = new int[5];
    public int DlAttackSuccessRateConstE; // solo DL -- bono de Leadership

    public readonly int[] DefenseSuccessRateConstA = new int[5];
    public readonly int[] DefenseConstA = new int[5];

    public int DkDamageMultiplierConstA;
    public int DlDamageMultiplierConstA;
    public int DkDamageMultiplierMaxRate;
    public int DlDamageMultiplierMaxRate;

    // Stat-point cap, indexed by AccountLevel 0-3 (CharacterLevelUpPointAdd, ObjectManager.cpp:1078) -- it
    // lives in "GameServerInfo - Common.dat" in the original (key MaxStatPoint_AL0..AL3), not in
    // "Character.dat" like the rest of this class. Indexed with PlayerObject.AccountLevel. FIXED: `Load` read
    // this key from the same `ini` as the rest of the file (Character.dat, via `config.CharacterInfoPath`), but
    // the real key lives in Common.dat -- so the lookup never found anything and always fell back to the
    // default (65000) for all 4 bands, regardless of what the admin set in the panel (Rates/Configuration page,
    // which does write Common.dat). It was not noticeable because the shipped factory value is also 65000 for
    // all 4 -- but an admin who changed the cap per account level to give VIP accounts more room saw no effect.
    public readonly int[] MaxStatPoint = new int[4];

    public static CharacterBalanceConfig Load(string path, string commonPath)
    {
        var ini = IniFile.Load(path);
        var cfg = new CharacterBalanceConfig();

        // Defaults = the real factory values (the shipped GameServerInfo - Character.dat), so that a deployment
        // without the .dat copied has the same balance as the original server.
        var minA = new[] { 6, 8, 7, 6, 7 };
        var maxA = new[] { 4, 4, 4, 4, 5 };
        var asrA = new[] { 5, 5, 5, 5, 5 };
        var asrB = new[] { 3, 3, 3, 3, 6 };
        var asrC = new[] { 2, 2, 2, 2, 2 };
        var asrD = new[] { 4, 4, 4, 4, 4 };
        var dsrA = new[] { 3, 3, 4, 3, 7 };
        var defA = new[] { 4, 3, 10, 4, 7 };

        for (int c = 0; c < 5; c++)
        {
            var p = Prefixes[c];
            cfg.PhysiDamageMinConstA[c] = ini.GetInt("GameServerInfo", $"{p}PhysiDamageMinConstA", minA[c]);
            cfg.PhysiDamageMaxConstA[c] = ini.GetInt("GameServerInfo", $"{p}PhysiDamageMaxConstA", maxA[c]);
            cfg.AttackSuccessRateConstA[c] = ini.GetInt("GameServerInfo", $"{p}AttackSuccessRateConstA", asrA[c]);
            cfg.AttackSuccessRateConstB[c] = ini.GetInt("GameServerInfo", $"{p}AttackSuccessRateConstB", asrB[c]);
            cfg.AttackSuccessRateConstC[c] = ini.GetInt("GameServerInfo", $"{p}AttackSuccessRateConstC", asrC[c]);
            cfg.AttackSuccessRateConstD[c] = ini.GetInt("GameServerInfo", $"{p}AttackSuccessRateConstD", asrD[c]);
            cfg.DefenseSuccessRateConstA[c] = ini.GetInt("GameServerInfo", $"{p}DefenseSuccessRateConstA", dsrA[c]);
            cfg.DefenseConstA[c] = ini.GetInt("GameServerInfo", $"{p}DefenseConstA", defA[c]);
            cfg.MagicDamageMinConstA[c] = ini.GetInt("GameServerInfo", $"{p}MagicDamageMinConstA", 9);
            cfg.MagicDamageMaxConstA[c] = ini.GetInt("GameServerInfo", $"{p}MagicDamageMaxConstA", 4);
            cfg.MpRecoveryRate[c] = ini.GetInt("GameServerInfo", $"{p}MPRecoveryRate", 4);
        }

        cfg.BpRecoveryRate[0] = ini.GetInt("GameServerInfo", "DWBPRecoveryRate", 3);
        cfg.BpRecoveryRate[1] = ini.GetInt("GameServerInfo", "DKBPRecoveryRate", 5);
        cfg.BpRecoveryRate[2] = ini.GetInt("GameServerInfo", "FEBPRecoveryRate", 3);
        cfg.BpRecoveryRate[3] = ini.GetInt("GameServerInfo", "MGBPRecoveryRate", 3);
        cfg.BpRecoveryRate[4] = ini.GetInt("GameServerInfo", "DLBPRecoveryRate", 3);

        cfg.PhysiDamageMinConstB[3] = ini.GetInt("GameServerInfo", "MGPhysiDamageMinConstB", 12);
        cfg.PhysiDamageMaxConstB[3] = ini.GetInt("GameServerInfo", "MGPhysiDamageMaxConstB", 8);
        cfg.PhysiDamageMinConstB[4] = ini.GetInt("GameServerInfo", "DLPhysiDamageMinConstB", 14);
        cfg.PhysiDamageMaxConstB[4] = ini.GetInt("GameServerInfo", "DLPhysiDamageMaxConstB", 10);

        cfg.FePhysiDamageMinBowConstA = ini.GetInt("GameServerInfo", "FEPhysiDamageMinBowConstA", 14);
        cfg.FePhysiDamageMinBowConstB = ini.GetInt("GameServerInfo", "FEPhysiDamageMinBowConstB", 7);
        cfg.FePhysiDamageMaxBowConstA = ini.GetInt("GameServerInfo", "FEPhysiDamageMaxBowConstA", 8);
        cfg.FePhysiDamageMaxBowConstB = ini.GetInt("GameServerInfo", "FEPhysiDamageMaxBowConstB", 4);

        cfg.DlAttackSuccessRateConstE = ini.GetInt("GameServerInfo", "DLAttackSuccessRateConstE", 10);

        cfg.DkDamageMultiplierConstA = ini.GetInt("GameServerInfo", "DKDamageMultiplierConstA", 10);
        cfg.DlDamageMultiplierConstA = ini.GetInt("GameServerInfo", "DLDamageMultiplierConstA", 20);
        cfg.DkDamageMultiplierMaxRate = ini.GetInt("GameServerInfo", "DKDamageMultiplierMaxRate", 500);
        cfg.DlDamageMultiplierMaxRate = ini.GetInt("GameServerInfo", "DLDamageMultiplierMaxRate", 500);

        var common = IniFile.Load(commonPath);
        for (int al = 0; al < 4; al++)
        {
            cfg.MaxStatPoint[al] = common.GetInt("GameServerInfo", $"MaxStatPoint_AL{al}", 65000);
        }

        return cfg;
    }
}
