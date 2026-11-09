using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The Dark Horse and the Dark Raven as items that grow: the experience table, the level and experience of a
/// pet, what they ask of the player, what they give back in defense and damage taken, the packets that carry them and
/// the share of a kill's experience they get. Expected values are worked out by hand from Item.cpp, User.cpp and
/// ObjectManager.cpp.</summary>
public class PetTests
{
    // PetExperienceMultiplierConstA 100, MaxPetLevel 50, PetExperienceRateDivisor 10: the shipped values.
    private static PetRules Rules() => new(100, 50, 10);

    private static Item Horse(byte durability = 255, bool loaded = true, int level = 1, uint experience = 0)
    {
        var horse = Make(Item.DarkHorse, durability: durability);
        horse.Serial = 7001;

        if (loaded)
        {
            horse.SetPetItemInfo(level, experience, Rules());
        }

        return horse;
    }

    private static Item Raven(byte durability = 255, bool loaded = true, int level = 1, uint experience = 0)
    {
        var raven = Make(Item.DarkRaven, durability: durability);
        raven.Serial = 7002;

        if (loaded)
        {
            raven.SetPetItemInfo(level, experience, Rules());
        }

        return raven;
    }

    private static ItemBalanceTable PetItems()
    {
        var items = Items();
        items.Add(new ItemBalance { Index = Item.DarkHorse, Section = 13, Sub = 4, Slot = 8, Level = 218, RequireLevel = 218, Durability = 255 });
        items.Add(new ItemBalance { Index = Item.DarkRaven, Section = 13, Sub = 5, Slot = 1, Level = 0, Durability = 255 });
        return items;
    }

    /// <summary>A Dark Lord with the level and command a pet of level 10 asks for.</summary>
    private static PlayerObject Lord(int level = 300, uint leadership = 500)
    {
        return new PlayerObject
        {
            Index = 9000, Session = FakeSession.Create(), Account = "acc", Name = "Lord", Class = 4, Level = (ushort)level,
            Strength = 100, Dexterity = 20, Vitality = 25, Energy = 30, Leadership = leadership, WorldEntered = true,
        };
    }

    // ---------------------------------------------------------------- the experience table

    [Test]
    public void Pet_experience_starts_at_level_three_and_grows_with_the_cube()
    {
        var rules = Rules();

        Assert.That(new[] { rules.Experience(0), rules.Experience(1), rules.Experience(2) }, Is.EqualTo(new uint[] { 0, 0, 0 }));
        Assert.That(rules.Experience(3), Is.EqualTo(13u * 3 * 3 * 3 * 100));
        Assert.That(rules.Experience(10), Is.EqualTo(20u * 10 * 10 * 10 * 100));
        Assert.That(rules.Experience(50), Is.EqualTo(60u * 50 * 50 * 50 * 100));
    }

    [Test]
    public void The_divisor_of_zero_in_the_file_is_read_as_one()
    {
        Assert.That(new PetRules(100, 50, 0).RateDivisor, Is.EqualTo(1));
    }

    // ---------------------------------------------------------------- a pet's level and experience

    [Test]
    public void A_pet_takes_the_level_and_experience_it_is_given_inside_the_limits()
    {
        var rules = Rules();
        var horse = Horse(loaded: false);

        horse.SetPetItemInfo(60, 0, rules);
        Assert.That(horse.PetLevel, Is.EqualTo(50), "capped at the server's maximum");
        Assert.That(horse.PetExp, Is.EqualTo(rules.Experience(50)), "and its experience raised to where that level starts");

        horse.SetPetItemInfo(5, rules.Experience(5) + 10, rules);
        Assert.That((horse.PetLevel, horse.PetExp), Is.EqualTo((5, rules.Experience(5) + 10)));

        horse.SetPetItemInfo(5, rules.Experience(6) + 5, rules);
        Assert.That(horse.PetExp, Is.EqualTo(rules.Experience(5)), "experience for the next level in this one goes back to the start");
        Assert.That(horse.PetInfoLoaded, Is.True);
    }

    [Test]
    public void Only_the_two_pets_take_pet_data()
    {
        var kris = Make(Kris);
        kris.SetPetItemInfo(10, 0, Rules());

        Assert.That(kris.PetInfoLoaded, Is.False);
        Assert.That(kris.IsPetItem, Is.False);
    }

