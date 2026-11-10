using MuServer.GameServer.Net;
using MuServer.GameServer.World;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.Protocol;

/// <summary>The skills that move allies: the Dark Wizard's Teleport Ally, which sends a member of the party to a spot near him, and the Dark
/// Lord's Summon Party, which calls the whole party to the caster after a countdown (SkillManager.cpp:2036-2084, 2603-2662; User.cpp:2223-2285).</summary>
public sealed partial class ClientProtocolHandler
{
    private const int SkillTeleport = 6;
    private const int SkillTeleportAlly = 15;

    /// <summary>Seconds the summoned wait before they arrive (SkillSummonPartyTime = 5).</summary>
    private const int SummonPartyDelay = 5;

    /// <summary>Where a player may be summoned (MoveSummon.txt).</summary>
    public MoveSummonTable MoveSummons { get; set; } = new();

    private string NoticeText(int number, string fallback, params object[] args) =>
        BloodCastleManager.Fmt(MessageText(number, fallback), args);

    // ---------------------------------------------------------------- teleport ally

    /// <summary>Port of gObjCheckTeleportArea (User.cpp:1110): a live player who is not already teleporting, a spot at most 8 tiles from where
    /// he stands, and neither of the two in a safe zone.</summary>
    private bool CheckTeleportArea(PlayerObject target, int x, int y)
    {
        if (!target.WorldEntered || target.IsDying || target.Life == 0)
        {
            return false;
        }

        if (x < target.X - 8 || x > target.X + 8 || y < target.Y - 8 || y > target.Y + 8)
        {
            return false;
        }

        var map = _maps.GetMap(target.Map);
        return !((map?.CheckAttr(target.X, target.Y, 1) ?? false) || (map?.CheckAttr(x, y, 1) ?? false));
    }

    /// <summary>Port of gObjTeleportMagicUse (User.cpp:1489): the player is at the new spot and his client is told.</summary>
    private async Task TeleportMagicUseAsync(PlayerObject target, int x, int y, CancellationToken ct)
    {
        target.X = (byte)x;
        target.Y = (byte)y;
        target.TX = (byte)x;
        target.TY = (byte)y;
        target.OldX = (byte)x;
        target.OldY = (byte)y;

        await target.Session.SendEncryptedAsync(WorldPacketBuilder.TeleportSend(0, target.Map, target.X, target.Y, target.Dir), ct);
    }

    /// <summary>Port of CGSkillTeleportAllyRecv (C3:B0): a member of the party is sent to a spot within eight tiles of where he stands. Both
    /// must be in the same party and map; the cost is paid by the caster. A target who is gone is the caster himself.</summary>
    private async Task OnTeleportAllyAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var caster = session.Player;

        if (caster == null || !caster.WorldEntered || caster.IsDying || caster.Life == 0 || p.Length < 7)
        {
            return;
        }

        int index = p[3] | (p[4] << 8); // a WORD of the machine, not of the network
        byte x = p[5];
        byte y = p[6];

        if (!_players.TryGet(index, out var target) || !target.WorldEntered)
        {
            target = caster;
        }

        if (caster.PartyNumber == -1 || target.PartyNumber != caster.PartyNumber || target.Map != caster.Map)
        {
            return;
        }

        if (!CheckTeleportArea(target, x, y))
        {
            return;
        }

        var skill = _skills.Get(SkillTeleportAlly);

        if (skill == null || !skill.CanUse(caster.Class, caster.ChangeUp) || caster.Level < skill.RequireLevel)
        {
            return;
        }

        if ((int)caster.Mana < skill.Mana || (int)caster.BP < skill.BP)
        {
            return;
        }

        await SendSkillEchoAsync(caster, skill, target.Index, landed: true, ct);
        await TeleportMagicUseAsync(target, x, y, ct);

