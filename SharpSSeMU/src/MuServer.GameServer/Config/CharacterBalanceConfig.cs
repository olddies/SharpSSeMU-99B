using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de las constantes de balance de "GameServerInfo - Character.dat" que usa
/// CObjectManager::CharacterCalcAttribute (ObjectManager.cpp:1887-2523) para calcular daño/acierto/
/// defensa por clase -- Fase 4 (segunda pasada, balance real de combate). El .dat original es un INI
/// plano leído con GetPrivateProfileInt (default 0 si falta la clave); acá se reusa <see cref="IniFile"/>
/// con los mismos defaults que trae el archivo real (ver MuServer99B/GameServer/DATA/GameServerInfo -
/// Character.dat), así que un despliegue sin el archivo copiado igual anda con el balance de fábrica.
///
/// Clases indexadas 0-4 = DW,DK,FE,MG,DL (mismo orden/valores crudos que PlayerObject.Class -- ver
/// ClientProtocolHandler.ClassFe=2). Solo se cargan las constantes que hacen falta para daño físico/
/// acierto/defensa cuerpo a cuerpo (esta pasada no toca magia/skills/velocidad, ver comentario de
/// PlayerObject.RecalcCombatStats).
/// </summary>
public sealed class CharacterBalanceConfig
{
    private static readonly string[] Prefixes = { "DW", "DK", "FE", "MG", "DL" };

    public readonly int[] PhysiDamageMinConstA = new int[5];
    public readonly int[] PhysiDamageMinConstB = new int[5]; // solo MG(3)/DL(4) -- el resto queda en 0, sin usar
    public readonly int[] PhysiDamageMaxConstA = new int[5];
    public readonly int[] PhysiDamageMaxConstB = new int[5];

    // Daño mágico base (Fase "skills") -- idénticos para las 5 clases en el .dat real (9,4), pero se
    // cargan por clase igual (no hardcodeados) por si un despliegue los customiza.
    public readonly int[] MagicDamageMinConstA = new int[5];
    public readonly int[] MagicDamageMaxConstA = new int[5];

    // Regeneración de maná/BP (CharacterAutoRecuperation, ObjectManager.cpp:1729-1758) -- porcentaje
    // de MaxMana/MaxBP que se recupera por tick de regeneración (ver World/ViewportTicker.cs).
    public readonly int[] MpRecoveryRate = new int[5];
    public readonly int[] BpRecoveryRate = new int[5];

    // FE con arco (ObjectManager.cpp:1999-2012) -- fórmula alternativa, no una constante por clase.
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

    // Tope de puntos por stat, indexado por AccountLevel 0-3 (CharacterLevelUpPointAdd,
    // ObjectManager.cpp:1078) -- vive en "GameServerInfo - Common.dat" en el original (clave
    // MaxStatPoint_AL0..AL3), no en "Character.dat" como el resto de esta clase. Indexado con
    // PlayerObject.AccountLevel.
    //
    // CORREGIDO: `Load` leía esta clave del mismo `ini` que el resto del archivo (Character.dat, vía
    // `config.CharacterInfoPath`), pero la clave real vive en Common.dat -- así que la búsqueda nunca
    // encontraba nada y siempre caía al default (65000) para las 4 franjas, sin importar lo que el
    // admin pusiera en el panel (página Tasas/Configuración, que sí escribe Common.dat). No se notaba
    // porque el valor de fábrica shippeado también es 65000 para las 4 -- pero un admin que cambiara
    // el tope por nivel de cuenta para dar más margen a las cuentas VIP no veía ningún efecto.
    public readonly int[] MaxStatPoint = new int[4];

    public static CharacterBalanceConfig Load(string path, string commonPath)
    {
        var ini = IniFile.Load(path);
        var cfg = new CharacterBalanceConfig();

        // Defaults = valores reales de fábrica (GameServerInfo - Character.dat shippeado), para que
        // un despliegue sin el .dat copiado tenga el mismo balance que el servidor original.
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
