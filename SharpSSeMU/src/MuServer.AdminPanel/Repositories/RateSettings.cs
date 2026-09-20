namespace MuServer.AdminPanel.Repositories;

/// <summary>A setting of the Rates page. <see cref="PerAccountLevel"/> marks the keys that are repeated per
/// account level in the file (<c>_AL0</c>..<c>_AL3</c>): they are shown as a single field and written to all 4,
/// because the server has no premium-account system (the account level is always 0) and leaving the other 3
/// with different values only causes confusion later on. Whoever needs the 4 separately has them on the
/// Configuration page.</summary>
public sealed record RateSetting(string Key, string Label, string Help, bool PerAccountLevel = false);

public sealed record RateGroup(string Title, string Icon, RateSetting[] Settings);

/// <summary>The settings an admin really touches to configure their server, picked by hand from
/// <c>GameServerInfo - Common.dat</c> with a name and explanation. It does not replace the Configuration page
/// (which shows the ~845 raw fields): it is the shortcut to what is always used. </summary>
public static class RateSettings
{
    public const string FileName = "GameServerInfo - Common.dat";
    public const string Section = "GameServerInfo";

    public static readonly RateGroup[] Groups =
    [
        new("Experience", "exp",
        [
            new("AddExperienceRate", "Experience multiplier",
                "The classic \"x10 server\": multiplies the experience of every monster. 1 = normal.", true),
            new("AddEventExperienceRate", "Event experience multiplier",
                "Same as the previous one but for events (Devil Square, Blood Castle...).", true),
            new("MaxLevel", "Maximum level",
                "Character level cap. On reaching it, experience stops accumulating."),
            new("MaxLevelUp", "Maximum levels per experience gain",
                "How many levels can be gained at once. Important on servers with very high exp."),
            new("MaxLevelUpEvent", "Maximum levels per gain (events)",
                "The same, but for experience earned in events."),
            new("ExperienceMultiplierConstA", "Experience formula constant A",
                "Part of the original formula that computes how much experience each monster gives. Only touch it if you know what it does."),
            new("ExperienceMultiplierConstB", "Experience formula constant B",
                "Ditto: it is the other constant of the original formula."),
        ]),

        new("Item drops", "drop",
        [
            new("ItemDropRate", "Item drop probability",
                "Chance that a monster drops an item on dying (out of 1000: 100 = 10%).", true),
            new("ItemDropTime", "Seconds an item stays on the floor",
                "After that time the item disappears from the ground."),
            new("MaxItemOption", "Maximum item options",
                "Cap on the additional options a dropped item can have."),
        ]),

        new("Zen drops", "zen",
        [
            new("MoneyAmountDropRate", "Zen multiplier",
                "How much zen each monster drops, as a percentage: 100 = normal, 200 = double.", true),
            new("MoneyDropTime", "Seconds zen stays on the floor",
                "Same as for items, but for zen piles."),
        ]),

        new("Jewels and item upgrades", "jewel",
        [
            new("SoulSuccessRate", "Jewel of Soul success (%)",
                "Probability that the jewel raises the item's level instead of breaking it.", true),
            new("LifeSuccessRate", "Jewel of Life success (%)",
                "Probability that the Jewel of Life adds option level.", true),
            new("AddLuckSuccessRate1", "Luck success 1 (%)",
                "Base probability of adding the Luck attribute.", true),
            new("AddLuckSuccessRate2", "Luck success 2 (%)",
                "The second luck rate; the Chaos Machine's +10/+11/+12/+13 mix also uses it.", true),
        ]),

        new("Fruit (stat points)", "fruit",
        [
            new("FruitAddPointMin", "Minimum points a fruit gives",
                "When the fruit succeeds, the minimum points it adds."),
            new("FruitAddPointMax", "Maximum points a fruit gives",
                "The maximum of the same range."),
            new("FruitAddPointSuccessRate", "Fruit success (%)",
                "Probability that the fruit adds points instead of failing.", true),
        ]),

        new("Points per level", "points",
        [
            new("DWLevelUpPoint", "Points per level -- Dark Wizard", "Stat points each level gives.", true),
            new("DKLevelUpPoint", "Points per level -- Dark Knight", "Stat points each level gives.", true),
            new("FELevelUpPoint", "Points per level -- Fairy Elf", "Stat points each level gives.", true),
            new("MGLevelUpPoint", "Points per level -- Magic Gladiator", "Stat points each level gives.", true),
            new("DLLevelUpPoint", "Points per level -- Dark Lord", "Stat points each level gives.", true),
            new("MaxStatPoint", "Cap per stat", "Maximum an individual stat can reach.", true),
            new("PlusStatPoint", "Extra points", "Additional points granted from the level below."),
            new("PlusStatMinLevel", "Level for the extra points", "From which level those extra points are given."),
        ]),

        new("Monsters (global multipliers)", "monster",
        [
            new("MonsterMaxLifeRate", "Monster life (%)",
                "Applied on top of MonsterList.txt life: 100 = as in the file, 200 = double."),
            new("MonsterPhysiDamageRate", "Monster damage (%)", "Ditto, on physical damage."),
            new("MonsterDefenseRate", "Monster defense (%)", "Ditto, on defense."),
            new("MonsterDefenseSuccessRateRate", "Monster dodge (%)", "Ditto, on the dodge rate."),
            new("MonsterAttackSuccessRateRate", "Monster hit rate (%)", "Ditto, on the hit rate."),
        ]),

        new("Durability", "durability",
        [
            new("WeaponDurabilityRate", "Weapon durability (%)", "How much it lasts before wearing out: higher = lasts longer."),
            new("ArmorDurabilityRate", "Armor durability (%)", "Ditto for armor pieces."),
            new("WingDurabilityRate", "Wing durability (%)", "Ditto for wings."),
            new("PendantDurabilityRate", "Pendant durability (%)", "Ditto for pendants."),
            new("RingDurabilityRate", "Ring durability (%)", "Ditto for rings."),
            new("PetDurabilityRate", "Pet durability (%)", "Ditto for pets."),
            new("GuardianDurabilityRate", "Guardian durability (%)", "Ditto for guardians."),
        ]),

        new("Characters", "character",
        [
            new("CharacterCreateSwitch", "Allow creating characters", "0 = disabled, 1 = enabled."),
            new("CharacterDeleteSwitch", "Allow deleting characters", "0 = disabled, 1 = enabled."),
            new("CharacterDeleteMaxLevel", "Maximum level to delete", "Above this level the character can no longer be deleted."),
            new("MGCreateLevel", "Level to create Magic Gladiator", "Level another character on the account must have.", true),
            new("DLCreateLevel", "Level to create Dark Lord", "Ditto for the Dark Lord.", true),
        ]),
    ];
}
