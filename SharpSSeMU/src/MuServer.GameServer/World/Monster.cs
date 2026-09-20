namespace MuServer.GameServer.World;

/// <summary>
/// Puerto reducido de la porción "monstruo" del OBJECTSTRUCT compartido (User.h:399-713) -- el
/// original mezcla jugadores y monstruos en el mismo arreglo gObj[10000]; acá se separa en dos
/// clases (PlayerObject / Monster) como en las fases anteriores, con MonsterRegistry ocupando el
/// mismo rango de índices que el original reserva para monstruos (0-7999, MAX_OBJECT_MONSTER).
///
/// Fase 4 (primera pasada): monstruos ESTÁTICOS -- sin IA de patrulla/persecución
/// (gObjMonsterUpdateProc/gObjMonsterReactionProc no portados todavía). Esto es seguro a nivel de
/// protocolo (confirmado en la investigación de esta fase: un monstruo inactivo nunca entra a
/// EMOTION_ATTACK/movimiento y por lo tanto nunca emite paquetes 0xD7/0xD9 propios), pero significa
/// que los monstruos no atacan al jugador todavía -- solo se los puede atacar a ellos. Contraataque
/// de monstruo queda para una siguiente pasada de esta misma fase.
/// </summary>
public sealed class Monster
{
    public required int Index { get; init; }
    public required int MonsterClass { get; init; }
    public required MonsterSpawnEntry SpawnEntry { get; init; }

    public string Name { get; init; } = string.Empty;
    public int Level { get; init; }

    public float Life { get; set; }
    public float MaxLife { get; init; }

    public byte Map { get; init; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public byte TX { get; set; }
    public byte TY { get; set; }
    public byte Dir { get; set; }

    /// <summary>Puerto de lpObj->StartX/StartY (Monster.cpp:199,419) -- dónde apareció ESTE monstruo,
    /// que es el centro desde el que puede alejarse como máximo <c>SpawnEntry.Dis</c> al pasear
    /// (gObjMonsterMoveCheck, Monster.cpp:430-462).
    ///
    /// <para>No se puede usar <c>SpawnEntry.X/Y</c> para esto: en los spawns de área (Type 1) esos
    /// campos son la ESQUINA del rectángulo compartido por todos los monstruos de esa fila, no la
    /// posición de cada uno. Usarlos arrastraba a los 45 spiders y 40 budge dragons de Lorencia a un
    /// cuadradito en la esquina del área, caminando en círculos encimados.</para></summary>
    public byte StartX { get; set; }
    public byte StartY { get; set; }

    public int DamageMin { get; init; }
    public int DamageMax { get; init; }
    public int Defense { get; init; }
    public int AttackSuccessRate { get; init; }
    public int DefenseSuccessRate { get; init; }
    public int AttackRange { get; init; }
    public int AttackType { get; init; }
    public int MonsterSkill { get; init; }
    public int ViewRange { get; init; }
    public int AttackSpeed { get; init; }
    public int ItemRate { get; init; }
    public int MoneyRate { get; init; }

    /// <summary>MONSTER_INFO.RegenTime*1000 (Monster.cpp:264) -- puerto de MaxRegenTime.</summary>
    public int MaxRegenMillis { get; init; }

    public bool Live { get; set; } = true;

    /// <summary>Momento en que murió (GetTickCount() en el original, ObjectManager.cpp:2897) -- el
    /// respawn se dispara cuando pasan MaxRegenMillis+1000ms desde acá (ver Monster.cpp:97-99,
    /// el "+1000" es un buffer fijo de gracia del original).</summary>
    public DateTime? DiedAt { get; set; }

    /// <summary>Puerto simplificado de HitDamage[MAX_HIT_DAMAGE] (User.h:359-364) -- cuánto daño
    /// acumulado infligió cada jugador, usado para repartir experiencia al morir
    /// (CharacterCalcExperienceSplit/Alone, ObjectManager.cpp:789-865). Sin reparto de grupo todavía
    /// (Social/Party no portado) -- el reparto acá siempre es "cada atacante se lleva la parte
    /// proporcional a SU daño", igual que el original hace incluso sin grupo.</summary>
    public Dictionary<int, int> DamageByAttacker { get; } = new();

    /// <summary>Jugadores que actualmente lo tienen en su viewport -- puerto simplificado de la
    /// lista inversa VpPlayer2[] (Util.cpp::MsgSendV2), usada para dirigir paquetes de daño/muerte
    /// solo a quien realmente lo está viendo.</summary>
    public HashSet<int> VisibleTo { get; } = new();

    public bool IsDead => !Live;

    public int TargetIndex { get; set; } = -1;
    public DateTime LastAttackTime { get; set; } = DateTime.MinValue;
    public DateTime LastMoveTime { get; set; } = DateTime.MinValue;

    // ---------------------------------------------------------------- Fase 6: eventos especiales (primera pasada)

    /// <summary>Puerto simplificado de la pertenencia implícita por MonsterIndex[] en
    /// DEVIL_SQUARE_LEVEL (DevilSquare.h) -- si no-nulo, este monstruo fue spawneado dinámicamente
    /// por CDevilSquare::SetMonster para el bracket indicado (0-based) y sus muertes cuentan puntaje
    /// de evento (ver ClientProtocolHandler.OnMonsterDeathAsync -> DevilSquareManager.OnMonsterKilledAsync)
    /// además del reparto de experiencia normal, que sigue aplicando igual que a cualquier otro
    /// monstruo (los dos sistemas son independientes en el original, no se excluyen entre sí).</summary>
    public int? DevilSquareBracket { get; set; }

    // ---------------------------------------------------------------- NPCs y tiendas (primera pasada)

    /// <summary>No-nulo si este objeto es en realidad un NPC de tienda (ShopManager.txt), no un
    /// monstruo de combate real -- reusa toda la infraestructura de <see cref="Monster"/>/
    /// <see cref="MonsterRegistry"/>/viewport tal cual (el original también representa NPCs como
    /// filas de "monstruo" con Type!=0, ver MonsterInfoTable/MonsterRegistry.SpawnAll), pero
    /// <see cref="ClientProtocolHandler.OnAttackAsync"/>/OnSkillAttackAsync deben rechazar atacar
    /// cualquier objeto con este campo seteado. El valor es el mismo índice de clase del NPC
    /// (=ShopNumber en este puerto, ver World/Shop.cs).</summary>
    public int? ShopNumber { get; set; }
}
