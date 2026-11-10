namespace MuServer.GameServer.World;

/// <summary>The skills that leave something on the one they hit (CAttack::ApplySkillEffect, Attack.cpp:1064): the poison of Poison
/// and Decay, the ice of Ice and Ice Storm, the root of Ice Arrow, the weakened defense of Fire Slash. The effect itself is the one
/// SkillList.txt names (Poison 55, Ice 56, Ice Arrow 57, Fire Slash 58); what each puts in it is here.</summary>
public static class SkillDebuffs
{
    public const int Poison = 1;
    public const int Ice = 7;
    public const int Decay = 38;
    public const int IceStorm = 39;
    public const int IceArrow = 51;
    public const int FireSlash = 55;

    /// <summary>The seconds the poison lasts and how it hits: every second of <see cref="PoisonEvery"/>, <see cref="PoisonPercent"/>
    /// of the life it has left.</summary>
    public const int PoisonSeconds = 20;
    public const int DecaySeconds = 10;
    public const int PoisonEvery = 2;
    public const int PoisonPercent = 3;
    public const int IceSeconds = 10;
    public const int IceArrowSeconds = 7;

    public static bool HasEffect(int skill) => skill is Poison or Ice or Decay or IceStorm or IceArrow or FireSlash;

    /// <summary>The Crystal Morning Star and the Crystal Sword, worn in the right hand with durability left, cast Ice by themselves on one
    /// plain blow in twenty (CAttack::Attack, Attack.cpp:237-254). <paramref name="roll"/> gives a number from 0 up to but not including
    /// its argument.</summary>
    public static bool CrystalCastsIce(Item weapon, Func<int, int> roll)
    {
        if (!weapon.IsItem() || weapon.Durability == 0)
        {
            return false;
        }

        if (weapon.Index != Item.GetItem(2, 4) && weapon.Index != Item.GetItem(2, 5))
        {
            return false;
        }

        return roll(20) == 0;
    }

    /// <summary>Port of gObjCheckResistance (User.cpp:1093): true when the target shrugs the effect off. 255 always does; otherwise
    /// the effect lands one time in (resistance + 1). <paramref name="roll"/> gives a number from 0 up to but not including its argument.</summary>
    public static bool Resists(byte resistance, Func<int, int> roll)
    {
        if (resistance == 0xFF)
        {
            return true;
        }

        return roll(resistance + 1) != 0;
    }

    /// <summary>What the skill puts on its target, as the arguments of AddEffect: the seconds and the four powers. Null for a skill
    /// that leaves nothing. For the poison the first power is the one who poisoned (the kill is his), the second how many seconds
    /// between one hit and the next, the third the percent of life it takes, the fourth the most it takes (0 for no limit).</summary>
    public static (int Seconds, ushort V1, ushort V2, ushort V3, ushort V4)? EffectFor(int skill, int attackerIndex, int attackerStrength, SupportSkillRules rules)
    {
        switch (skill)
        {
            case Poison:
                return (PoisonSeconds, (ushort)attackerIndex, PoisonEvery, PoisonPercent, 0);

            case Decay:
                return (DecaySeconds, (ushort)attackerIndex, PoisonEvery, PoisonPercent, 0);

            case Ice:
            case IceStorm:
                return (IceSeconds, 0, 0, 0, 0);

            case IceArrow:
                return (IceArrowSeconds, 0, 0, 0, 0);

            case FireSlash:
            {
                // ApplyFireSlashEffect (SkillManager.cpp:2279): the more strength, the more defense it takes, up to a limit.
                int value = rules.FireSlashConstA + (rules.FireSlashConstB == 0 ? 0 : attackerStrength / rules.FireSlashConstB);
                value = Math.Min(value, rules.FireSlashMaxRate);
                return (rules.FireSlashTimeConstA, (ushort)Math.Clamp(value, 0, ushort.MaxValue), 0, 0, 0);
            }

            default:
                return null;
        }
    }

    /// <summary>The damage one tick of the poison does (PeriodicEffect, EffectManager.cpp:586): a part of the life the victim has,
    /// at most the fourth power when it is not zero.</summary>
    public static int PoisonDamage(float life, ushort percent, ushort cap)
    {
        int damage = (int)(life * percent / 100);
        return damage > cap ? (cap == 0 ? damage : cap) : damage;
    }

    /// <summary>Whether this second is one the poison hits on (PeriodicEffect, EffectManager.cpp:567): every <paramref name="every"/>
    /// seconds of the count that is left.</summary>
    public static bool PoisonHitsNow(uint count, ushort every) => every != 0 && count % every == 0;
}
