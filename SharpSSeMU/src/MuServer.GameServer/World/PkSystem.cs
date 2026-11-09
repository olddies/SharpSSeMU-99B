namespace MuServer.GameServer.World;

/// <summary>The PK settings of "GameServerInfo - Common.dat" (ServerInfo.cpp:1131-1160). Set once at start-up
/// (<see cref="Current"/>); the defaults are the shipped values, so code that runs without the configuration (tests)
/// gets the factory balance.</summary>
public sealed class PkRules
{
    public static PkRules Current { get; set; } = new();

    /// <summary>NonPK: 1 turns PK off on every map that does not say otherwise.</summary>
    public int NonPk { get; init; }

    /// <summary>PKDeathAnnounce: 0 says nothing when a murderer dies, 1-3 announce it to the whole server.</summary>
    public int DeathAnnounce { get; init; }

    /// <summary>Seconds of PK time a second of play (PKDownPlusTimePoint) and a monster kill's level divisor
    /// (PKDownPlusKillPoint) take off.</summary>
    public int DownPlusTimePoint { get; init; } = 1;
    public int DownPlusKillPoint { get; init; } = 2;

    /// <summary>PKDownRequirePoint1-4: the PK time that marks each level (1 = warning, 2 = murderer, 3 = the next one up)
    /// and what one more kill adds once at the top.</summary>
    public int DownRequirePoint1 { get; init; } = 21600;
    public int DownRequirePoint2 { get; init; } = 43200;
    public int DownRequirePoint3 { get; init; } = 86400;
    public int DownRequirePoint4 { get; init; } = 3600;

    /// <summary>% chance that a dying murderer of PK level 4, 5, 6 drops a worn item, when killed by a player / a monster.</summary>
    public int[] ItemDropRatePvP { get; init; } = { 25, 50, 90 };
    public int[] ItemDropRatePvM { get; init; } = { 20, 40, 80 };

    /// <summary>The highest item level that can drop, and whether pets, wings, excellent items (with how many options at
    /// most) and set items may.</summary>
    public int ItemDropMaxLevel { get; init; } = 9;
    public bool ItemDropPet { get; init; }
    public bool ItemDropWing { get; init; }
    public int ItemDropExc { get; init; }
    public bool ItemDropSet { get; init; }
}

/// <summary> Port of the PK system of the original emulator (User.cpp: gObjPlayerKiller, gObjPKDownCheckTime,
/// gObjCheckSelfDefense, gObjTimeCheckSelfDefense, gObjKillerItemDrop). The PK levels are 3 for a normal player, 4-6 for
/// a murderer (the higher, the worse) and 0-2 for a hero who kills murderers. Killing an innocent player makes a
/// murderer; the level goes down with the PK time, a second at a time while playing and with every monster killed. A
/// player who attacks another starts a self-defense against them for a minute, and the one attacked can then kill
/// them without becoming a murderer.
/// <para>Everything here is pure: the callers decide when a kill counts (no duel, no Chaos Castle, no map without
/// outlaws) and tell the clients.</para> </summary>
public static class PkSystem
{
    public const int MaxSelfDefense = 5;
    public static readonly TimeSpan SelfDefenseTime = TimeSpan.FromSeconds(60);

    private static uint Clamp(long time) => (uint)Math.Clamp(time, 0, uint.MaxValue);

    /// <summary>The murderer level (4-6) a PK time marks, or null when it marks none.</summary>
    private static byte? MurdererLevel(long time, PkRules rules) =>
        time > rules.DownRequirePoint2 ? (byte)6 : time > rules.DownRequirePoint1 ? (byte)5 : time > 0 ? (byte)4 : null;

    /// <summary>The hero level (0-2) a PK time marks, or null when it marks none.</summary>
    private static byte? HeroLevel(long time, PkRules rules) =>
        time > rules.DownRequirePoint2 ? (byte)0 : time > rules.DownRequirePoint1 ? (byte)1 : time > 0 ? (byte)2 : null;

    /// <summary>Port of gObjPKDownCheckTime: takes <paramref name="time"/> off the PK time (always for a murderer; for a
    /// hero only while time passes, <paramref name="byKill"/> false) and works the level out again. True when the level
    /// changed and the clients have to be told.</summary>
    public static bool DownCheckTime(PlayerObject player, int time, bool byKill, PkRules rules)
    {
        if (player.PKLevel == 3)
        {
            return false;
        }

        long pkTime = player.PKTime;
        byte level = 3;

        if (player.PKLevel < 3)
        {
            if (!byKill)
            {
                pkTime -= time;
            }

            level = HeroLevel(pkTime, rules) ?? 3;
        }
        else
        {
            pkTime -= time;
            level = MurdererLevel(pkTime, rules) ?? 3;
        }

        player.PKTime = Clamp(pkTime);

        if (player.PKLevel == level)
        {
            return false;
        }

        player.PKLevel = level;
        return true;
    }

