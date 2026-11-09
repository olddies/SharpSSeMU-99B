using MuServer.GameServer.Config;
using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The Trainer: the pet mixes of its window (a Jewel of Chaos, the pet's Spirit, Creation, Bless and Soul make a Dark
/// Horse or a Dark Raven of level 1 for five million zen or one) and the repair of pets, which only it does. Worked out by
/// hand from ChaosBox.cpp (PetMix) and ItemManager.cpp (RepairItem).</summary>
public class TrainerTests
{
    private static readonly int Chaos = Item.GetItem(12, 15);
    private static readonly int Spirit = Item.GetItem(13, 31);
    private static readonly int Bless = Item.GetItem(14, 13);
    private static readonly int Soul = Item.GetItem(14, 14);
    private static readonly int Creation = Item.GetItem(14, 22);

    private static GameServerInfoChaosMix Rates(int rate)
    {
        var rates = new GameServerInfoChaosMix();

        for (int n = 0; n < 4; n++)
        {
            rates.PetMixRate[n] = rate;
        }

        return rates;
    }

    private static PlayerObject Player(uint money = 10_000_000)
    {
        var player = DarkKnight();
        player.Money = money;
        player.InTrainer = true;
        return player;
    }

    /// <summary>Puts the ingredients of a pet mix in the box: <paramref name="jewels"/> of Bless and of Soul.</summary>
    private static void Fill(PlayerObject player, byte spiritLevel, int jewels, int chaos = 1, int creation = 1)
    {
        int slot = 0;

        void Put(int index, byte level = 0) => player.ChaosBoxItems[slot++] = Make(index, level: level, durability: 255);

        for (int n = 0; n < chaos; n++) Put(Chaos);
        Put(Spirit, spiritLevel);
        for (int n = 0; n < jewels; n++) { Put(Bless); Put(Soul); }
        for (int n = 0; n < creation; n++) Put(Creation);
    }

    private static ChaosMixResult Mix(PlayerObject player, ChaosMixType type, int rate, bool execute = true) =>
        ChaosMixLogic.CalculateAndExecuteMix(player, type, Rates(rate), new int[4], _ => 0, execute);

    // ---------------------------------------------------------------- the dark horse

    [Test]
    public void The_dark_horse_asks_for_five_of_bless_and_of_soul_and_five_million_zen()
    {
        var player = Player();
        Fill(player, spiritLevel: 0, jewels: 5);

        var result = Mix(player, ChaosMixType.Pet1, rate: 100);

        Assert.That((result.Success, result.ResultCode, result.RequiredZen), Is.EqualTo((true, 100, 5_000_000)));
        Assert.That(result.Item!.Index, Is.EqualTo(Item.DarkHorse));
        Assert.That(result.DeliverViaInventory, Is.False, "the pet is shown in the Trainer's box (result 100), not put in the backpack");
        Assert.That(player.Money, Is.EqualTo(5_000_000u));
        Assert.That(player.ChaosBoxItems.Any(i => i.IsItem()), Is.False, "the ingredients are gone");
    }

    [Test]
    public void The_dark_raven_asks_for_two_and_one_million_zen_and_the_other_spirit()
    {
        var player = Player();
        Fill(player, spiritLevel: 1, jewels: 2);

        var result = Mix(player, ChaosMixType.Pet2, rate: 100);

        Assert.That((result.Success, result.ResultCode, result.RequiredZen), Is.EqualTo((true, 100, 1_000_000)));
        Assert.That(result.Item!.Index, Is.EqualTo(Item.DarkRaven));
        Assert.That(player.Money, Is.EqualTo(9_000_000u));
    }

    [Test]
    public void The_wrong_ingredients_are_refused_with_the_box_untouched()
    {
        var wrongSpirit = Player();
        Fill(wrongSpirit, spiritLevel: 1, jewels: 5); // the raven's spirit for a horse
        Assert.That(Mix(wrongSpirit, ChaosMixType.Pet1, 100).ResultCode, Is.EqualTo(7));

        var fewJewels = Player();
        Fill(fewJewels, spiritLevel: 0, jewels: 4);
        Assert.That(Mix(fewJewels, ChaosMixType.Pet1, 100).ResultCode, Is.EqualTo(7));

        var tooManyChaos = Player();
        Fill(tooManyChaos, spiritLevel: 0, jewels: 5, chaos: 2);
        Assert.That(Mix(tooManyChaos, ChaosMixType.Pet1, 100).ResultCode, Is.EqualTo(7));

        var noCreation = Player();
        Fill(noCreation, spiritLevel: 0, jewels: 5, creation: 0);
        Assert.That(Mix(noCreation, ChaosMixType.Pet1, 100).ResultCode, Is.EqualTo(7));

        Assert.That(noCreation.ChaosBoxItems.Count(i => i.IsItem()), Is.EqualTo(12), "nothing was taken");
        Assert.That(noCreation.Money, Is.EqualTo(10_000_000u));
    }

    [Test]
    public void Without_the_zen_nothing_is_taken()
    {
        var player = Player(money: 4_999_999);
        Fill(player, spiritLevel: 0, jewels: 5);

        var result = Mix(player, ChaosMixType.Pet1, 100);

        Assert.That(result.ResultCode, Is.EqualTo(2));
        Assert.That(player.Money, Is.EqualTo(4_999_999u));
        Assert.That(player.ChaosBoxItems.Count(i => i.IsItem()), Is.EqualTo(13));
    }

    [Test]
    public void A_failed_mix_takes_the_zen_and_the_ingredients_and_gives_nothing()
    {
        var player = Player();
        Fill(player, spiritLevel: 0, jewels: 5);

        var result = Mix(player, ChaosMixType.Pet1, rate: 0);

        Assert.That((result.Success, result.ResultCode, result.Item), Is.EqualTo((false, 0, (Item?)null)));
        Assert.That(player.Money, Is.EqualTo(5_000_000u));
        Assert.That(player.ChaosBoxItems.Any(i => i.IsItem()), Is.False);
    }

    [Test]
    public void Asking_the_rate_does_not_charge_or_take_anything()
    {
        var player = Player();
        Fill(player, spiritLevel: 0, jewels: 5);

        var result = Mix(player, ChaosMixType.Pet1, rate: 60, execute: false);

        Assert.That((result.SuccessRate, result.RequiredZen), Is.EqualTo((60, 5_000_000)));
        Assert.That(player.Money, Is.EqualTo(10_000_000u));
        Assert.That(player.ChaosBoxItems.Count(i => i.IsItem()), Is.EqualTo(13));
    }

    [Test]
    public void A_mix_does_not_close_the_window_for_the_next_one()
    {
        var player = Player();
        Fill(player, spiritLevel: 0, jewels: 5);

        Mix(player, ChaosMixType.Pet1, rate: 0);

        Assert.That(player.InTrainer, Is.True);
    }

    // ---------------------------------------------------------------- the repair

    private static ItemBalanceTable PetItems()
    {
        var items = Items();
        items.Add(new ItemBalance { Index = Item.DarkHorse, Section = 13, Sub = 4, Slot = 8, Level = 218, RequireLevel = 218, Durability = 255 });
        items.Add(new ItemBalance { Index = Item.DarkRaven, Section = 13, Sub = 5, Slot = 1, Level = 0, Durability = 255 });
        items.Add(new ItemBalance { Index = Item.GetItem(13, 0), Section = 13, Sub = 0, Slot = 8, Level = 23, Durability = 255 });
        items.Add(new ItemBalance { Index = Item.GetItem(4, 15), Section = 4, Sub = 15, Slot = 0, Level = 0, Durability = 255 });
        return items;
    }

    [Test]
    public void A_pet_is_repaired_only_at_the_trainer_and_only_the_horse_in_the_helper_slot()
    {
        var items = PetItems();
        var horse = Make(Item.DarkHorse, durability: 10);
        var raven = Make(Item.DarkRaven, durability: 10);
        var angel = Make(Item.GetItem(13, 0), durability: 10);
        var arrows = Make(Item.GetItem(4, 15), durability: 10);

        Assert.That(ItemCombatMath.CanRepair(horse, items.Get(horse.Index)!, atTrainer: false), Is.False, "the horse needs the Trainer");
        Assert.That(ItemCombatMath.CanRepair(horse, items.Get(horse.Index)!, atTrainer: true), Is.True);
        Assert.That(ItemCombatMath.CanRepair(raven, items.Get(raven.Index)!, atTrainer: true), Is.True);
        Assert.That(ItemCombatMath.CanRepair(angel, items.Get(angel.Index)!, atTrainer: true), Is.False, "no other helper is repaired");
        Assert.That(ItemCombatMath.CanRepair(arrows, items.Get(arrows.Index)!, atTrainer: true), Is.False, "nor arrows");
        Assert.That(ItemCombatMath.CanRepair(Make(Kris), items.Get(Kris)!, atTrainer: false), Is.True, "while a weapon always");
    }

    [Test]
    public void A_pet_costs_all_its_price_to_repair_and_double_when_it_has_no_durability_left()
    {
        var items = PetItems();
        var rules = new PetRules(100, 50, 10);
        var horse = Make(Item.DarkHorse, durability: 0);
        horse.SetPetItemInfo(2, rules.Experience(2), rules);
        var info = items.Get(horse.Index)!;

        // A level-2 horse is worth 4,000,000: sqrt = 2000, its root 44.72 -> (3 * 2000 * 44.72) * (1 - 0/255) + 1, twice.
        int expected = (int)(((3.0 * Math.Sqrt(4_000_000) * Math.Sqrt(Math.Sqrt(4_000_000))) + 1.0) * 2.0);
        expected = expected >= 1000 ? (expected / 100) * 100 : (expected / 10) * 10;

        Assert.That(ItemCombatMath.GetItemRepairMoney(horse, 0, info), Is.EqualTo(expected));

        horse.Durability = 255;
        Assert.That(ItemCombatMath.GetItemRepairMoney(horse, 0, info), Is.EqualTo(0), "nothing to repair");
    }

    [Test]
    public void The_trainers_box_list_has_its_own_type()
    {
        var items = new List<Item> { Make(Chaos) };

        Assert.That(ChaosBoxPacketBuilder.ChaosBoxItemListSend(items)[4], Is.EqualTo(3), "after the C2 header: the Chaos Machine's list");
        Assert.That(ChaosBoxPacketBuilder.ChaosBoxItemListSend(items, 5)[4], Is.EqualTo(5), "the Trainer's");
    }
}
