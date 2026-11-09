using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The pet on the helper slot wears down with the blows its wearer takes (CAttack::DamageSprite): the
/// Guardian Angel three points of durability for every hundred points of damage, the Imp two, the horns one, the Dark Horse
/// a point every fifteen hundred "small" ones. Expected values worked out by hand from Attack.cpp.</summary>
public class HelperWearTests
{
    private static readonly int Angel = Item.GetItem(13, 0);
    private static readonly int Imp = Item.GetItem(13, 1);
    private static readonly int Uniria = Item.GetItem(13, 2);
    private static readonly int Dinorant = Item.GetItem(13, 3);

    private static PetManager Manager(int guardianRate = 100, int petRate = 100) =>
        new(Items(), () => new PetRules(100, 50, 10, guardianRate, petRate));

    private static PlayerObject Wearer(int helper, byte durability = 255)
    {
        var player = new PlayerObject
        {
            Index = 9000, Session = FakeSession.Create(), Account = "acc", Name = "Wearer", Class = 1, Level = 100,
            Strength = 100, Dexterity = 20, Vitality = 25, Energy = 30, WorldEntered = true,
        };
        player.Items[Item.SlotHelper] = Make(helper, durability: durability);
        return player;
    }

    [TestCase(0, 3)] // Guardian Angel
    [TestCase(1, 2)] // Imp
    [TestCase(2, 1)] // Horn of Uniria
    [TestCase(3, 1)] // Horn of Dinorant
    public async Task Each_pet_wears_so_many_points_for_every_hundred_of_damage(int sub, int points)
    {
        var wearer = Wearer(Item.GetItem(13, sub));

        var outcome = await Manager().WearHelperAsync(wearer, 1000, CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(PetManager.HelperWearOutcome.None));
        Assert.That(wearer.Items[Item.SlotHelper].Durability, Is.EqualTo(255 - 10 * points));
        var told = ((FakeSession)wearer.Session).Of(0x2A).Single();
        Assert.That((told[3], told[4]), Is.EqualTo(((byte)Item.SlotHelper, (byte)(255 - 10 * points))), "slot and durability");
    }

    [Test]
    public async Task What_does_not_make_a_whole_point_is_kept_for_the_next_blow()
    {
        var wearer = Wearer(Angel);
        var manager = Manager();

        await manager.WearHelperAsync(wearer, 50, CancellationToken.None); // 1.5 points: 253.5 shows 253
        Assert.That(wearer.Items[Item.SlotHelper].Durability, Is.EqualTo(253));

        await manager.WearHelperAsync(wearer, 50, CancellationToken.None); // the half point is still there: 252.0
        Assert.That(wearer.Items[Item.SlotHelper].Durability, Is.EqualTo(252));
    }

    [Test]
    public async Task A_lower_guardian_rate_wears_the_pet_faster()
    {
        var wearer = Wearer(Dinorant);

        await Manager(guardianRate: 50).WearHelperAsync(wearer, 1000, CancellationToken.None); // 1 / 50% = twice the wear

        Assert.That(wearer.Items[Item.SlotHelper].Durability, Is.EqualTo(255 - 20));
    }

    [Test]
    public async Task A_pet_with_less_than_a_point_left_is_spent_and_taken_away()
    {
        var wearer = Wearer(Imp, durability: 3);

        var outcome = await Manager().WearHelperAsync(wearer, 100, CancellationToken.None); // two points: one left

        Assert.That(outcome, Is.EqualTo(PetManager.HelperWearOutcome.None));
        Assert.That(wearer.Items[Item.SlotHelper].Durability, Is.EqualTo(1));

        outcome = await Manager().WearHelperAsync(wearer, 100, CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(PetManager.HelperWearOutcome.Vanished | PetManager.HelperWearOutcome.StatsChanged));
        Assert.That(wearer.Items[Item.SlotHelper].IsItem(), Is.False);
        Assert.That(((FakeSession)wearer.Session).Of(0x28), Has.Count.EqualTo(1), "the client is told to delete it");
    }