    [Test]
    public void A_pet_gains_a_tenth_of_what_it_is_given_and_goes_up_when_it_reaches_the_next_level()
    {
        var rules = Rules();
        var horse = Horse(level: 3, experience: rules.Experience(3));

        Assert.That(horse.AddPetExp(1000, rules), Is.False);
        Assert.That(horse.PetExp, Is.EqualTo(rules.Experience(3) + 100), "1000 / PetExperienceRateDivisor");

        // Enough to reach level 4: it goes up and starts at the beginning of the new level.
        uint needed = rules.Experience(4) - horse.PetExp;
        Assert.That(horse.AddPetExp((int)(needed * 10), rules), Is.True);
        Assert.That((horse.PetLevel, horse.PetExp), Is.EqualTo((4, rules.Experience(4))));
    }

    [Test]
    public void A_pet_without_durability_or_data_gains_nothing()
    {
        var rules = Rules();

        var broken = Horse(durability: 0);
        Assert.That(broken.AddPetExp(100_000, rules), Is.False);
        Assert.That(broken.PetExp, Is.EqualTo(0u));

        var unknown = Horse(loaded: false);
        Assert.That(unknown.AddPetExp(100_000, rules), Is.False);
        Assert.That(unknown.PetExp, Is.EqualTo(0u), "until the DataServer has told it where it stands");
    }

    [Test]
    public void A_pet_at_the_maximum_level_gains_nothing()
    {
        var rules = Rules();
        var horse = Horse(level: 50, experience: rules.Experience(50));

        Assert.That(horse.AddPetExp(1_000_000, rules), Is.False);
        Assert.That(horse.PetExp, Is.EqualTo(rules.Experience(50)));
    }

    [Test]
    public void Taking_the_experience_back_can_take_the_level_back()
    {
        var rules = Rules();
        var horse = Horse(level: 4, experience: rules.Experience(4) + 5);

        Assert.That(horse.DecPetExp(30, rules), Is.False, "3 points less, still level 4");
        Assert.That(horse.PetExp, Is.EqualTo(rules.Experience(4) + 2));

        Assert.That(horse.DecPetExp(30, rules), Is.True, "3 points less, under where level 4 starts");
        Assert.That((horse.PetLevel, horse.PetExp), Is.EqualTo((3, rules.Experience(4) - 1)));
    }

    // ---------------------------------------------------------------- what a pet asks of the player

    [Test]
    public void The_dark_horse_asks_for_218_levels_and_two_more_for_every_level_it_has()
    {
        var items = PetItems();
        var info = items.Get(Item.DarkHorse)!;

        Assert.That(ItemCombatMath.GetRequireLevel(Horse(level: 1), info), Is.EqualTo(220));
        Assert.That(ItemCombatMath.GetRequireLevel(Horse(level: 10, experience: Rules().Experience(10)), info), Is.EqualTo(238));
    }

    [Test]
    public void The_dark_raven_asks_for_185_command_and_fifteen_more_for_every_level()
    {
        var info = PetItems().Get(Item.DarkRaven)!;

        Assert.That(ItemCombatMath.GetRequireLeadership(Raven(level: 1), info), Is.EqualTo(200));
        Assert.That(ItemCombatMath.GetRequireLeadership(Raven(level: 10, experience: Rules().Experience(10)), info), Is.EqualTo(335));
    }

    [Test]
    public void A_pet_is_valid_while_the_player_meets_what_it_asks_and_it_has_durability()
    {
        var items = PetItems();
        var info = items.Get(Item.DarkHorse)!;

        Assert.That(ItemCombatMath.IsValidItem(Lord(level: 220), Horse(level: 1), info), Is.True);
        Assert.That(ItemCombatMath.IsValidItem(Lord(level: 219), Horse(level: 1), info), Is.False, "one level short");
        Assert.That(ItemCombatMath.IsValidItem(Lord(level: 300), Horse(durability: 0), info), Is.False);
    }

    // ---------------------------------------------------------------- what a pet gives

