using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto PARCIAL de <c>CServerInfo</c> (ServerInfo.h/.cpp, ~520 campos entre 7 archivos INI reales:
/// <c>GameServerInfo - {ChaosMix,Command,Common,Custom,Event,Item,Skill}.dat</c>, todos texto plano
/// pese a la extensión .dat, leídos con GetPrivateProfileInt/String igual que <see cref="IniFile"/>).
///
/// Esta clase SOLO carga los campos de <c>Common.dat</c> y <c>Event.dat</c> que tienen un consumidor
/// real ya portado en este proyecto (verificado grepeando cada <c>gServerInfo.m_Xxx</c> contra el
/// código fuente completo, no solo dónde se lee el .dat). El resto de campos de esos 2 archivos (PK,
/// Trade, Duel, Guild, Jewel, Fruit, Quest, reparto de experiencia de grupo) y los otros 5 archivos
/// completos quedan SIN portar porque el sistema que los consumiría no existe todavía en este puerto:
///
/// - <c>ChaosMix.dat</c> (~90 campos): tasas de combinación/mezcla de items (Chaos Mix, alas de
///   2da gen, Dinorant, Fruit, mascotas) -- el NPC/mecánica de Chaos Mix no está portado.
/// - <c>Command.dat</c> (~90 campos): configuración de <c>/reset</c> y <c>/masterreset</c> (puntos,
///   límites diarios/semanales/mensuales, requisitos por clase) -- el sistema de reset no existe.
/// - <c>Custom.dat</c>: Custom Arena/Attack/Pick (eventos custom de SSeMU, ni siquiera son
///   <c>CServerInfo</c> -- los leen 3 clases propias, <c>CCustomArena</c>/<c>CCustomAttack</c>/
///   <c>CCustomPick</c>) -- ninguno de los 3 está portado.
/// - <c>Item.dat</c>: constantes de Transformation Ring, daño de Satanás/Dinorant/Ángel/Caballo
///   Oscuro, tasas de poción (Apple/Life/Mana chica-mediana-grande) por clase, Ale/Olivo/Remedio del
///   Amor -- ninguno de estos items/efectos especiales está implementado como sistema de juego.
/// - <c>Skill.dat</c>: constantes de Mana Shield (daño absorbido/tiempo/tasa por clase) -- el skill
///   Mana Shield no está portado (<see cref="SkillInfoTable"/> solo cubre skills de ataque).
/// - De <c>Common.dat</c>: PK (todo el sistema de puntos/anuncios/límites), Trade/PersonalShop/Duel/
///   Guild switches (esos sistemas no existen), Jewel (Soul/Life/Luck success rates -- fabricar
///   joyas no está portado), Fruit (Fruta de Vida/Poder -- no portado), Quest
///   (<c>QuestMonsterItemDropParty</c> -- sistema de quest no portado),
///   <c>PartyGeneralExperience</c>/<c>PartySpecialExperience</c>/<c>PartyMaxGapLevel</c> (el reparto
///   de experiencia de grupo de este puerto, <see cref="World.PlayerObject"/> vía
///   <c>GrantPartyExperienceAsync</c>, usa una fórmula estructuralmente distinta a la real de
///   <c>CharacterCalcExperienceParty</c> -- ObjectManager.cpp:895-970 -- reconciliarla es trabajo
///   aparte, no alcanza con enchufar estas 3 constantes).
/// - De <c>Event.dat</c>: todo lo de Blood Castle/Chaos Castle/Bonus Manager/Drop Event/Invasion
///   Manager (esos eventos no están portados) -- de Devil Square solo se usa
///   <see cref="DevilSquareMaxUser"/>, el resto (<c>DevilSquareMaxEntryCount_AL0-3</c>, tope diario
///   por cuenta) tampoco se trackea.
///
/// Todos los <c>_AL0-3</c> son por "AccountLevel" (nivel de cuenta/VIP 0-3, ObjectManager.cpp) --
/// indexado con <see cref="World.PlayerObject.AccountLevel"/>, que JoinServer calcula de verdad
/// (WZ_GetAccountLevel) y manda a GameServer al conectar la cuenta.
/// </summary>
public sealed class ServerInfoConfig
{
    // ---- Common.dat: CServerInfo::ReadStartupInfo (ServerInfo.cpp:382-447) ----

    /// <summary>Puerto de gObjSetExperienceTable (User.cpp:276-297): junto con
    /// <see cref="ExperienceMultiplierConstB"/> arma la tabla real de experiencia requerida por
    /// nivel, <c>gLevelExperience[n] = (n+9)*n*n*ConstA</c> (más un término extra con ConstB para
    /// niveles por encima de 255) -- reemplaza el placeholder <c>nivel²*1000</c> que tenía
    /// <see cref="Protocol.WorldPacketBuilder.NextExperience"/> antes de portar este archivo.</summary>
    public int ExperienceMultiplierConstA { get; private set; } = 10;

    public int ExperienceMultiplierConstB { get; private set; } = 1000;

    /// <summary>Tabla de experiencia de mascotas (gPetExperience) -- cargado por completitud, este
    /// puerto no tiene mascotas todavía.</summary>
    public int PetExperienceMultiplierConstA { get; private set; } = 100;

    public int MaxLevel { get; private set; } = 400;

    public int MaxPetLevel { get; private set; } = 50;

    /// <summary>Puerto de CObjectManager::CharacterLevelUp (ObjectManager.cpp:983-1041) -- tope de
    /// niveles que un solo evento de experiencia (una muerte de monstruo) puede hacer subir de una vez.
    /// Al llegar al tope, TODA la experiencia sobrante de ese evento se descarta -- no queda guardada
    /// para el próximo kill (<c>AddExperience -= (((--MaxLevelUp)==0)?AddExperience:...)</c>). Antes de
    /// portar este campo, el puerto dejaba subir tantos niveles como alcanzara la experiencia ganada en
    /// un solo kill, sin límite ni descarte.</summary>
    public int MaxLevelUp { get; private set; } = 1;

    // ---- Common.dat: CServerInfo::ReadCommonInfo (ServerInfo.cpp:1104-1328), solo lo consumido ----

    /// <summary>Puerto de CMonsterManager::SetInfo (MonsterManager.cpp:166-183) -- multiplicadores
    /// globales de servidor aplicados sobre las columnas crudas de MonsterList.txt al spawnear
    /// (<see cref="World.MonsterRegistry.SpawnAll"/>), todos en porcentaje (100 = sin cambio).</summary>
    public int MonsterMaxLifeRate { get; private set; } = 100;
    public int MonsterDefenseRate { get; private set; } = 100;
    public int MonsterDefenseSuccessRateRate { get; private set; } = 100;
    public int MonsterPhysiDamageRate { get; private set; } = 100;
    public int MonsterAttackSuccessRateRate { get; private set; } = 100;

    /// <summary>Puerto de CharacterCalcExperienceAlone (ObjectManager.cpp:845) -- multiplicador
    /// DIRECTO (no porcentaje, no hay "/100") sobre la experiencia ya calculada de un kill en
    /// solitario. Indexado por AccountLevel (PlayerObject.AccountLevel).</summary>
    public int[] AddExperienceRate { get; private set; } = { 1, 1, 1, 1 };
    public int[] MoneyAmountDropRate { get; private set; } = { 100, 100, 100, 100 };

    /// <summary>Puerto de m_ItemDropTime/m_MoneyDropTime (Common.dat) -- tiempo de vida (segundos) de
    /// un item/dinero tirado en el piso antes de desaparecer solo; el loot-lock del dueño dura la
    /// MITAD de este tiempo (<c>m_ItemDropTime*500</c> en ms, MapItem.cpp:29-99) -- ver
    /// <see cref="World.GroundItem"/>. CORREGIDO: antes de portar este archivo el puerto usaba 60s/
    /// 30s hardcodeado (el doble de lo real).</summary>
    public int ItemDropTimeSeconds { get; private set; } = 30;
    public int MoneyDropTimeSeconds { get; private set; } = 30;

    // ---- Event.dat: CServerInfo::ReadEventInfo (ServerInfo.cpp:1330-1372), solo Devil Square ----

    /// <summary>Puerto de MAX_DS_USER real (m_DevilSquareMaxUser) -- tope de participantes por
    /// bracket de Devil Square (<see cref="World.DevilSquareManager"/>). CORREGIDO: antes de portar
    /// este archivo el puerto tenía un tope hardcodeado de 50 (arbitrario); el real es 15.</summary>
    public int DevilSquareMaxUser { get; private set; } = 15;

    public static ServerInfoConfig Load(string commonPath, string eventPath)
    {
        var common = IniFile.Load(commonPath);
        var evt = IniFile.Load(eventPath);
        var cfg = new ServerInfoConfig
        {
            ExperienceMultiplierConstA = common.GetInt("GameServerInfo", "ExperienceMultiplierConstA", 10),
            ExperienceMultiplierConstB = common.GetInt("GameServerInfo", "ExperienceMultiplierConstB", 1000),
            PetExperienceMultiplierConstA = common.GetInt("GameServerInfo", "PetExperienceMultiplierConstA", 100),
            MaxLevel = common.GetInt("GameServerInfo", "MaxLevel", 400),
            MaxPetLevel = common.GetInt("GameServerInfo", "MaxPetLevel", 50),
            MaxLevelUp = common.GetInt("GameServerInfo", "MaxLevelUp", 1),

            MonsterMaxLifeRate = common.GetInt("GameServerInfo", "MonsterMaxLifeRate", 100),
            MonsterDefenseRate = common.GetInt("GameServerInfo", "MonsterDefenseRate", 100),
            MonsterDefenseSuccessRateRate = common.GetInt("GameServerInfo", "MonsterDefenseSuccessRateRate", 100),
            MonsterPhysiDamageRate = common.GetInt("GameServerInfo", "MonsterPhysiDamageRate", 100),
            MonsterAttackSuccessRateRate = common.GetInt("GameServerInfo", "MonsterAttackSuccessRateRate", 100),

            AddExperienceRate = new[]
            {
                common.GetInt("GameServerInfo", "AddExperienceRate_AL0", 1),
                common.GetInt("GameServerInfo", "AddExperienceRate_AL1", 1),
                common.GetInt("GameServerInfo", "AddExperienceRate_AL2", 1),
                common.GetInt("GameServerInfo", "AddExperienceRate_AL3", 1),
            },

            MoneyAmountDropRate = new[]
            {
                common.GetInt("GameServerInfo", "MoneyAmountDropRate_AL0", 100),
                common.GetInt("GameServerInfo", "MoneyAmountDropRate_AL1", 100),
                common.GetInt("GameServerInfo", "MoneyAmountDropRate_AL2", 100),
                common.GetInt("GameServerInfo", "MoneyAmountDropRate_AL3", 100),
            },

            ItemDropTimeSeconds = common.GetInt("GameServerInfo", "ItemDropTime", 30),
            MoneyDropTimeSeconds = common.GetInt("GameServerInfo", "MoneyDropTime", 30),

            DevilSquareMaxUser = evt.GetInt("GameServerInfo", "DevilSquareMaxUser", 15),
        };

        return cfg;
    }
}
