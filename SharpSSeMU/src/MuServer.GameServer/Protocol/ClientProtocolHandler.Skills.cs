using MuServer.GameServer.Net;
using MuServer.GameServer.World;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.Protocol;

/// <summary>The skills that help instead of hurting, and the effects (buffs and debuffs) they put on players: Mana Shield, Heal,
/// Greater Defense, Greater Damage, Greater Life and Greater Critical Damage (SkillManager.cpp:1152-1340, 1727-1777, 2089-2133).</summary>
public sealed partial class ClientProtocolHandler
{
    /// <summary>The buffs and debuffs players carry (CEffectManager); see <see cref="EffectManager"/>.</summary>
    public EffectManager Effects { get; }

    /// <summary>Where a player in a safe zone may still cast (SkillUseArea.txt): the starting places of the events.</summary>
    public SkillUseAreaTable SkillUseAreas { get; set; } = new();

    /// <summary>An effect came or went and changed what the player is worth: his numbers are worked out again and the client is
    /// told the new maximums and what he has now (the end of CharacterCalcAttribute and its notifications).</summary>
    private async Task RefreshStatsAsync(PlayerObject player, CancellationToken ct)
    {
        if (!player.WorldEntered)
        {
            return;
        }

        player.RecalcCombatStats(_itemBalance, _characterBalance);
        var session = player.Session;
        await session.SendAsync(WorldPacketBuilder.NewCharacterCalcSend(player, SpiritPreview(player)), ct);
        await session.SendAsync(LifePacketBuilder.LifeSend(0xFE, (int)player.MaxLife), ct);
        await session.SendAsync(LifePacketBuilder.LifeSend(0xFF, (int)player.Life), ct);
        await session.SendAsync(ManaPacketBuilder.ManaSend(0xFE, (int)player.MaxMana, (int)player.MaxBP), ct);
        await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)player.Mana, (int)player.BP), ct);
    }

    /// <summary>Port of CGSkillCancelRecv (SkillManager.cpp:2591): the client asks to end a skill's effect (Mana Shield is the one
    /// a player can take off).</summary>
    private async Task OnSkillCancelAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || p.Length < 4)
        {
            return;
        }

        var skill = _skills.Get(p[3]);

        if (skill != null && skill.Effect > 0)
        {
            await Effects.DelAsync(player, skill.Effect, ct);
        }
    }

    /// <summary>The cast of a skill that helps (the C3:19 of CGSkillAttackRecv for those skills). Heal, Mana Shield, Greater
    /// Defense and Greater Damage fall on the player the caster points at (himself included); Greater Life and Greater Critical
    /// Damage on the caster and the members of his party within the skill's radius. The cost is paid only when the skill took
    /// effect, and everybody who sees the caster is shown the cast.</summary>
    private async Task CastSupportSkillAsync(ClientSession session, PlayerObject caster, SkillAttackRecv recv, CancellationToken ct)
    {
        if (caster.IsDying || caster.Life == 0)
        {
            return;
        }

        var skill = _skills.Get(recv.Skill);

        if (skill == null || !skill.CanUse(caster.Class, caster.ChangeUp) || caster.Level < skill.RequireLevel)
        {
            return;
        }

        PlayerObject? target = null;

        if (SupportSkills.IsTargeted(skill.Index))
        {
            if (!_players.TryGet(recv.TargetIndex, out target) || !target.WorldEntered || target.IsDying || target.Life == 0
                || target.Map != caster.Map)
            {
                return; // a monster cannot be helped, and neither can the dead
            }
        }

        // A safe zone allows nothing but what SkillUseArea.txt lists, and only at the places it lists (CGSkillAttackRecv).
        var map = _maps.GetMap(caster.Map);

        if (((map?.CheckAttr(caster.X, caster.Y, 1) ?? false) && !SkillUseAreas.CheckUseSkill(caster.Map, caster.X, caster.Y, skill.Index))
            || (target != null && (map?.CheckAttr(target.X, target.Y, 1) ?? false) && !SkillUseAreas.CheckUseSkill(target.Map, target.X, target.Y, skill.Index)))
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (caster.SkillDelay.TryGetValue(skill.Index, out var lastCast) && (now - lastCast).TotalMilliseconds < skill.Delay)
        {
            return;
        }

        if ((int)caster.Mana < skill.Mana || (int)caster.BP < skill.BP)
        {
            return;
        }

        var rules = SupportSkillRules.Current;
        var helped = new List<PlayerObject>();

        switch (skill.Index)
        {
            case SupportSkills.ManaShield:
                // Only a party's own (or two players with none: both are -1, as in the original), and in the Chaos Castle only oneself.
                if (caster.PartyNumber != target!.PartyNumber || !CanBeHelpedBy(caster, target) || !InSkillRange(caster, skill, target))
                {
                    return;
                }

                var (shield, shieldSeconds) = SupportSkills.ManaShieldFor(caster, target, rules);
                await Effects.AddAsync(target, false, skill.Effect, shieldSeconds, (ushort)Math.Clamp(shield, 0, ushort.MaxValue), 0, 0, 0, ct);
                helped.Add(target);
                break;

            case SupportSkills.Heal:
                if (!CanBeHelpedBy(caster, target!) || !InSkillRange(caster, skill, target!))
                {
                    return;
                }

                int healed = SupportSkills.HealFor(caster, rules);
                target!.Life = (uint)Math.Min((long)target.Life + healed, target.MaxLife);
                await target.Session.SendAsync(LifePacketBuilder.LifeSend(0xFF, (int)target.Life), ct);
                helped.Add(target);
                break;

            case SupportSkills.GreaterDefense:
                if (!CanBeHelpedBy(caster, target!) || !InSkillRange(caster, skill, target!))
                {
                    return;
                }

                var (defense, defenseSeconds) = SupportSkills.GreaterDefenseFor(caster, target!, rules);
                await Effects.AddAsync(target!, false, skill.Effect, defenseSeconds, (ushort)Math.Clamp(defense, 0, ushort.MaxValue), 0, 0, 0, ct);
                helped.Add(target!);
                break;

            case SupportSkills.GreaterDamage:
                if (!CanBeHelpedBy(caster, target!) || !InSkillRange(caster, skill, target!))
                {
                    return;
                }

                var (damage, damageSeconds) = SupportSkills.GreaterDamageFor(caster, target!, rules);
                await Effects.AddAsync(target!, false, skill.Effect, damageSeconds, (ushort)Math.Clamp(damage, 0, ushort.MaxValue), 0, 0, 0, ct);
                helped.Add(target!);
                break;

            case SupportSkills.GreaterLife:
                var (life, lifeSeconds) = SupportSkills.GreaterLifeFor(caster, rules);

                foreach (var member in PartyMembersInRadius(caster, skill))
                {
                    // The class rate of each member is applied to the value the previous member got, as the original does.
                    life = SupportSkills.ApplyGreaterLifeRate(life, member, rules);
                    await Effects.AddAsync(member, false, skill.Effect, lifeSeconds, (ushort)Math.Clamp(life, 0, ushort.MaxValue), 0, 0, 0, ct);
                    helped.Add(member);
                }

                break;

            case SupportSkills.GreaterCriticalDamage:
                var (critical, criticalSeconds) = SupportSkills.GreaterCriticalDamageFor(caster, rules);

                foreach (var member in PartyMembersInRadius(caster, skill))
                {
                    await Effects.AddAsync(member, false, skill.Effect, criticalSeconds, (ushort)Math.Clamp(critical, 0, ushort.MaxValue), 0, 0, 0, ct);
                    helped.Add(member);
                }

                break;

            case SupportSkills.SummonParty:
                await StartSummonPartyAsync(caster, ct); // the cast takes effect even for a Dark Lord with no party, as in the original
                helped.Add(caster);
                break;

            default:
                return;
        }

        // The cost is paid for a cast that took effect (UseAttackSkill), and the next one waits for the skill's delay.
        caster.SkillDelay[skill.Index] = now;
        caster.Mana = (uint)Math.Max((int)caster.Mana - skill.Mana, 0);
        caster.BP = (uint)Math.Max((int)caster.BP - skill.BP, 0);
        await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)caster.Mana, (int)caster.BP), ct);

        foreach (var receiver in helped)
        {
            var packet = CombatPacketBuilder.SkillAttackSend(caster.Index, (byte)skill.Index, receiver.Index, true);
            await session.SendEncryptedAsync(packet, ct);

            foreach (var viewer in _players.All)
            {
                if (viewer.Index != caster.Index && viewer.WorldEntered && viewer.VisibleTo.Contains(caster.Index))
                {
                    await viewer.Session.SendEncryptedAsync(packet, ct);
                }
            }
        }

        Log.Add(LogColor.Black, "[Skill][{0}] '{1}' casts '{2}' on {3} player(s) (mana {4}/{5})",
            caster.Index, caster.Name, skill.Name, helped.Count, caster.Mana, caster.MaxMana);
    }

    /// <summary>Inside the Chaos Castle nobody is helped but the caster himself.</summary>
    private static bool CanBeHelpedBy(PlayerObject caster, PlayerObject target) =>
        caster.Index == target.Index || caster.Map is < 18 or > 23;

    /// <summary>CheckSkillRange: the target is within the skill's reach (two tiles more for a Dark Lord on his Dark Horse), and a
    /// skill with no range cannot be pointed at anyone.</summary>
    private static bool InSkillRange(PlayerObject caster, SkillInfo skill, PlayerObject target)
    {
        if (skill.Range == 0)
        {
            return false;
        }

        int reach = skill.Range + (caster.Class == 4 && caster.Items[Item.SlotHelper].Index == Item.DarkHorse ? 2 : 0);
        double distance = Math.Sqrt(Math.Pow(caster.X - target.X, 2) + Math.Pow(caster.Y - target.Y, 2));
        return distance <= reach;
    }

    /// <summary>The caster, alone or with the members of his party who stand within the skill's radius (CheckSkillRadio).</summary>
    private List<PlayerObject> PartyMembersInRadius(PlayerObject caster, SkillInfo skill)
    {
        var members = new List<PlayerObject>();

        if (caster.PartyNumber == -1 || !_parties.TryGet(caster.PartyNumber, out var party))
        {
            members.Add(caster);
            return members;
        }

        foreach (int index in party.MemberIndices)
        {
            if (!_players.TryGet(index, out var member) || !member.WorldEntered || member.IsDying || member.Life == 0 || member.Map != caster.Map)
            {
                continue;
            }

            double distance = Math.Sqrt(Math.Pow(caster.X - member.X, 2) + Math.Pow(caster.Y - member.Y, 2));

            if (skill.Radio != 0 && distance <= skill.Radio)
            {
                members.Add(member);
            }
        }

        return members;
    }
}
