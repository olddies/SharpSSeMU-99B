using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The PK system (User.cpp: gObjPlayerKiller, gObjPKDownCheckTime, gObjCheckSelfDefense, gObjKillerItemDrop)
/// with the shipped constants: a PK time of 6, 12 and 24 hours marks the levels 4, 5 and 6, and one more kill at the
/// top adds an hour.</summary>
public class PkSystemTests
{
    private static readonly PkRules Rules = new();
    private int _next;

    [SetUp]
    public void SetUp() => _next = 100;

    private PlayerObject Player(byte pkLevel = 3, uint pkTime = 0, int party = -1) => new()
    {
        Index = _next++, Session = FakeSession.Create(), Level = 30, Name = $"P{_next}", PKLevel = pkLevel, PKTime = pkTime, PartyNumber = party,
    };

    // ---------------------------------------------------------------- killing

    [Test]
    public void Killing_an_innocent_makes_a_murderer_for_six_hours()
    {
        var killer = Player();
        var changed = PkSystem.PlayerKilled(killer, Player(), Rules);

        Assert.That(changed, Is.True);
        Assert.That((killer.PKLevel, killer.PKCount, killer.PKTime), Is.EqualTo(((byte)4, 1u, 21_600u)));
    }

    [Test]
    public void Each_kill_of_a_murderer_goes_a_level_up_until_the_top_and_then_adds_an_hour()
    {
        var killer = Player();
        var steps = new List<(byte Level, uint Time)>();

        for (int n = 0; n < 4; n++)
        {
            PkSystem.PlayerKilled(killer, Player(), Rules);
            steps.Add((killer.PKLevel, killer.PKTime));
        }

        // 6 hours (level 4) -> 12 hours (5) -> 24 hours (6) -> 25 hours (still 6)
        Assert.That(steps, Is.EqualTo(new[] { ((byte)4, 21_600u), ((byte)5, 43_200u), ((byte)6, 86_400u), ((byte)6, 90_000u) }));
        Assert.That(killer.PKCount, Is.EqualTo(4u));
    }

    [Test]
    public void Killing_a_murderer_makes_a_hero()
    {
        var killer = Player();
        var changed = PkSystem.PlayerKilled(killer, Player(pkLevel: 5), Rules);

        Assert.That(changed, Is.True);
        Assert.That((killer.PKLevel, killer.PKCount, killer.PKTime), Is.EqualTo(((byte)2, 1u, 21_600u)));

        PkSystem.PlayerKilled(killer, Player(pkLevel: 6), Rules);
        Assert.That((killer.PKLevel, killer.PKTime), Is.EqualTo(((byte)1, 43_200u)), "a second one: level 1");
    }

    [Test]
    public void A_hero_who_kills_an_innocent_is_a_murderer_from_the_start()
    {
        var hero = Player(pkLevel: 2, pkTime: 5000);
        hero.PKCount = 7;

        PkSystem.PlayerKilled(hero, Player(), Rules);

        Assert.That((hero.PKLevel, hero.PKCount, hero.PKTime), Is.EqualTo(((byte)4, 1u, 21_600u)));
    }

    [Test]
    public void A_murderer_killing_a_murderer_changes_nothing()
    {
        var killer = Player(pkLevel: 4, pkTime: 1000);

        Assert.That(PkSystem.PlayerKilled(killer, Player(pkLevel: 5), Rules), Is.False);
        Assert.That((killer.PKLevel, killer.PKCount, killer.PKTime), Is.EqualTo(((byte)4, 0u, 1000u)));
    }

    [Test]
    public void Killing_someone_who_attacked_you_first_is_self_defense_and_costs_nothing()
    {
        var killer = Player();
        var victim = Player();
        victim.SelfDefense[0] = killer.Index; // the victim attacked the killer first

        Assert.That(PkSystem.PlayerKilled(killer, victim, Rules), Is.False);
        Assert.That((killer.PKLevel, killer.PKCount), Is.EqualTo(((byte)3, 0u)));
    }

    // ---------------------------------------------------------------- time

    [Test]
    public void A_murderers_time_runs_down_and_the_level_falls_with_it_reporting_the_change_once()
    {
        var murderer = Player(pkLevel: 6, pkTime: 43_201);

        Assert.That(PkSystem.DownCheckTime(murderer, 1, byKill: false, Rules), Is.True);
        Assert.That(PkSystem.DownCheckTime(murderer, 1, byKill: false, Rules), Is.False, "nothing new a second later");
        Assert.That((murderer.PKLevel, murderer.PKTime), Is.EqualTo(((byte)5, 43_199u)));
    }

