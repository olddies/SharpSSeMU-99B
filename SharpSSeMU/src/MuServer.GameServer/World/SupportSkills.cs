namespace MuServer.GameServer.World;

/// <summary>The settings of the skills that help instead of hurting (Skill.dat): what the Dark Wizard's Mana Shield, the Elf's
/// Heal, Greater Defense and Greater Damage, the Dark Knight's Greater Life and the Dark Lord's Greater Critical Damage give,
/// for how long, and the percentage each class gets of it. The shipped values by default.</summary>
public sealed class SupportSkillRules
{
    /// <summary>The rules in force (set by Program).</summary>
    public static SupportSkillRules Current { get; set; } = new();

    public int ManaShieldConstA { get; init; } = 10;
    public int ManaShieldConstB { get; init; } = 50;
    public int ManaShieldConstC { get; init; } = 200;
    public int ManaShieldMaxRate { get; init; } = 50;
    public int ManaShieldTimeConstA { get; init; } = 60;
    public int ManaShieldTimeConstB { get; init; } = 40;
    public int[] ManaShieldRate { get; init; } = { 100, 100, 100, 100, 100 };

    public int HealConstA { get; init; } = 5;
    public int HealConstB { get; init; } = 5;

    public int GreaterDefenseConstA { get; init; } = 2;
    public int GreaterDefenseConstB { get; init; } = 8;
    public int GreaterDefenseTimeConstA { get; init; } = 60;
    public int[] GreaterDefenseRate { get; init; } = { 100, 100, 100, 100, 100 };

    public int GreaterDamageConstA { get; init; } = 3;
    public int GreaterDamageConstB { get; init; } = 7;
    public int GreaterDamageTimeConstA { get; init; } = 60;
    public int[] GreaterDamageRate { get; init; } = { 100, 100, 100, 100, 100 };

    public int GreaterLifeConstA { get; init; } = 12;
    public int GreaterLifeConstB { get; init; } = 100;
    public int GreaterLifeConstC { get; init; } = 20;
    public int GreaterLifeMaxRate { get; init; } = 100;
    public int GreaterLifeTimeConstA { get; init; } = 60;
    public int GreaterLifeTimeConstB { get; init; } = 10;
    public int[] GreaterLifeRate { get; init; } = { 100, 100, 100, 100, 100 };

    public int GreaterCriticalDamageConstA { get; init; } = 25;
    public int GreaterCriticalDamageConstB { get; init; } = 30;
    public int GreaterCriticalDamageTimeConstA { get; init; } = 60;
    public int GreaterCriticalDamageTimeConstB { get; init; } = 10;
}

/// <summary>The skills that help and the numbers they give (SkillManager.cpp:1152-1340, 1727-1777, 2089-2133). A value is what the
/// effect carries; the time is how many seconds it lasts. The stats are the caster's with the additions of items and effects.</summary>
public static class SupportSkills
{
    public const int ManaShield = 16;
    public const int Heal = 26;
    public const int GreaterDefense = 27;
    public const int GreaterDamage = 28;
    public const int GreaterLife = 48;
    public const int GreaterCriticalDamage = 64;

    public static bool IsSupport(int skill) =>
        skill is ManaShield or Heal or GreaterDefense or GreaterDamage or GreaterLife or GreaterCriticalDamage;

    /// <summary>The skills that fall on the one the caster points at; the others (Greater Life and Greater Critical Damage) fall
    /// on the caster and his party.</summary>
    public static bool IsTargeted(int skill) => skill is ManaShield or Heal or GreaterDefense or GreaterDamage;

    private static int Div(int value, int divisor) => divisor == 0 ? 0 : value / divisor;

    private static int Dexterity(PlayerObject p) => (int)(p.Dexterity + p.AddDexterity);
    private static int Energy(PlayerObject p) => (int)(p.Energy + p.AddEnergy);
    private static int Vitality(PlayerObject p) => (int)p.Vitality;
    private static int Leadership(PlayerObject p) => (int)(p.Leadership + p.AddLeadership);

    private static int ClassRate(int[] rates, PlayerObject p) => rates[Math.Clamp((int)p.Class, 0, 4)];

    /// <summary>Mana Shield: part of the damage that falls on the mana instead; the target's class sets how much of it he gets.</summary>
    public static (int Value, int Seconds) ManaShieldFor(PlayerObject caster, PlayerObject target, SupportSkillRules r)
    {
        int value = r.ManaShieldConstA + Div(Dexterity(caster), r.ManaShieldConstB) + Div(Energy(caster), r.ManaShieldConstC);
        value = Math.Min(value, r.ManaShieldMaxRate);
        value = value * ClassRate(r.ManaShieldRate, target) / 100;
        return (value, r.ManaShieldTimeConstA + Div(Energy(caster), r.ManaShieldTimeConstB));
    }

    /// <summary>Heal: the life it gives back.</summary>
    public static int HealFor(PlayerObject caster, SupportSkillRules r) => r.HealConstA + Div(Energy(caster), r.HealConstB);

    /// <summary>Greater Defense: the effect carries twice the value, as the original sets it.</summary>
    public static (int Value, int Seconds) GreaterDefenseFor(PlayerObject caster, PlayerObject target, SupportSkillRules r)
    {
        int value = r.GreaterDefenseConstA + Div(Energy(caster), r.GreaterDefenseConstB);
        value = value * ClassRate(r.GreaterDefenseRate, target) / 100;
        return (value * 2, r.GreaterDefenseTimeConstA);
    }

    public static (int Value, int Seconds) GreaterDamageFor(PlayerObject caster, PlayerObject target, SupportSkillRules r)
    {
        int value = r.GreaterDamageConstA + Div(Energy(caster), r.GreaterDamageConstB);
        value = value * ClassRate(r.GreaterDamageRate, target) / 100;
        return (value, r.GreaterDamageTimeConstA);
    }

    /// <summary>Greater Life (Swell Life): the value before each member's class rate, and the seconds. The original applies the
    /// class rate to the running value, so every member after the first gets a rate on top of the previous one's; that is kept.</summary>
    public static (int Value, int Seconds) GreaterLifeFor(PlayerObject caster, SupportSkillRules r)
    {
        int value = r.GreaterLifeConstA + Div(Vitality(caster), r.GreaterLifeConstB) + Div(Energy(caster), r.GreaterLifeConstC);
        value = Math.Min(value, r.GreaterLifeMaxRate);
        return (value, r.GreaterLifeTimeConstA + Div(Energy(caster), r.GreaterLifeTimeConstB));
    }

    public static int ApplyGreaterLifeRate(int value, PlayerObject member, SupportSkillRules r) =>
        value * ClassRate(r.GreaterLifeRate, member) / 100;

    public static (int Value, int Seconds) GreaterCriticalDamageFor(PlayerObject caster, SupportSkillRules r) =>
        (Div(Energy(caster), r.GreaterCriticalDamageConstA) + Div(Leadership(caster), r.GreaterCriticalDamageConstB),
         r.GreaterCriticalDamageTimeConstA + Div(Energy(caster), r.GreaterCriticalDamageTimeConstB));
}
