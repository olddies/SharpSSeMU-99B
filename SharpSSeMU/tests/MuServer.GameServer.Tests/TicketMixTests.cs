using MuServer.GameServer.Config;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The Chaos Machine mixes for the event tickets: Devil's Invitation and Invisibility Cloak.</summary>
public class TicketMixTests
{
    private static readonly int Chaos = Item.GetItem(12, 15);
    private static readonly int DevilEye = Item.GetItem(14, 17);
    private static readonly int DevilKey = Item.GetItem(14, 18);
    private static readonly int Scroll = Item.GetItem(13, 16);
    private static readonly int Bone = Item.GetItem(13, 17);
    private static readonly int DevilInvitation = Item.GetItem(14, 19);
    private static readonly int Cloak = Item.GetItem(13, 18);

    private static GameServerInfoChaosMix Rates(int rate)
    {
        var rates = new GameServerInfoChaosMix();

        foreach (var table in new[]
        {
            rates.DevilSquareMixRate1, rates.DevilSquareMixRate2, rates.DevilSquareMixRate3, rates.DevilSquareMixRate4,
            rates.BloodCastleMixRate1, rates.BloodCastleMixRate2, rates.BloodCastleMixRate3, rates.BloodCastleMixRate4,
            rates.BloodCastleMixRate5, rates.BloodCastleMixRate6, rates.BloodCastleMixRate7,
        })
        {
            table[0] = rate;
        }

        return rates;
    }

    private static PlayerObject Box(uint money, params (int Index, byte Level)[] items)
    {
        var player = DarkKnight();
        player.Money = money;
        player.InChaosBox = true;

        for (int n = 0; n < items.Length; n++)
        {
            player.ChaosBoxItems[n] = Make(items[n].Index, level: items[n].Level, durability: 1);
        }

        return player;
    }

    private static ChaosMixResult Mix(PlayerObject player, ChaosMixType type, int rate, bool execute = true) =>
        ChaosMixLogic.CalculateAndExecuteMix(player, type, Rates(rate), new int[4], _ => 1000, execute);

    // ---------------------------------------------------------------- Devil's Invitation

    [Test]
    public void Chaos_eye_and_key_of_the_same_level_make_the_invitation_of_that_level()
    {
        foreach (var (level, zen) in new[] { (1, 100_000u), (2, 200_000u), (3, 400_000u), (4, 700_000u) })
        {
            var player = Box(1_000_000, (Chaos, 0), (DevilEye, (byte)level), (DevilKey, (byte)level));
            var result = Mix(player, ChaosMixType.DevilSquare, rate: 100);

            Assert.That(result.Success, Is.True, $"level {level}");
            Assert.That((int)result.Item!.Index, Is.EqualTo(DevilInvitation));
            Assert.That(result.Item.Level, Is.EqualTo(level));
            Assert.That(result.Item.Durability, Is.EqualTo(1));
            Assert.That(result.DeliverViaInventory, Is.True);
            Assert.That(result.RequiredZen, Is.EqualTo((int)zen));
            Assert.That(player.Money, Is.EqualTo(1_000_000 - zen), "the zen is charged");
            Assert.That(player.ChaosBoxItems.Any(i => i.IsItem()), Is.False, "the box is emptied");
        }
    }

