using System.Numerics;
using MuServer.GameServer.Config;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The Dinorant mix of the Chaos Machine: a Jewel of Chaos and ten Horns of Uniria make a Horn of Dinorant.</summary>
public class DinorantMixTests
{
    private static readonly int Chaos = Item.GetItem(12, 15);
    private static readonly int Uniria = Item.GetItem(13, 2);
    private static readonly int Dinorant = Item.GetItem(13, 3);

    private static GameServerInfoChaosMix Rates(int rate)
    {
        var rates = new GameServerInfoChaosMix();
        rates.DinorantMixRate[0] = rate;
        return rates;
    }

    /// <summary>The bags of the server with only what the mix needs: row 3 of the excellent counts always gives two.</summary>
    private static ItemBagManager Bags(int excellentCount)
    {
        var optionRates = new ItemOptionRateTable();
        var chances = new int[7];
        chances[excellentCount] = 10_000;
        optionRates.Add(ItemOptionRateTable.ExcellentCount, 3, chances);

        var excellent = new ExcellentOptionRateTable();

        foreach (int bit in new[] { 1, 2, 4, 8, 16, 32 })
        {
            excellent.Add(new ExcellentOptionRow(bit, Dinorant, Dinorant, "dinorant", 50));
        }

        return new ItemBagManager(new ItemBagItemMaker(Items(), optionRates, excellent));
    }

    private static PlayerObject Box(uint money, int unirias = 10, byte uniriaDurability = 255, int chaos = 1)
    {
        var player = DarkKnight();
        player.Money = money;
        player.InChaosBox = true;
        int slot = 0;

        for (int n = 0; n < chaos; n++)
        {
            player.ChaosBoxItems[slot++] = Make(Chaos, level: 0, durability: 1);
        }

        for (int n = 0; n < unirias; n++)
        {
            player.ChaosBoxItems[slot++] = Make(Uniria, level: 0, durability: uniriaDurability);
        }

        return player;
    }

    private static ChaosMixResult Mix(PlayerObject player, int rate, ItemBagManager? bags = null, bool execute = true) =>
        ChaosMixLogic.CalculateAndExecuteMix(player, ChaosMixType.Dinorant, Rates(rate), new int[4], _ => 1000, execute, bags);

    [Test]
    public void Ten_unirias_and_a_chaos_make_a_dinorant_with_options_and_full_durability()
    {
        var player = Box(1_000_000);
        var result = Mix(player, rate: 100, Bags(excellentCount: 2));

        Assert.That(result.Success, Is.True);
        Assert.That((int)result.Item!.Index, Is.EqualTo(Dinorant));
        Assert.That(result.Item.Durability, Is.EqualTo(255));
        Assert.That(result.Item.Level, Is.EqualTo(0));
        Assert.That(BitOperations.PopCount(result.Item.NewOption), Is.EqualTo(2), "two excellent options, as the rates row says");
        Assert.That(result.DeliverViaInventory, Is.True);
        Assert.That(result.RequiredZen, Is.EqualTo(500_000));
        Assert.That(player.Money, Is.EqualTo(500_000u));
        Assert.That(player.ChaosBoxItems.Any(i => i.IsItem()), Is.False, "the box is emptied");
    }

    [Test]
    public void The_dinorant_never_carries_more_than_three_excellent_options()
    {
        var result = Mix(Box(1_000_000), rate: 100, Bags(excellentCount: 6));

        Assert.That(BitOperations.PopCount(result.Item!.NewOption), Is.EqualTo(3));
    }

    [Test]
    public void Without_the_bags_the_horn_comes_plain()
    {
        var result = Mix(Box(1_000_000), rate: 100);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Item!.NewOption, Is.EqualTo(0));
    }

    [Test]
    public void A_failed_mix_still_costs_the_zen_and_the_pieces()
    {
        var player = Box(1_000_000);
        var result = Mix(player, rate: 0, Bags(2));

        Assert.That(result.Success, Is.False);
        Assert.That(result.ResultCode, Is.EqualTo(0));
        Assert.That(player.Money, Is.EqualTo(500_000u));
        Assert.That(player.ChaosBoxItems.Any(i => i.IsItem()), Is.False);
    }

    [Test]
    public void Wrong_pieces_or_too_little_zen_stop_the_mix_without_charging()
    {
        // Nine horns, eleven horns, a horn worn down, no chaos jewel, two of them.
        Assert.That(Mix(Box(1_000_000, unirias: 9), 100).ResultCode, Is.EqualTo(7));
        Assert.That(Mix(Box(1_000_000, unirias: 11), 100).ResultCode, Is.EqualTo(7));
        Assert.That(Mix(Box(1_000_000, uniriaDurability: 254), 100).ResultCode, Is.EqualTo(7));
        Assert.That(Mix(Box(1_000_000, chaos: 0), 100).ResultCode, Is.EqualTo(7));
        Assert.That(Mix(Box(1_000_000, chaos: 2, unirias: 10), 100).ResultCode, Is.EqualTo(7));

        var poor = Box(499_999);
        Assert.That(Mix(poor, 100).ResultCode, Is.EqualTo(2));
        Assert.That(poor.Money, Is.EqualTo(499_999u));
        Assert.That(poor.ChaosBoxItems.Count(i => i.IsItem()), Is.EqualTo(11), "nothing is taken");
    }

    [Test]
    public void Asking_for_the_rate_charges_nothing_and_caps_it_at_a_hundred()
    {
        var player = Box(1_000_000);
        var result = Mix(player, rate: 70, execute: false);

        Assert.That((result.SuccessRate, result.RequiredZen), Is.EqualTo((70, 500_000)));
        Assert.That(player.Money, Is.EqualTo(1_000_000u));
        Assert.That(player.ChaosBoxItems.Count(i => i.IsItem()), Is.EqualTo(11));

        Assert.That(Mix(Box(1_000_000), rate: 250, execute: false).SuccessRate, Is.EqualTo(100));
    }
}