    [Test]
    public async Task Other_helpers_and_an_empty_slot_do_not_wear()
    {
        var wearer = Wearer(Item.GetItem(13, 20)); // a ring in the helper slot: not a guardian
        Assert.That(await Manager().WearHelperAsync(wearer, 1000, CancellationToken.None), Is.EqualTo(PetManager.HelperWearOutcome.None));
        Assert.That(wearer.Items[Item.SlotHelper].Durability, Is.EqualTo(255));

        wearer.Items[Item.SlotHelper] = Item.Empty();
        Assert.That(await Manager().WearHelperAsync(wearer, 1000, CancellationToken.None), Is.EqualTo(PetManager.HelperWearOutcome.None));
    }

    [Test]
    public async Task The_dark_horse_loses_a_point_every_fifteen_hundred_small_ones_and_tells_its_wearer()
    {
        var wearer = Wearer(Item.DarkHorse);
        var horse = wearer.Items[Item.SlotHelper];
        horse.SetPetItemInfo(7, new PetRules(100, 50, 10).Experience(7), new PetRules(100, 50, 10));
        var manager = Manager();

        var outcome = await manager.WearHelperAsync(wearer, 74_000, CancellationToken.None); // 1480 + 1 = 1481 small points: not yet
        Assert.That(outcome, Is.EqualTo(PetManager.HelperWearOutcome.None));
        Assert.That(horse.Durability, Is.EqualTo(255));

        outcome = await manager.WearHelperAsync(wearer, 1_000, CancellationToken.None); // +21: 1502 > 1500

        Assert.That(outcome, Is.EqualTo(PetManager.HelperWearOutcome.StatsChanged));
        Assert.That((horse.Durability, horse.DurabilitySmall), Is.EqualTo(((byte)254, 0)));

        var session = (FakeSession)wearer.Session;
        Assert.That(session.Of(0x2A).Single()[4], Is.EqualTo(254));
        var info = session.Of(0xA9).Single();
        Assert.That((info[3], info[5], info[6]), Is.EqualTo(((byte)1, (byte)Item.SlotHelper, (byte)7)), "the horse's type, slot and level");
    }

    [Test]
    public async Task A_dark_horse_at_nothing_stays_and_wears_no_more()
    {
        var wearer = Wearer(Item.DarkHorse, durability: 0);

        var outcome = await Manager().WearHelperAsync(wearer, 1_000_000, CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(PetManager.HelperWearOutcome.None));
        Assert.That(wearer.Items[Item.SlotHelper].IsItem(), Is.True, "it is never taken away, only left at nothing");
    }

    [Test]
    public async Task What_changes_in_an_item_in_place_reaches_the_blob_that_is_saved()
    {
        var wearer = Wearer(Angel);
        wearer.SetItem(Item.SlotHelper, wearer.Items[Item.SlotHelper]); // as if it had come from the database

        await Manager().WearHelperAsync(wearer, 1000, CancellationToken.None); // 30 points of the 255

        Assert.That(Item.FromDbBytes(wearer.Inventory.AsSpan(Item.SlotHelper * 16, 16)).Durability, Is.EqualTo(255), "not in the blob yet");

        wearer.SyncInventory();

        Assert.That(Item.FromDbBytes(wearer.Inventory.AsSpan(Item.SlotHelper * 16, 16)).Durability, Is.EqualTo(225));
    }

    [Test]
    public void The_hit_reports_the_blow_the_pet_wears_with_before_the_server_rates()
    {
        ItemCombatMath.Options = LoadRules();
        CombatRules.Current = new CombatRules { GeneralDamageRatePvP = 50 };

        try
        {
            var attacker = DarkKnight(strength: 400);
            attacker.RecalcCombatStats(Items(), Balance());
            var target = DarkKnight();
            target.RecalcCombatStats(Items(), Balance());

            var hit = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, Items(), new ScriptedRandom(99, 0, 99, 99), null);

            Assert.That(hit.TargetWearDamage, Is.GreaterThan(0));
            Assert.That(hit.Damage, Is.EqualTo(hit.TargetWearDamage * 50 / 100), "the rates come after");
        }
        finally
        {
            CombatRules.Current = new CombatRules();
        }
    }
}
