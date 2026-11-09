using MuServer.GameServer.Config;
using MuServer.GameServer.Net;
using MuServer.GameServer.World;
using MuServer.Shared.Localization;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.Protocol;

/// <summary>Player against player combat and the Chaos Castle glue of the protocol handler.</summary>
public sealed partial class ClientProtocolHandler
{
    private readonly ChaosCastleManager? _chaosCastle;

    /// <summary>The duels (set by Program).</summary>
    public DuelManager? Duel { get; set; }

    private bool InDuelWith(PlayerObject a, PlayerObject b) => Duel != null && Duel.CheckDuel(a, b);

    /// <summary>Port of CAttack::CheckPlayerTarget: who may hurt whom. Never in Devil Square or Blood Castle; in the
    /// Chaos Castle while its fight is on; anywhere else when both are above level 5 and the map allows PK (the NonPK
    /// column of MapManager.txt, or the server's NonPK). A party's members may or may not hit each other as
    /// PartyDisableKillBetweenMembers says. Duels and guild wars, which also open PvP in the original, are not ported.</summary>
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

        // Someone in a duel can be hurt only by the one they duel (Attack.cpp:939).
        if (!InDuelWith(attacker, target) && target.DuelUser >= 0)
        {
            return false;
        }

        if (attacker.Map is 9 or 32 or (>= 11 and <= 17))
        {
            return false; // Devil Square, Blood Castle
        }

        if (attacker.Map is >= 18 and <= 23)
        {
            return _chaosCastle != null && _chaosCastle.IsFightOn(attacker.Map);
        }

