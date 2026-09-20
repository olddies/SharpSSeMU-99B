using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de cobertura COMPLETA de <c>GameServerInfo - Skill.dat</c> (parte de <c>CServerInfo</c>,
/// ServerInfo.h/.cpp del árbol correcto -- ver doc-comment de <see cref="ServerInfoConfig"/> para la
/// explicación completa de por qué ese es el árbol correcto). A diferencia de las clases "curadas"
/// existentes (<see cref="ServerInfoConfig"/>, <see cref="CharacterBalanceConfig"/>, que solo cargan
/// los campos con un consumidor real ya portado), esta clase carga TODOS los 42 campos de
/// este archivo (61 claves de .ini, algunos son arrays por AccountLevel 0-3 o por clase
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
public sealed class GameServerInfoSkill
{
    private const string Section = "GameServerInfo";

    public int ManaShieldConstA { get; private set; }
    public int ManaShieldConstB { get; private set; }
    public int ManaShieldConstC { get; private set; }
    public int[] ManaShieldRate { get; } = new int[5];
    public int ManaShieldTimeConstA { get; private set; }
    public int ManaShieldTimeConstB { get; private set; }
    public int ManaShieldMaxRate { get; private set; }
    public int HealConstA { get; private set; }
    public int HealConstB { get; private set; }
    public int GreaterDefenseConstA { get; private set; }
    public int GreaterDefenseConstB { get; private set; }
    public int[] GreaterDefenseRate { get; } = new int[5];
    public int GreaterDefenseTimeConstA { get; private set; }
    public int GreaterDamageConstA { get; private set; }
    public int GreaterDamageConstB { get; private set; }
    public int[] GreaterDamageRate { get; } = new int[5];
    public int GreaterDamageTimeConstA { get; private set; }
    public int SummonMonster1 { get; private set; }
    public int SummonMonster2 { get; private set; }
    public int SummonMonster3 { get; private set; }
    public int SummonMonster4 { get; private set; }
    public int SummonMonster5 { get; private set; }
    public int SummonMonster6 { get; private set; }
    public int SummonMonster7 { get; private set; }
    public int GreaterLifeConstA { get; private set; }
    public int GreaterLifeConstB { get; private set; }
    public int GreaterLifeConstC { get; private set; }
    public int[] GreaterLifeRate { get; } = new int[5];
    public int GreaterLifeTimeConstA { get; private set; }
    public int GreaterLifeTimeConstB { get; private set; }
    public int GreaterLifeMaxRate { get; private set; }
    public int FireSlashConstA { get; private set; }
    public int FireSlashConstB { get; private set; }
    public int FireSlashTimeConstA { get; private set; }
    public int FireSlashMaxRate { get; private set; }
    public int GreaterCriticalDamageConstA { get; private set; }
    public int GreaterCriticalDamageConstB { get; private set; }
    public int GreaterCriticalDamageTimeConstA { get; private set; }
    public int GreaterCriticalDamageTimeConstB { get; private set; }
    public int[] InfinityArrowSwitch { get; } = new int[4];
    public int MagicDamageImmunityTimeConstA { get; private set; }
    public int PhysiDamageImmunityTimeConstA { get; private set; }

    public static GameServerInfoSkill Load(string path)
    {
        var ini = IniFile.Load(path);
        var cfg = new GameServerInfoSkill();

        cfg.ManaShieldConstA = ini.GetInt(Section, "ManaShieldConstA", 0);

        cfg.ManaShieldConstB = ini.GetInt(Section, "ManaShieldConstB", 0);

        cfg.ManaShieldConstC = ini.GetInt(Section, "ManaShieldConstC", 0);

        cfg.ManaShieldRate[0] = ini.GetInt(Section, "ManaShieldRateDW", 0);
        cfg.ManaShieldRate[1] = ini.GetInt(Section, "ManaShieldRateDK", 0);
        cfg.ManaShieldRate[2] = ini.GetInt(Section, "ManaShieldRateFE", 0);
        cfg.ManaShieldRate[3] = ini.GetInt(Section, "ManaShieldRateMG", 0);
        cfg.ManaShieldRate[4] = ini.GetInt(Section, "ManaShieldRateDL", 0);

        cfg.ManaShieldTimeConstA = ini.GetInt(Section, "ManaShieldTimeConstA", 0);

        cfg.ManaShieldTimeConstB = ini.GetInt(Section, "ManaShieldTimeConstB", 0);

        cfg.ManaShieldMaxRate = ini.GetInt(Section, "ManaShieldMaxRate", 0);

        cfg.HealConstA = ini.GetInt(Section, "HealConstA", 0);

        cfg.HealConstB = ini.GetInt(Section, "HealConstB", 0);

        cfg.GreaterDefenseConstA = ini.GetInt(Section, "GreaterDefenseConstA", 0);

        cfg.GreaterDefenseConstB = ini.GetInt(Section, "GreaterDefenseConstB", 0);

        cfg.GreaterDefenseRate[0] = ini.GetInt(Section, "GreaterDefenseRateDW", 0);
        cfg.GreaterDefenseRate[1] = ini.GetInt(Section, "GreaterDefenseRateDK", 0);
        cfg.GreaterDefenseRate[2] = ini.GetInt(Section, "GreaterDefenseRateFE", 0);
        cfg.GreaterDefenseRate[3] = ini.GetInt(Section, "GreaterDefenseRateMG", 0);
        cfg.GreaterDefenseRate[4] = ini.GetInt(Section, "GreaterDefenseRateDL", 0);

        cfg.GreaterDefenseTimeConstA = ini.GetInt(Section, "GreaterDefenseTimeConstA", 0);

        cfg.GreaterDamageConstA = ini.GetInt(Section, "GreaterDamageConstA", 0);

        cfg.GreaterDamageConstB = ini.GetInt(Section, "GreaterDamageConstB", 0);

        cfg.GreaterDamageRate[0] = ini.GetInt(Section, "GreaterDamageRateDW", 0);
        cfg.GreaterDamageRate[1] = ini.GetInt(Section, "GreaterDamageRateDK", 0);
        cfg.GreaterDamageRate[2] = ini.GetInt(Section, "GreaterDamageRateFE", 0);
        cfg.GreaterDamageRate[3] = ini.GetInt(Section, "GreaterDamageRateMG", 0);
        cfg.GreaterDamageRate[4] = ini.GetInt(Section, "GreaterDamageRateDL", 0);

        cfg.GreaterDamageTimeConstA = ini.GetInt(Section, "GreaterDamageTimeConstA", 0);

        cfg.SummonMonster1 = ini.GetInt(Section, "SummonMonster1", 0);

        cfg.SummonMonster2 = ini.GetInt(Section, "SummonMonster2", 0);

        cfg.SummonMonster3 = ini.GetInt(Section, "SummonMonster3", 0);

        cfg.SummonMonster4 = ini.GetInt(Section, "SummonMonster4", 0);

        cfg.SummonMonster5 = ini.GetInt(Section, "SummonMonster5", 0);

        cfg.SummonMonster6 = ini.GetInt(Section, "SummonMonster6", 0);

        cfg.SummonMonster7 = ini.GetInt(Section, "SummonMonster7", 0);

        cfg.GreaterLifeConstA = ini.GetInt(Section, "GreaterLifeConstA", 0);

        cfg.GreaterLifeConstB = ini.GetInt(Section, "GreaterLifeConstB", 0);

        cfg.GreaterLifeConstC = ini.GetInt(Section, "GreaterLifeConstC", 0);

        cfg.GreaterLifeRate[0] = ini.GetInt(Section, "GreaterLifeRateDW", 0);
        cfg.GreaterLifeRate[1] = ini.GetInt(Section, "GreaterLifeRateDK", 0);
        cfg.GreaterLifeRate[2] = ini.GetInt(Section, "GreaterLifeRateFE", 0);
        cfg.GreaterLifeRate[3] = ini.GetInt(Section, "GreaterLifeRateMG", 0);
        cfg.GreaterLifeRate[4] = ini.GetInt(Section, "GreaterLifeRateDL", 0);

        cfg.GreaterLifeTimeConstA = ini.GetInt(Section, "GreaterLifeTimeConstA", 0);

        cfg.GreaterLifeTimeConstB = ini.GetInt(Section, "GreaterLifeTimeConstB", 0);

        cfg.GreaterLifeMaxRate = ini.GetInt(Section, "GreaterLifeMaxRate", 0);

        cfg.FireSlashConstA = ini.GetInt(Section, "FireSlashConstA", 0);

        cfg.FireSlashConstB = ini.GetInt(Section, "FireSlashConstB", 0);

        cfg.FireSlashTimeConstA = ini.GetInt(Section, "FireSlashTimeConstA", 0);

        cfg.FireSlashMaxRate = ini.GetInt(Section, "FireSlashMaxRate", 0);

        cfg.GreaterCriticalDamageConstA = ini.GetInt(Section, "GreaterCriticalDamageConstA", 0);

        cfg.GreaterCriticalDamageConstB = ini.GetInt(Section, "GreaterCriticalDamageConstB", 0);

        cfg.GreaterCriticalDamageTimeConstA = ini.GetInt(Section, "GreaterCriticalDamageTimeConstA", 0);

        cfg.GreaterCriticalDamageTimeConstB = ini.GetInt(Section, "GreaterCriticalDamageTimeConstB", 0);

        cfg.InfinityArrowSwitch[0] = ini.GetInt(Section, "InfinityArrowSwitch_AL0", 0);
        cfg.InfinityArrowSwitch[1] = ini.GetInt(Section, "InfinityArrowSwitch_AL1", 0);
        cfg.InfinityArrowSwitch[2] = ini.GetInt(Section, "InfinityArrowSwitch_AL2", 0);
        cfg.InfinityArrowSwitch[3] = ini.GetInt(Section, "InfinityArrowSwitch_AL3", 0);

        cfg.MagicDamageImmunityTimeConstA = ini.GetInt(Section, "MagicDamageImmunityTimeConstA", 0);

        cfg.PhysiDamageImmunityTimeConstA = ini.GetInt(Section, "PhysiDamageImmunityTimeConstA", 0);

        return cfg;
    }
}