    [Test]
    public void The_dark_horse_on_the_target_takes_a_share_that_grows_with_its_level()
    {
        CombatRules.Current = new CombatRules(); // 10 and 3 -- the shipped constants
        var target = DarkKnight();
        target.Items[Item.SlotHelper] = Horse(level: 1);
        Assert.That(PlayerAttackMath.ReduceMonsterHit(target, 100), Is.EqualTo(100 * (100 - (10 + 1) / 3) / 100));

        target.Items[Item.SlotHelper] = Horse(level: 50, experience: Rules().Experience(50));
        Assert.That(PlayerAttackMath.ReduceMonsterHit(target, 100), Is.EqualTo(100 * (100 - (10 + 50) / 3) / 100));

        target.Items[Item.SlotHelper] = Horse(durability: 0);
        Assert.That(PlayerAttackMath.ReduceMonsterHit(target, 100), Is.EqualTo(100), "a horse with no durability left takes none");
    }

    [Test]
    public void The_dark_horse_adds_to_the_defense_a_twentieth_of_the_dexterity_two_per_level_and_five()
    {
        var items = PetItems();
        var lord = DarkKnight();
        lord.Class = 4;
        lord.Dexterity = 200;
        lord.RecalcCombatStats(items, Balance());
        int bare = lord.Defense;

        lord.Items[Item.SlotHelper] = Horse(level: 10, experience: Rules().Experience(10));
        lord.RecalcCombatStats(items, Balance());

        Assert.That(lord.Defense, Is.EqualTo(bare + 200 / 20 + 10 * 2 + 5));

        lord.Items[Item.SlotHelper] = Horse(durability: 0);
        lord.RecalcCombatStats(items, Balance());
        Assert.That(lord.Defense, Is.EqualTo(bare), "and nothing once it has no durability");
    }

    // ---------------------------------------------------------------- the packets

    [Test]
    public void The_pet_query_answer_is_twelve_bytes_with_the_level_and_the_experience()
    {
        var packet = QuestPacketBuilder.PetItemInfoSend(1, 0xFE, 8, 7, 123_456);

        Assert.That(packet.Length, Is.EqualTo(12));
        Assert.That(packet.Take(8).ToArray(), Is.EqualTo(new byte[] { 0xC1, 12, 0xA9, 1, 0xFE, 8, 7, 0 }), "type, flag, slot, level and a padding byte");
        Assert.That(BitConverter.ToUInt32(packet, 8), Is.EqualTo(123_456u));
    }

    [Test]
    public void The_request_and_the_save_to_the_dataserver_are_laid_out_as_it_reads_them()
    {
        var request = DataServerCharacterPacketBuilder.PetItemInfoRequest(9000, "acc", 1, new List<(byte, uint)> { (8, 7001), (1, 7002) });

        Assert.That(request[0], Is.EqualTo(0xC2));
        Assert.That(request[3], Is.EqualTo(0x09));
        Assert.That(BitConverter.ToUInt16(request, 4), Is.EqualTo(9000));
        Assert.That(System.Text.Encoding.ASCII.GetString(request, 6, 11).TrimEnd('\0'), Is.EqualTo("acc"));
        Assert.That((request[17], request[18]), Is.EqualTo(((byte)1, (byte)2)), "type and count");
        Assert.That((request[19], BitConverter.ToUInt32(request, 20)), Is.EqualTo(((byte)8, 7001u)));
        Assert.That((request[24], BitConverter.ToUInt32(request, 25)), Is.EqualTo(((byte)1, 7002u)));

        var save = DataServerCharacterPacketBuilder.PetItemInfoSave(9000, "acc", new List<(uint, byte, uint)> { (7001, 12, 555_000) });

        Assert.That(save[3], Is.EqualTo(0x34));
        Assert.That(save[17], Is.EqualTo(1), "count");
        Assert.That((BitConverter.ToUInt32(save, 18), save[22], BitConverter.ToUInt32(save, 23)), Is.EqualTo((7001u, (byte)12, 555_000u)));
    }