    [Test]
    public void When_the_time_is_over_the_murderer_is_normal_again()
    {
        var murderer = Player(pkLevel: 4, pkTime: 1);

        Assert.That(PkSystem.DownCheckTime(murderer, 1, byKill: false, Rules), Is.True);
        Assert.That((murderer.PKLevel, murderer.PKTime), Is.EqualTo(((byte)3, 0u)));
    }

    [Test]
    public void A_normal_player_is_left_alone()
    {
        var player = Player();

        Assert.That(PkSystem.DownCheckTime(player, 1000, byKill: false, Rules), Is.False);
        Assert.That(player.PKTime, Is.EqualTo(0u));
    }

    [Test]
    public void A_monster_killed_takes_time_off_a_murderer_but_not_off_a_hero()
    {
        var murderer = Player(pkLevel: 4, pkTime: 100);
        var hero = Player(pkLevel: 2, pkTime: 100);

        PkSystem.DownCheckTime(murderer, 10, byKill: true, Rules);
        PkSystem.DownCheckTime(hero, 10, byKill: true, Rules);

        Assert.That(murderer.PKTime, Is.EqualTo(90u));
        Assert.That(hero.PKTime, Is.EqualTo(100u), "a hero's time only runs down with time");

        PkSystem.DownCheckTime(hero, 10, byKill: false, Rules);
        Assert.That(hero.PKTime, Is.EqualTo(90u));
    }

    // ---------------------------------------------------------------- self-defense

    private static PlayerObject? Find(IEnumerable<PlayerObject> all, int index) => all.FirstOrDefault(p => p.Index == index);

    [Test]
    public void Attacking_someone_starts_a_self_defense_for_a_minute_that_a_new_blow_renews()
    {
        var attacker = Player();
        var target = Player();
        var now = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        Assert.That(PkSystem.StartSelfDefense(attacker, target, _ => null, now), Is.True);
        Assert.That(attacker.SelfDefense, Does.Contain(target.Index), "in the last free slot, like the original");
        Assert.That(attacker.MySelfDefenseTime, Is.EqualTo(now));

        Assert.That(PkSystem.StartSelfDefense(attacker, target, _ => null, now.AddSeconds(40)), Is.False, "the same one again: only renewed");
        Assert.That(PkSystem.ExpireSelfDefense(attacker, now.AddSeconds(70)), Is.Empty, "40 + 60 seconds");
        Assert.That(PkSystem.ExpireSelfDefense(attacker, now.AddSeconds(101)), Is.EqualTo(new[] { target.Index }));
        Assert.That(attacker.SelfDefense, Is.All.EqualTo(-1));
    }

    [Test]
    public void Attacking_a_murderer_starts_nothing()
    {
        var attacker = Player();

        Assert.That(PkSystem.StartSelfDefense(attacker, Player(pkLevel: 5), _ => null, DateTime.UtcNow), Is.False);
        Assert.That(attacker.SelfDefense, Is.All.EqualTo(-1));
    }

    [Test]
    public void Fighting_someone_who_is_already_defending_against_you_or_your_party_starts_nothing()
    {
        var attacker = Player(party: 4);
        var target = Player();
        var partyMate = Player(party: 4);
        var all = new[] { attacker, target, partyMate };

        target.SelfDefense[0] = attacker.Index;
        Assert.That(PkSystem.StartSelfDefense(attacker, target, i => Find(all, i), DateTime.UtcNow), Is.False, "they are already defending against the attacker");

        target.SelfDefense[0] = partyMate.Index;
        Assert.That(PkSystem.StartSelfDefense(attacker, target, i => Find(all, i), DateTime.UtcNow), Is.False, "or against the attacker's party");
    }

    [Test]
    public void Only_five_self_defenses_fit()
    {
        var attacker = Player();

        for (int n = 0; n < 5; n++)
        {
            Assert.That(PkSystem.StartSelfDefense(attacker, Player(), _ => null, DateTime.UtcNow), Is.True);
        }

        Assert.That(PkSystem.StartSelfDefense(attacker, Player(), _ => null, DateTime.UtcNow), Is.False);
    }

    // ---------------------------------------------------------------- what a dying murderer drops

    private static ItemBalanceTable NoItems() => new();

    private PlayerObject Murderer(byte level = 4)
    {
        var murderer = Player(pkLevel: level);
        murderer.Items[Item.SlotArmor] = Make(BronzeArmor, level: 5);
        return murderer;
    }