        return attacker.Level > 5 && target.Level > 5 && !_loot.MapDrops.NonPk(target.Map, PkRules.Current.NonPk);
    }

    /// <summary>Whether a kill of <paramref name="victim"/> by <paramref name="killer"/> counts for the PK system: not in
    /// the Chaos Castle and not on a map without outlaws (the checks of gObjPlayerKiller and of the self-defense).</summary>
    private bool PlayerKillCounts(PlayerObject killer, PlayerObject victim) =>
        !(killer.Map is >= 18 and <= 23 && victim.Map is >= 18 and <= 23) && !_loot.MapDrops.NonOutlaw(killer.Map) && !InDuelWith(killer, victim);

    /// <summary>Tells the player, and whoever sees them, their PK level (GCPKLevelSend).</summary>
    private async Task SendPkLevelAsync(PlayerObject player, CancellationToken ct)
    {
        var packet = WorldPacketBuilder.PKLevelSend(player.Index, player.PKLevel);
        await player.Session.SendAsync(packet, ct);

        foreach (var other in _players.All)
        {
            if (other.Index != player.Index && other.WorldEntered && other.VisibleTo.Contains(player.Index))
            {
                await other.Session.SendAsync(packet, ct);
            }
        }
    }

    private string MessageText(int number, string fallback) => _messages?.GetMessage(number) is { Length: > 0 } text ? text : fallback;

    /// <summary>Port of the self-defense check of CAttack::Attack: the attacker has hurt the target. Unless the kill would
    /// not count anyway, the attacker starts defending against them, and the target (or their whole party) and the
    /// attacker are told.</summary>
    private async Task CheckSelfDefenseAsync(PlayerObject attacker, PlayerObject target, CancellationToken ct)
    {
        if (!PlayerKillCounts(attacker, target))
        {
            return;
        }

        if (!PkSystem.StartSelfDefense(attacker, target, index => _players.TryGet(index, out var found) ? found : null, DateTime.UtcNow))
        {
            return;
        }

        var notice = ChatPacketBuilder.NoticeSend(BloodCastleManager.Fmt(MessageText(284, "Self defense against %s started"), attacker.Name), 1);

        if (target.PartyNumber >= 0 && _parties.TryGet(target.PartyNumber, out var group))
        {
            foreach (int memberIndex in group.MemberIndices.ToList())
            {
                if (_players.TryGet(memberIndex, out var member))
                {
                    await member.Session.SendAsync(notice, ct);
                }
            }
        }
        else
        {
            await target.Session.SendAsync(notice, ct);
        }

        await attacker.Session.SendAsync(notice, ct);
    }

    /// <summary>Once a second for every player: the PK time runs down (and the level with it) and the self-defenses
    /// whose minute is over end, with a notice to both sides (the PK part of gObjSecondProc).</summary>
    public async Task OnPlayerSecondAsync(PlayerObject player, CancellationToken ct)
    {
        var rules = PkRules.Current;

        if (PkSystem.DownCheckTime(player, rules.DownPlusTimePoint, byKill: false, rules))
        {
            await SendPkLevelAsync(player, ct);
        }

        if (Duel != null)
        {
            await Duel.OnSecondAsync(player, ct);
        }

        foreach (int against in PkSystem.ExpireSelfDefense(player, DateTime.UtcNow))
        {
            var notice = ChatPacketBuilder.NoticeSend(BloodCastleManager.Fmt(MessageText(285, "Self defense against %s finished"), player.Name), 1);
            await player.Session.SendAsync(notice, ct);

            if (_players.TryGet(against, out var other))
            {
                await other.Session.SendAsync(notice, ct);
            }
        }
    }

    /// <summary>A monster killed takes PK time off its killer (gObjPKDownCheckTime with the monster's level over
    /// PKDownPlusKillPoint): the way a murderer works their level down.</summary>
    private async Task PkDownByKillAsync(PlayerObject killer, int monsterLevel, CancellationToken ct)
    {
        var rules = PkRules.Current;

        if (PkSystem.DownCheckTime(killer, monsterLevel / Math.Max(rules.DownPlusKillPoint, 1), byKill: true, rules))
        {
            await SendPkLevelAsync(killer, ct);
        }
    }

    /// <summary>Port of the announcement of gObjUserDie and of gObjKillerItemDrop: a murderer who dies is announced
    /// when the server says so, and may drop one of the items they wear where they fall, for whoever picks it up.</summary>
    private async Task OnMurdererDiedAsync(PlayerObject victim, PlayerObject? killer, CancellationToken ct)
    {
        var rules = PkRules.Current;

        if (killer != null && InDuelWith(victim, killer))
        {
            return; // no announcement and no item for a death in a duel
        }

        if (victim.PKLevel > 4 && killer != null && rules.DeathAnnounce > 0)
        {
            var text = BloodCastleManager.Fmt(MessageText(383, "[PK] %s was killed by %s!"), victim.Name, killer.Name);
            var announcement = ChatPacketBuilder.NoticeSend(text, (byte)(rules.DeathAnnounce == 1 ? 0 : 1));

            foreach (var player in _players.All)
            {
                if (player.WorldEntered)
                {
                    await player.Session.SendAsync(announcement, ct);
                }
            }
        }

        if (_groundItems == null || !_loot.MapDrops.PkItemDrop(victim.Map))
        {
            return;
        }

        int slot = PkSystem.ChooseDroppedSlot(victim, killer != null, rules, _itemBalance, () => Rng.Next(100), count => Rng.Next(count));

        if (slot < 0)
        {
            return;
        }

        var item = victim.Items[slot];
        var ground = _groundItems.Drop(victim.Map, item, victim.X, victim.Y, victim.Index, null, GroundItemLifetime, TimeSpan.Zero);

        if (ground == null)
        {
            return;
        }

        victim.SetItem(slot, Item.Empty());
        await victim.Session.SendAsync(ItemPacketBuilder.ItemDropSend(1, (byte)slot), ct);

        victim.RebuildCharSet();
        victim.RecalcCombatStats(_itemBalance, _characterBalance);
        await victim.Session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(victim), ct);

        var appearance = WorldPacketBuilder.ViewportPlayerAppear(new[] { victim });
        var itemAppears = WorldPacketBuilder.ViewportItemAppear(new[] { ground });
        await victim.Session.SendAsync(itemAppears, ct);

        foreach (var other in _players.All)
        {
            if (other.Index != victim.Index && other.WorldEntered && other.VisibleTo.Contains(victim.Index))
            {
                await other.Session.SendAsync(appearance, ct);
                await other.Session.SendAsync(itemAppears, ct);
            }
        }

        ground.JustDropped = false;
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

        Duel?.Touch(attacker, target); // a blow keeps a duel alive
        Spirits.SetTarget(attacker, target.Index);

        var hit = PlayerAttackMath.HitPlayer(attacker, target, skillIndex, skill?.DamageMin ?? 0, skill?.DamageMax ?? 0, graze: false,
            _itemBalance, Rng, skill == null ? null : _skillDamage.Apply, chaosCastle: attacker.Map is >= 18 and <= 23, duel: InDuelWith(attacker, target));

        await ApplyAttackerLifeCostAsync(attacker, hit.AttackerLifeLost, ct);
        await ApplyHelperWearAsync(target, hit.TargetWearDamage, ct);

        if (hit.Damage > 0)
        {
            await CheckSelfDefenseAsync(attacker, target, ct);
        }

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

        // gObjPlayerKiller: the killer may become a murderer, or go up the hero levels for killing one.
        if (_players.TryGet(killerIndex, out var killer) && PlayerKillCounts(killer, target)
            && PkSystem.PlayerKilled(killer, target, PkRules.Current))
        {
            await SendPkLevelAsync(killer, ct);
        }

        PkSystem.ClearSelfDefense(target);

        // A kill in a duel is a point for the killer; the loser comes back where they fell.
        if (killer != null && Duel != null)
        {
            await Duel.OnKilledAsync(target, killer, ct);
        }

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

    /// <summary>The commands' settings of the Command.dat (set by Program): what /pkclear costs.</summary>
    public GameServerInfoCommand? CommandInfo { get; set; }

    /// <summary>/pkclear (CCommandManager::CommandPKClear): a murderer pays to go back to normal. With
    /// <c>CommandPKClearType</c> 0 it is free; otherwise it costs <c>CommandPKClearMoney</c> of the account level for each
    /// kill. The generic command requirements of the original (level, zen and the rest of CommandManager.txt) are not
    /// ported.</summary>
    private async Task PkClearCommandAsync(ClientSession session, PlayerObject player, CancellationToken ct)
    {
        if (player.PKLevel < 4)
        {
            await session.SendAsync(ChatPacketBuilder.NoticeSend(MessageText(329, "You're not PK"), 1), ct);
            return;
        }

        long money = 0;

        if ((CommandInfo?.CommandPKClearType ?? 0) != 0)
        {
            money = (long)CommandInfo!.CommandPKClearMoney[Math.Clamp(player.AccountLevel, 0, 3)] * player.PKCount;

            if (player.Money < money)
            {
                await session.SendAsync(ChatPacketBuilder.NoticeSend(BloodCastleManager.Fmt(MessageText(306, "You must be at least %d Money to use %s"), money, "/pkclear"), 1), ct);
                return;
            }
        }

        player.PKLevel = 3;
        player.PKCount = 0;
        player.PKTime = 0;
        await SendPkLevelAsync(player, ct);
        await session.SendAsync(ChatPacketBuilder.NoticeSend(MessageText(330, "You cleared your PK"), 1), ct);

        if (money > 0)
        {
            player.Money -= (uint)money;
            await session.SendAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
        }

        Log.Add(LogColor.Blue, "[CommandPKClear][{0}][{1}] - cleared (cost {2})", player.Account, player.Name, money);
        await SaveCharacterAsync(player, ct);
    }

    // ---------------------------------------------------------------- Duel

    /// <summary>C1:AA -- the player asks another to duel.</summary>
    private async Task OnDuelStartAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || Duel == null)
        {
            return;
        }

        var recv = DuelStartRecv.Parse(p);
        await Duel.HandleStartAsync(player, recv.Index, recv.Name, ct);
    }

    /// <summary>C1:AC -- the answer to a request.</summary>
    private async Task OnDuelAnswerAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || Duel == null)
        {
            return;
        }

        var recv = DuelOkRecv.Parse(p);
        await Duel.HandleAnswerAsync(player, recv.Flag != 0, recv.Index, recv.Name, ct);
    }

    /// <summary>C1:AB -- the player gives a request up or ends their duel.</summary>
    private async Task OnDuelEndAsync(ClientSession session, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || Duel == null)
        {
            return;
        }

        await Duel.HandleEndAsync(player, ct);
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