    /// <summary>Port of gObjPlayerKiller: what killing <paramref name="victim"/> does to the killer's PK level. The
    /// caller has already ruled out the places where a kill does not count. Nothing happens when the killer was
    /// defending themselves (the victim attacked them first). True when the killer's level changed.</summary>
    public static bool PlayerKilled(PlayerObject killer, PlayerObject victim, PkRules rules)
    {
        if (victim.SelfDefense.Contains(killer.Index))
        {
            return false;
        }

        long time = killer.PKTime;
        byte before = killer.PKLevel;
        byte level = killer.PKLevel;

        if (killer.PKLevel > 3)
        {
            if (victim.PKLevel > 4)
            {
                return false; // a murderer killing a murderer
            }

            killer.PKCount++;
            time = killer.PKLevel switch { 4 => rules.DownRequirePoint2, 5 => rules.DownRequirePoint3, _ => time + rules.DownRequirePoint4 };
            level = MurdererLevel(time, rules) ?? level;
        }
        else if (victim.PKLevel < 5)
        {
            // Killing someone who is not a murderer: the killer becomes one.
            if (killer.PKLevel <= 3)
            {
                level = 3;
                killer.PKCount = 0;
            }

            killer.PKCount++;
            time = level switch { 3 => rules.DownRequirePoint1, 4 => rules.DownRequirePoint2, 5 => rules.DownRequirePoint3, _ => time + rules.DownRequirePoint4 };
            level = MurdererLevel(time, rules) ?? level;
        }
        else
        {
            // Killing a murderer: the killer goes up the hero levels.
            killer.PKCount++;
            time = level switch { 3 => rules.DownRequirePoint1, 2 => rules.DownRequirePoint2, 1 => rules.DownRequirePoint3, _ => time + rules.DownRequirePoint4 };
            level = HeroLevel(time, rules) ?? level;
        }

        killer.PKTime = Clamp(time);
        killer.PKLevel = level;
        return killer.PKLevel != before;
    }

    /// <summary>Port of gObjCheckSelfDefense: <paramref name="attacker"/> has just hurt <paramref name="target"/>. Unless the
    /// target is a murderer, or already fighting the attacker or the attacker's party, the attacker starts a self-defense
    /// against them for a minute (or the minute is renewed). Returns whether a new one started, which the players are told.</summary>
    public static bool StartSelfDefense(PlayerObject attacker, PlayerObject target, Func<int, PlayerObject?> find, DateTime now)
    {
        if (target.PKLevel > 4)
        {
            return false;
        }

        foreach (int defending in target.SelfDefense)
        {
            if (defending < 0)
            {
                continue;
            }

            // The target is already defending against the attacker, or against someone in the attacker's party.
            if (defending == attacker.Index || (attacker.PartyNumber >= 0 && find(defending)?.PartyNumber == attacker.PartyNumber))
            {
                return false;
            }
        }

        int blank = -1;

        for (int n = 0; n < MaxSelfDefense; n++)
        {
            if (attacker.SelfDefense[n] == target.Index)
            {
                attacker.SelfDefenseUntil[n] = now + SelfDefenseTime;
                return false;
            }

            if (attacker.SelfDefense[n] < 0)
            {
                blank = n;
            }
        }

        if (blank < 0)
        {
            return false;
        }

        attacker.MySelfDefenseTime = now;
        attacker.SelfDefense[blank] = target.Index;
        attacker.SelfDefenseUntil[blank] = now + SelfDefenseTime;
        return true;
    }

    /// <summary>Port of gObjTimeCheckSelfDefense: the self-defenses whose minute is over end. Returns who they were against.</summary>
    public static List<int> ExpireSelfDefense(PlayerObject player, DateTime now)
    {
        var ended = new List<int>();

        for (int n = 0; n < MaxSelfDefense; n++)
        {
            if (player.SelfDefense[n] >= 0 && now > player.SelfDefenseUntil[n])
            {
                ended.Add(player.SelfDefense[n]);
                player.SelfDefense[n] = -1;
            }
        }

        return ended;
    }

    /// <summary>A dying player's self-defenses end with them (the original clears the times at the respawn).</summary>
    public static void ClearSelfDefense(PlayerObject player)
    {
        Array.Fill(player.SelfDefense, -1);
        Array.Fill(player.SelfDefenseUntil, DateTime.MinValue);
    }

    /// <summary>Port of the choice in gObjKillerItemDrop: the worn slot (0-11) a dying murderer drops, or -1 for none.
    /// <paramref name="roll100"/> is the percent roll for the chance and <paramref name="pick"/> chooses among the
    /// slots that may drop.</summary>
    public static int ChooseDroppedSlot(PlayerObject victim, bool killedByPlayer, PkRules rules, ItemBalanceTable items, Func<int> roll100, Func<int, int> pick)
    {
        if (victim.PKLevel <= 3)
        {
            return -1;
        }

        int step = Math.Clamp((int)victim.PKLevel, 4, 6) - 4;
        int rate = (killedByPlayer ? rules.ItemDropRatePvP : rules.ItemDropRatePvM)[step];

        if (roll100() >= rate)
        {
            return -1;
        }

        var candidates = new List<int>();

        for (int slot = 0; slot < Item.InventoryWearSize; slot++)
        {
            var item = victim.Items[slot];

            if (!item.IsItem()
                || item.Index == Item.GetItem(4, 7) || item.Index == Item.GetItem(4, 15)   // bolt, arrow
                || item.Level > rules.ItemDropMaxLevel
                || (slot == Item.SlotWing && !rules.ItemDropWing)
                || (slot == Item.SlotHelper && !rules.ItemDropPet)
                || (slot != Item.SlotWing && item.Index != Item.GetItem(13, 3) && item.NewOption != 0 && System.Numerics.BitOperations.PopCount(item.NewOption) > rules.ItemDropExc)
                || (SetItemRules.IsSetItem(item) && !rules.ItemDropSet))
            {
                continue;
            }

            candidates.Add(slot);
        }

        return candidates.Count == 0 ? -1 : candidates[pick(candidates.Count)];
    }
}