    [Test]
    public void A_dying_murderer_may_drop_a_worn_item_by_the_rate_of_their_level()
    {
        var murderer = Murderer(level: 5);

        // killed by a player: 50% at level 5
        Assert.That(PkSystem.ChooseDroppedSlot(murderer, true, Rules, NoItems(), () => 49, _ => 0), Is.EqualTo(Item.SlotArmor));
        Assert.That(PkSystem.ChooseDroppedSlot(murderer, true, Rules, NoItems(), () => 50, _ => 0), Is.EqualTo(-1));

        // killed by a monster: 40%
        Assert.That(PkSystem.ChooseDroppedSlot(murderer, false, Rules, NoItems(), () => 39, _ => 0), Is.EqualTo(Item.SlotArmor));
        Assert.That(PkSystem.ChooseDroppedSlot(murderer, false, Rules, NoItems(), () => 40, _ => 0), Is.EqualTo(-1));
    }

    [Test]
    public void A_normal_player_never_drops_anything()
    {
        Assert.That(PkSystem.ChooseDroppedSlot(Murderer(level: 3), true, Rules, NoItems(), () => 0, _ => 0), Is.EqualTo(-1));
    }

    [Test]
    public void What_is_too_good_or_is_a_pet_or_wings_stays_on()
    {
        var murderer = Player(pkLevel: 6);
        murderer.Items[Item.SlotWeapon1] = Make(Kris, level: 10);                       // above +9
        murderer.Items[Item.SlotWing] = Make(WingsOfSatan);                              // wings: off by default
        murderer.Items[Item.SlotHelper] = Make(Item.GetItem(13, 0));                     // a pet: off by default
        murderer.Items[Item.SlotHelm] = Make(BronzeArmor, excellent: 1);                // excellent: off by default

        Assert.That(PkSystem.ChooseDroppedSlot(murderer, true, Rules, NoItems(), () => 0, _ => 0), Is.EqualTo(-1), "nothing left that may drop");

        var permissive = new PkRules { ItemDropWing = true, ItemDropPet = true, ItemDropExc = 1, ItemDropMaxLevel = 15 };
        var slots = new HashSet<int>();

        for (int pick = 0; pick < 4; pick++)
        {
            slots.Add(PkSystem.ChooseDroppedSlot(murderer, true, permissive, NoItems(), () => 0, n => pick % n));
        }

        Assert.That(slots, Is.EquivalentTo(new[] { Item.SlotWeapon1, Item.SlotWing, Item.SlotHelper, Item.SlotHelm }));
    }

    [Test]
    public void Arrows_are_never_dropped()
    {
        var murderer = Player(pkLevel: 4);
        murderer.Items[Item.SlotWeapon2] = Make(Item.GetItem(4, 15)); // the arrows

        Assert.That(PkSystem.ChooseDroppedSlot(murderer, true, Rules, NoItems(), () => 0, _ => 0), Is.EqualTo(-1));
    }

    // ---------------------------------------------------------------- the map columns

    [Test]
    public void The_map_table_reads_the_pk_columns()
    {
        string path = Path.Combine(Path.GetTempPath(), $"maps-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, """
            0    *   12   100   100   100   1000   0   1   1   *   *   *   1   1   1   1   *   *   "Lorencia"
            6    1   12   100   100   100   1000   0   1   0   *   *   *   1   1   1   1   *   *   "Arena"
            7    2   12   100   100   100   1000   0   1   1   *   *   *   1   1   1   1   *   *   "Atlans"
            end
            """);

        try
        {
            var table = new MapDropTable();
            Assert.That(table.Load(path), Is.EqualTo(3));

            Assert.That(table.NonPk(0, serverNonPk: 0), Is.False, "the star follows the server: PK allowed");
            Assert.That(table.NonPk(0, serverNonPk: 1), Is.True, "and forbidden when the server says so");
            Assert.That(table.NonPk(6, serverNonPk: 0), Is.True, "1: no PK on this map");
            Assert.That(table.NonPk(7, serverNonPk: 0), Is.False, "2: PK, but without outlaws");
            Assert.That(table.NonOutlaw(7), Is.True);
            Assert.That(table.NonOutlaw(0), Is.False);
            Assert.That(table.PkItemDrop(0), Is.True);
            Assert.That(table.PkItemDrop(6), Is.False);
            Assert.That(table.ItemDropRate(6), Is.EqualTo(100), "the columns after it still line up");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
