using MuServer.GameServer.World;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.Protocol;

/// <summary>The skills that leave something on the one they hit: the poison that keeps hurting, the ice that slows, the arrow that
/// roots, the slash that weakens (CAttack::ApplySkillEffect, Attack.cpp:1064; CEffectManager::PeriodicEffect, EffectManager.cpp:560).
/// What the effects do afterwards -- the monsters' slowness and rooting, the defense, the immunities -- is in the places that use
/// them.</summary>
public sealed partial class ClientProtocolHandler
{
    /// <summary>Port of CAttack::ApplySkillEffect: the effect a skill leaves on its target, once the target's resistance to the
    /// element has had its say. False when the target resisted (the skill still hurts, but leaves nothing, and the client is told
    /// the skill did not take); true otherwise, whether or not the skill leaves anything.</summary>
    internal async Task<bool> ApplySkillEffectAsync(int attackerIndex, int attackerStrength, IEffectCarrier target, SkillInfo skill, CancellationToken ct)
    {
        byte[]? resistance = target switch
        {
            PlayerObject player => player.Resistance,
            Monster monster => monster.Resistance,
            _ => null,
        };

        if (skill.Type is >= 0 and < 7 && resistance != null && SkillDebuffs.Resists(resistance[skill.Type], n => Rng.Next(n)))
        {
            return false;
        }

        StartKnockback(attackerIndex, target, skill, ct); // Lightning and the Dark Knight's skills push, the Earthquake throws

        var effect = SkillDebuffs.EffectFor(skill.Index, attackerIndex, attackerStrength, SupportSkillRules.Current);

        if (effect != null && skill.Effect > 0)
        {
            var (seconds, v1, v2, v3, v4) = effect.Value;
            await Effects.AddAsync(target, false, skill.Effect, seconds, v1, v2, v3, v4, ct);
        }

        return true;
    }

    /// <summary>The poison's turn (CEffectManager::PeriodicEffect): every few seconds it takes a part of the life its victim has
    /// left, and the blow counts as the poisoner's: the number is shown to him, and the kill, with its reward, is his. A poison
    /// whose poisoner is gone does nothing.</summary>
    private async Task OnEffectPeriodicAsync(IEffectCarrier carrier, ActiveEffect effect, CancellationToken ct)
    {
        if (effect.Index != EffectIds.Poison || !SkillDebuffs.PoisonHitsNow(effect.Count, effect.Value[1]))
        {
            return;
        }

        int attackerIndex = effect.Value[0];
        _players.TryGet(attackerIndex, out var attackerPlayer);

        switch (carrier)
        {
            case Monster monster when monster.Live && monster.ShopNumber == null:
            {
                if (attackerPlayer == null)
                {
                    return; // only a player's poison reaches a monster, and he must still be there for the kill to be his
                }

                int damage = SkillDebuffs.PoisonDamage(monster.Life, effect.Value[2], effect.Value[3]);

                if (damage <= 0)
                {
                    return;
                }

                monster.Life = Math.Max(monster.Life - damage, 0);
                monster.DamageByAttacker.AddOrUpdate(attackerPlayer.Index, damage, (_, previous) => previous + damage);

                if (monster.Life > 0)
                {
                    await attackerPlayer.Session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, damage, PoisonDamageType, missFlag: false, monster.Life), ct);
                    return;
                }

                await OnMonsterDeathAsync(attackerPlayer, monster, damage, 0, ct);
                break;
            }

            case PlayerObject victim when victim.WorldEntered && !victim.IsDying && victim.Life > 0:
            {
                if (attackerPlayer == null && !_monsters.TryGet(attackerIndex, out _))
                {
                    return;
                }

                int damage = SkillDebuffs.PoisonDamage(victim.Life, effect.Value[2], effect.Value[3]);

                if (damage <= 0)
                {
                    return;
                }

                victim.Life = (uint)Math.Max(0, (long)victim.Life - damage);

                if (victim.Life > 0)
                {
                    var packet = CombatPacketBuilder.DamageSend(victim.Index, damage, PoisonDamageType, missFlag: false, victim.Life);
                    await victim.Session.SendAsync(packet, ct);

                    if (attackerPlayer != null && attackerPlayer.Index != victim.Index)
                    {
                        await attackerPlayer.Session.SendAsync(packet, ct);
                    }

                    return;
                }

                await KillPlayerAsync(victim, attackerIndex, 0, ct);
                break;
            }
        }
    }

    /// <summary>The colour of the number the client draws for a hit of the poison (the DamageType 2 of CharacterLifeCheck, which sends 5).</summary>
    private const byte PoisonDamageType = 5;

    /// <summary>Once a second for every monster that carries an effect: the poison takes its toll and the effects run out (the
    /// monster half of CEffectManager::MainProc).</summary>
    public async Task OnMonstersSecondAsync(CancellationToken ct)
    {
        foreach (var monster in _monsters.All)
        {
            if (monster.ActiveEffects.Any(e => e.IsEffect))
            {
                try
                {
                    await Effects.TickAsync(monster, ct);
                }
                catch (Exception ex)
                {
                    Log.Add(LogColor.Red, "[Effects] The effects of monster #{0} failed: {1}", monster.Index, ex.Message);
                }
            }
        }
    }

    /// <summary>Whether a monster stands rooted by an Ice Arrow: it neither wanders nor chases (gMonsterCheckMovingCondition).</summary>
    public bool IsRooted(Monster monster) => Effects.CheckImmobilize(monster);

    /// <summary>The Ice's extra wait for a monster, in milliseconds (DelayActionTime): every action it takes comes this much later.</summary>
    public static int ActionDelay(Monster monster) => monster.EffectBonus.DelayActionTime;
}