    [Test]
    public void A_failed_mix_still_costs_the_zen_and_the_pieces()
    {
        var player = Box(1_000_000, (Chaos, 0), (DevilEye, 1), (DevilKey, 1));
        var result = Mix(player, ChaosMixType.DevilSquare, rate: 0);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ResultCode, Is.EqualTo(0));
        Assert.That(player.Money, Is.EqualTo(900_000u));
        Assert.That(player.ChaosBoxItems.Any(i => i.IsItem()), Is.False);
    }

    [Test]
    public void The_wrong_pieces_the_wrong_levels_or_too_little_zen_stop_the_mix_without_charging()
    {
        // The pieces are not the right ones, or their levels differ.
        Assert.That(Mix(Box(1_000_000, (Chaos, 0), (DevilEye, 1)), ChaosMixType.DevilSquare, 100).ResultCode, Is.EqualTo(7));
        Assert.That(Mix(Box(1_000_000, (Chaos, 0), (DevilEye, 1), (DevilKey, 2)), ChaosMixType.DevilSquare, 100).ResultCode, Is.EqualTo(7));
        Assert.That(Mix(Box(1_000_000, (Chaos, 0), (Chaos, 0), (DevilEye, 1), (DevilKey, 1)), ChaosMixType.DevilSquare, 100).ResultCode, Is.EqualTo(7));

        // Above the highest level of the event.
        Assert.That(Mix(Box(1_000_000, (Chaos, 0), (DevilEye, 5), (DevilKey, 5)), ChaosMixType.DevilSquare, 100).ResultCode, Is.EqualTo(8));

        // Not enough zen: nothing is taken.
        var poor = Box(99_999, (Chaos, 0), (DevilEye, 1), (DevilKey, 1));
        Assert.That(Mix(poor, ChaosMixType.DevilSquare, 100).ResultCode, Is.EqualTo(2));
        Assert.That(poor.Money, Is.EqualTo(99_999u));
        Assert.That(poor.ChaosBoxItems.Count(i => i.IsItem()), Is.EqualTo(3));
    }

    [Test]
    public void Asking_for_the_rate_charges_nothing()
    {
        var player = Box(1_000_000, (Chaos, 0), (DevilEye, 2), (DevilKey, 2));
        var result = Mix(player, ChaosMixType.DevilSquare, rate: 60, execute: false);

        Assert.That((result.SuccessRate, result.RequiredZen), Is.EqualTo((60, 200_000)));
        Assert.That(player.Money, Is.EqualTo(1_000_000u));
        Assert.That(player.ChaosBoxItems.Count(i => i.IsItem()), Is.EqualTo(3));
    }

    // ---------------------------------------------------------------- Invisibility Cloak

    [Test]
    public void Chaos_scroll_and_bone_of_the_same_level_make_the_cloak_of_that_level()
    {
        foreach (var (level, zen) in new[] { (1, 50_000u), (2, 80_000u), (3, 150_000u), (4, 250_000u), (5, 400_000u), (6, 600_000u), (7, 850_000u) })
        {
            var player = Box(1_000_000, (Chaos, 0), (Scroll, (byte)level), (Bone, (byte)level));
            var result = Mix(player, ChaosMixType.BloodCastle, rate: 100);

            Assert.That(result.Success, Is.True, $"level {level}");
            Assert.That((int)result.Item!.Index, Is.EqualTo(Cloak));
            Assert.That(result.Item.Level, Is.EqualTo(level));
            Assert.That(player.Money, Is.EqualTo(1_000_000 - zen));
        }
    }

    [Test]
    public void The_cloak_mix_has_its_own_checks()
    {
        Assert.That(Mix(Box(1_000_000, (Chaos, 0), (Scroll, 3), (Bone, 4)), ChaosMixType.BloodCastle, 100).ResultCode, Is.EqualTo(7));
        Assert.That(Mix(Box(1_000_000, (Chaos, 0), (Scroll, 8), (Bone, 8)), ChaosMixType.BloodCastle, 100).ResultCode, Is.EqualTo(8));
        Assert.That(Mix(Box(10, (Chaos, 0), (Scroll, 1), (Bone, 1)), ChaosMixType.BloodCastle, 100).ResultCode, Is.EqualTo(2));

        // The Devil Square pieces do not make a cloak, and the other way round.
        Assert.That(Mix(Box(1_000_000, (Chaos, 0), (DevilEye, 1), (DevilKey, 1)), ChaosMixType.BloodCastle, 100).ResultCode, Is.EqualTo(7));
        Assert.That(Mix(Box(1_000_000, (Chaos, 0), (Scroll, 1), (Bone, 1)), ChaosMixType.DevilSquare, 100).ResultCode, Is.EqualTo(7));
    }
}
