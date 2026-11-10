namespace MuServer.GameServer.World;

/// <summary> Reduced port of the "monster" portion of the shared OBJECTSTRUCT (User.h:399-713) -- the original
/// mixes players and monsters in the same array gObj[10000]; here it is split into two classes (PlayerObject /
/// Monster) as in earlier phases, with MonsterRegistry occupying the same index range the original reserves for
/// monsters (0-7999, MAX_OBJECT_MONSTER). Phase 4 (first pass): STATIC monsters -- no patrol/chase AI
/// (gObjMonsterUpdateProc/gObjMonsterReactionProc not ported yet). This is safe at protocol level (confirmed in
/// this phase's research: an idle monster never enters EMOTION_ATTACK/movement and therefore never emits its
/// own 0xD7/0xD9 packets), but it means monsters do not attack the player yet -- they can only be attacked.
/// Monster counterattack is left for a next pass of this same phase. </summary>
public sealed class Monster : IEffectCarrier
{
    public required int Index { get; init; }
    public required int MonsterClass { get; init; }
    public required MonsterSpawnEntry SpawnEntry { get; init; }

    public string Name { get; init; } = string.Empty;
    public int Level { get; init; }

    public float Life { get; set; }
    public float MaxLife { get; set; }

    public byte Map { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public byte TX { get; set; }
    public byte TY { get; set; }
    public byte Dir { get; set; }

    /// <summary>Port of lpObj->StartX/StartY (Monster.cpp:199,419) -- where THIS monster appeared, which is the
    /// centre from which it can stray at most <c>SpawnEntry.Dis</c> when wandering (gObjMonsterMoveCheck,
    /// Monster.cpp:430-462). <para><c>SpawnEntry.X/Y</c> cannot be used for this: in area spawns (Type 1) those
    /// fields are the CORNER of the rectangle shared by all the monsters of that row, not each one's position.
    /// Using them dragged the 45 spiders and 40 budge dragons of Lorencia into a little square in the corner of
    /// the area, walking in overlapping circles.</para></summary>
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
    public int MoveSpeed { get; init; }
    public int ItemRate { get; init; }
    public int MoneyRate { get; init; }

    /// <summary>The monster's zen (OBJECTSTRUCT::Money): not a fixed reward but the experience it gave, summed over
    /// everyone who hit it -- set when it dies, before its loot is decided.</summary>
    public long Money { get; set; }

    /// <summary>MONSTER_INFO.RegenTime*1000 (Monster.cpp:264) -- puerto de MaxRegenTime.</summary>
    public int MaxRegenMillis { get; set; }

    public bool Live { get; set; } = true;

    /// <summary>Moment when it died (GetTickCount() in the original, ObjectManager.cpp:2897) -- the respawn
    /// triggers when MaxRegenMillis+1000ms have passed since then (see Monster.cpp:97-99, the "+1000" is a
    /// fixed grace buffer of the original).</summary>
    public DateTime? DiedAt { get; set; }

    /// <summary>Simplified port of HitDamage[MAX_HIT_DAMAGE] (User.h:359-364) -- how much accumulated damage
    /// each player inflicted, used to share experience on death (CharacterCalcExperienceSplit/Alone,
    /// ObjectManager.cpp:789-865). Without party sharing yet (Social/Party not ported) -- the split here is
    /// always "each attacker takes the part proportional to THEIR damage", like the original does even without
    /// a party.</summary>
    // Thread-safe on purpose: hits arrive on the packet threads while the respawn tick (MonsterRegistry.Respawn)
    // clears it from the ticker thread. With a plain Dictionary a hit landing right at the respawn instant could
    // corrupt it and spin the packet handler forever.
    public System.Collections.Concurrent.ConcurrentDictionary<int, int> DamageByAttacker { get; } = new();

    /// <summary>Players who currently have it in their viewport -- simplified port of the reverse list
    /// VpPlayer2[] (Util.cpp::MsgSendV2), used to direct damage/death packets only to whoever is really
    /// watching it.</summary>
    public HashSet<int> VisibleTo { get; } = new();

    public bool IsDead => !Live;

    public int TargetIndex { get; set; } = -1;
    public DateTime LastAttackTime { get; set; } = DateTime.MinValue;
    public DateTime LastMoveTime { get; set; } = DateTime.MinValue;

    // ---------------------------------------------------------------- Fase 6: eventos especiales (primera pasada)

    /// <summary>Simplified port of the implicit membership by MonsterIndex[] in DEVIL_SQUARE_LEVEL
    /// (DevilSquare.h) -- if non-null, this monster was dynamically spawned by CDevilSquare::SetMonster for the
    /// indicated (0-based) bracket and its deaths count towards event score (see
    /// ClientProtocolHandler.OnMonsterDeathAsync -> DevilSquareManager.OnMonsterKilledAsync) in addition to the
    /// normal experience share, which still applies just as to any other monster (the two systems are
    /// independent in the original, they are not mutually exclusive).</summary>
    public int? DevilSquareBracket { get; set; }

    /// <summary>Non-null if this monster belongs to the (0-based) level of a running Blood Castle -- spawned for
    /// one stage of it, or the castle gate or a statue (<c>BLOOD_CASTLE_LEVEL::MonsterIndex</c>).</summary>
    public int? BloodCastleLevel { get; set; }

    /// <summary>Non-null if this monster belongs to the (0-based) level of a running Chaos Castle
    /// (<c>CHAOS_CASTLE_LEVEL::MonsterIndex</c>).</summary>
    public int? ChaosCastleLevel { get; set; }

    /// <summary>Takes no damage at all, whatever hits it: the Blood Castle gate and statues until their stage is reached and the
    /// Kalima gate. (The original gives the first two the monster immunity effects, which <see cref="EffectManager.IsImmune"/>
    /// also honors; this flag is what the event code sets.) A blow against it is a miss.</summary>
    public bool Immune { get; set; }

    // ---------------------------------------------------------------- effects (EffectList.txt)

    /// <summary>The sixteen effect slots (CEffect Effect[MAX_EFFECT_LIST]): the poison, the ice... that skills put on it.</summary>
    public ActiveEffect[] ActiveEffects { get; } = ActiveEffect.CreateSlots();

    /// <summary>What the active effects change in its numbers (EffectOption): the Fire Slash lowers its defense, the Ice slows it.</summary>
    public EffectBonus EffectBonus { get; } = new();

    /// <summary>How well it shrugs a skill off (Resistance[7] of MonsterList.txt: ice, poison, lightning, fire, earth, wind, water).
    /// 0 never resists; each point makes the skill's effect one chance in (resistance + 1) to land; 255 is immune.</summary>
    public byte[] Resistance { get; } = new byte[7];

    /// <summary>The defense a hit meets (the first lines of CAttack::GetTargetDefense, Attack.cpp:1119), with what the effects add and take.</summary>
    public int EffectiveDefense()
    {
        int defense = Defense + EffectBonus.AddDefense - EffectBonus.SubDefense;

        if (EffectBonus.MulDefense > 0)
        {
            defense += defense * EffectBonus.MulDefense / 100;
        }

        if (EffectBonus.DivDefense > 0)
        {
            defense -= defense * EffectBonus.DivDefense / 100;
        }

        return defense;
    }

    /// <summary>The player whose summon this is (a Fairy Elf's Summon skills), or -1 for any other monster. A summon follows its master and
    /// fights what he fights; it is nobody's target and what it kills is its master's.</summary>
    public int OwnerIndex { get; set; } = -1;

    public bool IsSummon => OwnerIndex >= 0;

    /// <summary>The summon this monster is fighting, -1 when it fights none: a monster that a summon hits goes for it instead of the players
    /// (gObjMonsterProcessHit, Monster.cpp:1719), and the summon manager plays its part until one of them is gone.</summary>
    public int SummonTargetIndex { get; set; } = -1;

    /// <summary>It has just been called: the next time somebody sees it appear it comes with the magic circle.</summary>
    public bool JustSummoned { get; set; }

    /// <summary>Whether a player can attack it: not a shop NPC, and not somebody's summon.</summary>
    public bool CanBeAttacked => ShopNumber == null && !IsSummon;

    /// <summary>Once dead it is taken out of the world instead of coming back (<c>PosNum = -1</c> in the
    /// original): the Blood Castle gate and statues.</summary>
    public bool NoRespawn { get; set; }

    // ---------------------------------------------------------------- NPCs y tiendas (primera pasada)

    /// <summary>Non-null if this object is actually a shop NPC (ShopManager.txt), not a real combat monster --
    /// it reuses the whole <see cref="Monster"/>/ <see cref="MonsterRegistry"/>/viewport infrastructure as is
    /// (the original also represents NPCs as "monster" rows with Type!=0, see
    /// MonsterInfoTable/MonsterRegistry.SpawnAll), but <see
    /// cref="ClientProtocolHandler.OnAttackAsync"/>/OnSkillAttackAsync must reject attacking any object with
    /// this field set. The value is the NPC's same class index (=ShopNumber in this port, see
    /// World/Shop.cs).</summary>
    public int? ShopNumber { get; set; }
}