    [Test]
    public void The_dataserver_answer_is_read_back()
    {
        var w = new MuServer.Shared.Protocol.PacketWriter();
        w.WriteUInt16(9000);
        w.WriteFixedString("acc", 11);
        w.WriteByte(0);
        w.WriteByte(2);
        w.WriteByte(8); w.WriteUInt32(7001); w.WriteByte(12); w.WriteUInt32(555_000);
        w.WriteByte(1); w.WriteUInt32(7002); w.WriteByte(3); w.WriteUInt32(0);
        var packet = MuServer.Shared.Protocol.PacketBuilder.BuildC2(0x09, w.ToArray());

        var info = PetItemInfoFromDataServer.Parse(packet)!;

        Assert.That((info.Index, info.Account, info.Type), Is.EqualTo(((ushort)9000, "acc", (byte)0)));
        Assert.That(info.Pets, Is.EqualTo(new[] { ((byte)8, 7001u, (byte)12, 555_000u), ((byte)1, 7002u, (byte)3, 0u) }));
    }

    [Test]
    public void A_stored_item_keeps_its_serial_and_options_through_the_warehouse_save()
    {
        var player = Lord();
        var sword = Make(Kris, level: 5, excellent: 0x10, durability: 30);
        sword.Serial = 424_242;
        player.WarehouseItems[7] = sword;

        var packet = DataServerCharacterPacketBuilder.WarehouseSaveRequest(9000, "acc", player);
        const int firstSlot = 5 + 2 + 10; // C2 header and sub head, the index and the account

        var back = Item.FromDbBytes(packet.AsSpan(firstSlot + (7 * 16), 16));

        Assert.That((back.Index, back.Level, back.Durability, back.NewOption, back.Serial),
            Is.EqualTo(((short)Kris, (byte)5, (byte)30, (byte)0x10, 424_242u)));
        Assert.That(Item.FromDbBytes(packet.AsSpan(firstSlot, 16)).IsItem(), Is.False, "and an empty slot stays empty");
    }

    // ---------------------------------------------------------------- the pet manager

    [Test]
    public void The_pets_to_ask_and_to_save_are_picked_by_their_data()
    {
        var container = new Item[10];
        Array.Fill(container, Item.Empty());
        container[2] = Horse();
        container[5] = Raven(loaded: false);
        container[7] = Make(Kris);

        Assert.That(PetManager.PetsToRequest(container), Is.EqualTo(new[] { ((byte)2, 7001u), ((byte)5, 7002u) }));
        Assert.That(PetManager.PetsToSave(container), Is.EqualTo(new[] { (7001u, (byte)1, 0u) }), "the raven the dataserver never answered for stays out");
    }

    [Test]
    public void The_answer_goes_to_the_pet_with_that_serial_and_a_worn_one_asks_for_the_stats_again()
    {
        var manager = new PetManager(PetItems(), Rules);
        var player = Lord();
        player.Items[Item.SlotHelper] = Horse(loaded: false);
        player.Items[20] = Raven(loaded: false);
        var rules = Rules();

        bool worn = manager.ApplyInfo(player, 0, new[]
        {
            ((byte)Item.SlotHelper, 7001u, (byte)6, rules.Experience(6) + 40),
            ((byte)20, 9999u, (byte)9, 0u), // another serial: not this item any more
        });

        Assert.That(worn, Is.True);
        Assert.That((player.Items[Item.SlotHelper].PetLevel, player.Items[Item.SlotHelper].PetExp), Is.EqualTo((6, rules.Experience(6) + 40)));
        Assert.That(player.Items[20].PetInfoLoaded, Is.False);

        Assert.That(manager.ApplyInfo(player, 0, new[] { ((byte)20, 7002u, (byte)2, 0u) }), Is.False, "one in the backpack changes nothing worn");
        Assert.That(player.Items[20].PetInfoLoaded, Is.True);
    }

