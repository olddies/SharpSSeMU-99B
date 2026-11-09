using MuServer.GameServer.Net;
using MuServer.GameServer.World;
using MuServer.Shared.Localization;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.Protocol;

/// <summary>Player against player combat and the Chaos Castle glue of the protocol handler.</summary>
public sealed partial class ClientProtocolHandler
{
    private readonly ChaosCastleManager? _chaosCastle;

    /// <summary>Port of CAttack::CheckPlayerTarget for the one place players may fight: the Chaos Castle while its
    /// fight is on. Everywhere else players cannot hurt each other (the PK system -- PK levels, outlaws, the items a
    /// murdered player drops -- is not ported, so opening PvP in the towns and fields would be PK without its rules).
    /// A party's members may or may not hit each other as PartyDisableKillBetweenMembers says.</summary>
    private bool CanAttackPlayer(PlayerObject attacker, PlayerObject target)
    {
        if (attacker.Index == target.Index || !target.WorldEntered || target.IsDying || target.Life == 0 || attacker.Map != target.Map)
        {
            return false;
        }

        if ((_gsiCommon?.PartyDisableKillBetweenMembers ?? 0) != 0 && attacker.PartyNumber != -1 && attacker.PartyNumber == target.PartyNumber)
        {
            return false;
        }

        return _chaosCastle != null && _chaosCastle.IsFightOn(attacker.Map);
    }

    /// <summary>A plain attack on another player (the player branch of <see cref="OnAttackAsync"/>): the same checks
    /// as against a monster, the PvP hit roll and damage, and the death if it kills.</summary>
    private async Task AttackPlayerAsync(ClientSession session, PlayerObject attacker, PlayerObject target, AttackRecv recv, CancellationToken ct)
    {
        if (attacker.IsDying || attacker.Life == 0 || !CanAttackPlayer(attacker, target))
        {
            return;
        }

        var map = _maps.GetMap(attacker.Map);

        if ((map?.CheckAttr(attacker.X, attacker.Y, 1) ?? false) || (map?.CheckAttr(target.X, target.Y, 1) ?? false))
        {
            return;
        }

        double distance = Math.Sqrt(Math.Pow(attacker.X - target.X, 2) + Math.Pow(attacker.Y - target.Y, 2));

        if (distance > (attacker.Class == ClassFe ? 6 : 3))
        {
            return;
        }

        attacker.Dir = recv.Dir;

        var actionPacket = CombatPacketBuilder.ActionSend(attacker.Index, attacker.Dir, recv.Action, target.Index);

        foreach (var viewer in _players.All)
        {
            if (viewer.Index != attacker.Index && viewer.VisibleTo.Contains(attacker.Index))
            {
                await viewer.Session.SendAsync(actionPacket, ct);
            }
        }

        await ResolvePlayerHitAsync(attacker, target, skill: null, ct);
    }

    /// <summary>A single-target skill on another player (the player branch of <see cref="OnSkillAttackAsync"/>).</summary>
    private async Task SkillAttackPlayerAsync(ClientSession session, PlayerObject attacker, PlayerObject target, SkillAttackRecv recv, CancellationToken ct)
    {
        if (attacker.IsDying || attacker.Life == 0 || !CanAttackPlayer(attacker, target))
        {
            return;
        }

        var map = _maps.GetMap(attacker.Map);

        if ((map?.CheckAttr(attacker.X, attacker.Y, 1) ?? false) || (map?.CheckAttr(target.X, target.Y, 1) ?? false))
        {
            return;
        }

        var skill = await TryUseAttackSkillAsync(session, attacker, recv.Skill, target.X, target.Y, ct);

        if (skill == null)
        {
            return;
        }

        await ResolvePlayerHitAsync(attacker, target, skill, ct);

        var skillPacket = SkillPacketBuilder.SkillAttackSend((byte)skill.Index, attacker.Index, target.Index);
        await session.SendEncryptedAsync(skillPacket, ct);

        foreach (var viewer in _players.All)
        {
            if (viewer.Index != attacker.Index && viewer.VisibleTo.Contains(attacker.Index))
            {
                await viewer.Session.SendEncryptedAsync(skillPacket, ct);
            }
        }
    }