        caster.Mana = (uint)Math.Max((int)caster.Mana - skill.Mana, 0);
        caster.BP = (uint)Math.Max((int)caster.BP - skill.BP, 0);
        await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)caster.Mana, (int)caster.BP), ct);

        Log.Add(LogColor.Black, "[Skill][{0}] '{1}' sends '{2}' to {3},{4} with Teleport Ally", caster.Index, caster.Name, target.Name, x, y);
    }

    // ---------------------------------------------------------------- summon party

    /// <summary>The cast of Summon Party (SkillManager.cpp:2036): each member of the caster's party who can be taken where he stands is told
    /// he will be, in five seconds, at a free spot near him; those who cannot are told so.</summary>
    private async Task StartSummonPartyAsync(PlayerObject caster, CancellationToken ct)
    {
        if (caster.PartyNumber == -1 || !_parties.TryGet(caster.PartyNumber, out var party))
        {
            return;
        }

        var map = _maps.GetMap(caster.Map);

        foreach (int index in party.MemberIndices.ToList())
        {
            if (index == caster.Index || !_players.TryGet(index, out var member) || !member.WorldEntered)
            {
                continue;
            }

            if (!MoveSummons.Check(member, caster.Map, caster.X, caster.Y))
            {
                await member.Session.SendAsync(ChatPacketBuilder.NoticeSend(NoticeText(272, "It was not possible to carry out teleportation"), 1), ct);
                continue;
            }

            var (px, py) = map?.GetRandomPos(caster.X, caster.Y, 4, Rng) ?? (caster.X, caster.Y);

            member.SkillSummonPartyTime = SummonPartyDelay;
            member.SkillSummonPartyMap = caster.Map;
            member.SkillSummonPartyX = (byte)px;
            member.SkillSummonPartyY = (byte)py;

            await member.Session.SendAsync(ChatPacketBuilder.NoticeSend(NoticeText(273, "You will be teleported in %d second(s)", member.SkillSummonPartyTime), 1), ct);
        }
    }

    /// <summary>Once a second (gObjSkillUseProc, User.cpp:2262): a summoned player's countdown runs down, he is told the seconds left, and at
    /// zero he is taken there if the place still allows it.</summary>
    private async Task OnSummonSecondAsync(PlayerObject player, CancellationToken ct)
    {
        if (player.SkillSummonPartyTime <= 0)
        {
            return;
        }

        if (--player.SkillSummonPartyTime > 0)
        {
            await player.Session.SendAsync(ChatPacketBuilder.NoticeSend(NoticeText(273, "You will be teleported in %d second(s)", player.SkillSummonPartyTime), 1), ct);
            return;
        }

        if (!MoveSummons.Check(player, player.SkillSummonPartyMap, player.SkillSummonPartyX, player.SkillSummonPartyY))
        {
            await player.Session.SendAsync(ChatPacketBuilder.NoticeSend(NoticeText(272, "It was not possible to carry out teleportation"), 1), ct);
            return;
        }

        await SummonAllyAsync(player, player.SkillSummonPartyMap, player.SkillSummonPartyX, player.SkillSummonPartyY, ct);
    }

    /// <summary>Walking or going through a gate while waiting to be summoned cancels it (Protocol.cpp:963, User.cpp:2054).</summary>
    private async Task CancelSummonAsync(PlayerObject player, CancellationToken ct)
    {
        if (player.SkillSummonPartyTime == 0)
        {
            return;
        }

        player.SkillSummonPartyTime = 0;
        await player.Session.SendAsync(ChatPacketBuilder.NoticeSend(NoticeText(272, "It was not possible to carry out teleportation"), 1), ct);
    }

    /// <summary>Port of gObjSummonAlly (User.cpp:2223): on the same map the player teleports there with the Teleport skill's echo; on another
    /// one he changes map, and the client is told as for a gate.</summary>
    private async Task SummonAllyAsync(PlayerObject player, byte map, byte x, byte y, CancellationToken ct)
    {
        player.SkillSummonPartyTime = 0;
        player.SkillSummonPartyMap = 0;
        player.SkillSummonPartyX = 0;
        player.SkillSummonPartyY = 0;

        if (player.Map == map)
        {
            var teleport = _skills.Get(SkillTeleport);

            if (teleport != null)
            {
                await SendSkillEchoAsync(player, teleport, player.Index, landed: true, ct);
            }

            await TeleportMagicUseAsync(player, x, y, ct);
            return;
        }

        player.Map = map;
        player.X = x;
        player.Y = y;
        player.TX = x;
        player.TY = y;
        player.OldX = x;
        player.OldY = y;
        player.VisibleMonsters.Clear();
        await Spirits.StandDownAsync(player, ct);

        await player.Session.SendEncryptedAsync(WorldPacketBuilder.TeleportSend(1, player.Map, player.X, player.Y, player.Dir), ct);
    }
}