    [Test]
    public async Task A_kill_gives_the_worn_pets_their_share_and_tells_the_client()
    {
        var manager = new PetManager(PetItems(), Rules);
        var player = Lord(level: 300, leadership: 500);
        player.Items[Item.SlotHelper] = Horse(level: 3, experience: Rules().Experience(3));
        player.Items[Item.SlotWeapon2] = Raven(level: 2, experience: 0); // levels 1 and 2 both start at nothing: 1 goes to 2 at once
        var session = (FakeSession)player.Session;

        await manager.GrantExperienceAsync(player, 2000, CancellationToken.None);

        Assert.That(player.Items[Item.SlotHelper].PetExp, Is.EqualTo(Rules().Experience(3) + 200));
        Assert.That(player.Items[Item.SlotWeapon2].PetExp, Is.EqualTo(200u));

        var answers = session.Of(0xA9);
        Assert.That(answers, Has.Count.EqualTo(2));
        Assert.That((answers[0][3], answers[0][4], answers[0][5], answers[0][6]), Is.EqualTo(((byte)1, (byte)0, (byte)8, (byte)3)), "the horse: type 1, slot 8, level 3");
        Assert.That((answers[1][3], answers[1][4], answers[1][5], answers[1][6]), Is.EqualTo(((byte)0, (byte)0, (byte)1, (byte)2)), "the raven: type 0, slot 1, level 2");
    }

    [Test]
    public async Task A_new_pet_goes_from_level_one_to_two_on_its_first_experience()
    {
        // gPetExperience[1] and [2] are both 0 in the original, so level 2 is reached with the first point.
        var manager = new PetManager(PetItems(), Rules);
        var player = Lord(level: 300);
        player.Items[Item.SlotHelper] = Horse(level: 1);
        var session = (FakeSession)player.Session;

        await manager.GrantExperienceAsync(player, 100, CancellationToken.None);

        Assert.That((player.Items[Item.SlotHelper].PetLevel, player.Items[Item.SlotHelper].PetExp), Is.EqualTo((2, 0u)));
        Assert.That(session.Of(0xA9).Single()[4], Is.EqualTo(0xFE));
    }

    [Test]
    public async Task A_pet_that_goes_up_a_level_the_player_still_qualifies_for_says_so()
    {
        var manager = new PetManager(PetItems(), Rules);
        var player = Lord(level: 300);
        var rules = Rules();
        player.Items[Item.SlotHelper] = Horse(level: 3, experience: rules.Experience(4) - 1);
        var session = (FakeSession)player.Session;

        await manager.GrantExperienceAsync(player, 100, CancellationToken.None);

        var answer = session.Of(0xA9).Single();
        Assert.That(answer[4], Is.EqualTo(0xFE), "the level-up flag");
        Assert.That(answer[6], Is.EqualTo(4));
        Assert.That(player.Items[Item.SlotHelper].PetLevel, Is.EqualTo(4));
    }

    [Test]
    public async Task A_pet_that_would_outgrow_the_player_gives_the_level_back()
    {
        var manager = new PetManager(PetItems(), Rules);
        var rules = Rules();
        var player = Lord(level: 223);
        player.Items[Item.SlotHelper] = Horse(level: 2, experience: rules.Experience(3) - 1); // level 2 asks 222, level 3 asks 224
        var session = (FakeSession)player.Session;

        await manager.GrantExperienceAsync(player, 100, CancellationToken.None);

        var horse = player.Items[Item.SlotHelper];
        Assert.That(horse.PetLevel, Is.EqualTo(2), "it stays where it was");
        Assert.That(horse.PetExp, Is.EqualTo(rules.Experience(3) - 1));

        var answer = session.Of(0xA9).Single();
        Assert.That(answer[4], Is.EqualTo(0), "no level-up flag");
        Assert.That(answer[6], Is.EqualTo(2));
    }

    [Test]
    public async Task Nothing_happens_for_a_player_with_no_pets_worn()
    {
        var manager = new PetManager(PetItems(), Rules);
        var player = Lord();
        player.Items[20] = Horse(); // in the backpack: not worn, not growing
        var session = (FakeSession)player.Session;

        await manager.GrantExperienceAsync(player, 5000, CancellationToken.None);

        Assert.That(session.Sent, Is.Empty);
        Assert.That(player.Items[20].PetExp, Is.EqualTo(0u));
    }

    [Test]
    public void A_pet_is_sold_for_a_third_of_two_million_or_one_million_a_level()
    {
        Assert.That(PetManager.SellValue(Horse(level: 3, experience: Rules().Experience(3))), Is.EqualTo(2_000_000L));
        Assert.That(PetManager.SellValue(Raven(level: 6, experience: Rules().Experience(6))), Is.EqualTo(2_000_000L));
    }
}