    /// <summary>The roll, the damage and its consequences of one blow of a player on another: CAttack::Attack's
    /// MissCheckPvP, the PvP damage, the life it takes and, when it kills, the death.</summary>
    private async Task ResolvePlayerHitAsync(PlayerObject attacker, PlayerObject target, SkillInfo? skill, CancellationToken ct)
    {
        byte skillIndex = skill == null ? (byte)0 : (byte)skill.Index;

        if (!PlayerAttackMath.PvpBlowLands(attacker, target, Rng))
        {
            var miss = CombatPacketBuilder.DamageSend(target.Index, 0, 0, missFlag: true, target.Life);
            await attacker.Session.SendAsync(miss, ct);
            await target.Session.SendAsync(miss, ct);
            return;
        }

        var now = DateTime.UtcNow;
        attacker.LastCombatTime = now;
        target.LastCombatTime = now;

        var hit = PlayerAttackMath.HitPlayer(attacker, target, skillIndex, skill?.DamageMin ?? 0, skill?.DamageMax ?? 0, graze: false,
            _itemBalance, Rng, skill == null ? null : _skillDamage.Apply, chaosCastle: true);

        await ApplyAttackerLifeCostAsync(attacker, hit.AttackerLifeLost, ct);

        target.Life = (uint)Math.Max(0, (long)target.Life - hit.Damage);

        Log.Add(LogColor.Black, "[PvP][{0}] '{1}' hits '{2}' for {3} (remaining life {4}/{5})",
            attacker.Index, attacker.Name, target.Name, hit.Damage, target.Life, target.MaxLife);

        // CharacterLifeCheck: only a blow that leaves the target alive sends the damage; the death follows otherwise.
        if (target.Life > 0)
        {
            var damagePacket = CombatPacketBuilder.DamageSend(target.Index, hit.Damage, hit.Effect, missFlag: false, target.Life);
            await attacker.Session.SendAsync(damagePacket, ct);
            await target.Session.SendAsync(damagePacket, ct);
            return;
        }

        await KillPlayerAsync(target, attacker.Index, skillIndex, ct);
    }

    /// <summary>A player's death by another player's hand: the event learns of it, and the target and whoever sees
    /// them are told (GCUserDieSend with the killer).</summary>
    private async Task KillPlayerAsync(PlayerObject target, int killerIndex, byte skill, CancellationToken ct)
    {
        if (target.IsDying)
        {
            return;
        }

        target.IsDying = true;
        target.DiedAt = DateTime.UtcNow;

        Log.Add(LogColor.Blue, "[PvP][{0}] '{1}' was killed by #{2} (Map={3} X={4} Y={5})",
            target.Index, target.Name, killerIndex, target.Map, target.X, target.Y);

        await OnPlayerKilledAsync(target, ct, killerIndex);

        await target.Session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("You died.")), ct);
        await target.Session.SendAsync(LifePacketBuilder.LifeSend(0xFF, 0), ct);

        var diePacket = CombatPacketBuilder.UserDieSend(target.Index, skill, killerIndex);
        await target.Session.SendAsync(diePacket, ct);

        foreach (int observer in target.VisibleTo.ToList())
        {
            if (_players.TryGet(observer, out var watcher) && watcher.WorldEntered)
            {
                await watcher.Session.SendAsync(diePacket, ct);
            }
        }
    }

    // ---------------------------------------------------------------- Chaos Castle

    /// <summary>C1:AF:01 -- the entry request.</summary>
    private async Task OnChaosCastleEnterAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || _chaosCastle == null)
        {
            return;
        }

        var recv = ChaosCastleEnterRecv.Parse(p);
        await _chaosCastle.HandleEnterAsync(session, player, recv.Slot, ct);
    }

    /// <summary>C1:AF:02 -- where the client says the player landed after being blown away.</summary>
    private async Task OnChaosCastlePositionAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || _chaosCastle == null)
        {
            return;
        }

        var recv = ChaosCastlePositionRecv.Parse(p);
        await _chaosCastle.HandlePositionAsync(player, recv.X, recv.Y, ct);
    }
}
