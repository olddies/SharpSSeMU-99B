using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

/// <summary>
/// Port of CDuel (Duel.cpp). A player asks another to duel (the request waits 30 seconds); if the other accepts, the two
/// fight each other, and only each other, up to a score or until a minute goes by with no blow. A kill in a duel is
/// no PK: no murderer, no self-defense, no item dropped, and the loser comes back where they fell. The winner goes in
/// the duel ranking.
/// <para>Not ported: the refusals that depend on systems this server does not have (Guild War, the Castle Siege),
/// and the player's own "do not accept duels" option, which the original never changes either.</para>
/// </summary>
public sealed class DuelManager
{
    private static readonly TimeSpan RequestTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DuelIdleTime = TimeSpan.FromSeconds(60);

    private readonly PlayerRegistry _players;
    private readonly MapDropTable _maps;
    private readonly MessageTable _messages;
    private readonly Func<byte[], CancellationToken, Task> _sendToDataServer;

    public DuelManager(PlayerRegistry players, MapDropTable maps, MessageTable messages, Func<byte[], CancellationToken, Task> sendToDataServer)
    {
        _players = players;
        _maps = maps;
        _messages = messages;
        _sendToDataServer = sendToDataServer;
    }

    /// <summary>The clock (tests move it).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DateTime Now => Clock();

    private static Config.ServerInfoConfig ServerInfo => WorldPacketBuilder.ServerInfo;

    /// <summary>Port of CheckDuel: the two are fighting each other.</summary>
    public bool CheckDuel(PlayerObject a, PlayerObject b) =>
        a.DuelUser >= 0 && b.DuelUser >= 0 && a.DuelUser == b.Index && b.DuelUser == a.Index;

    private bool DuelAllowedOn(int map) => _maps.DuelEnabled(map, ServerInfo.DuelSwitch);

    private static bool Busy(PlayerObject p) => p.InChaosBox || p.InWarehouse || p.TargetShopNumber != null;

    private static bool InAnyDuelState(PlayerObject p) => p.DuelUserReserved >= 0 || p.DuelUserRequested >= 0 || p.DuelUser >= 0;

    private static void Reset(PlayerObject p)
    {
        p.DuelUserReserved = -1;
        p.DuelUserRequested = -1;
        p.DuelScore = 0;
        p.DuelUser = -1;
        p.DuelTick = DateTime.MinValue;
    }

    private string Text(int number, string fallback) => _messages.GetMessage(number) is { Length: > 0 } text ? text : fallback;

    private static Task Notice(PlayerObject to, string text, CancellationToken ct) =>
        to.Session.SendAsync(ChatPacketBuilder.NoticeSend(text, 1), ct);

    private async Task NoticeToAllAsync(string text, CancellationToken ct)
    {
        var packet = ChatPacketBuilder.NoticeSend(text);

        foreach (var player in _players.All)
        {
            if (player.WorldEntered)
            {
                await player.Session.SendAsync(packet, ct);
            }
        }
    }

    private PlayerObject? Find(int index) => index >= 0 && _players.TryGet(index, out var found) && found.WorldEntered ? found : null;

    // ---------------------------------------------------------------- the requests

    /// <summary>Port of CGDuelStartRecv: <paramref name="player"/> asks the player <paramref name="targetIndex"/>, named
    /// <paramref name="name"/>, to duel.</summary>
    public async Task HandleStartAsync(PlayerObject player, int targetIndex, string name, CancellationToken ct)
    {
        if (!DuelAllowedOn(player.Map))
        {
            await Notice(player, Text(167, "You can not duel in this map"), ct);
            return;
        }

        var target = Find(targetIndex);

        if (target == null || target.Index == player.Index)
        {
            await Notice(player, Text(160, "Target player is not online"), ct);
            return;
        }

        if (Busy(player) || Busy(target))
        {
            await Notice(player, Text(161, "You can not duel at this time"), ct);
            return;
        }

        if (InAnyDuelState(player))
        {
            await Notice(player, Text(163, "Have you ever asked or been asked to duel"), ct);
            return;
        }

        if (InAnyDuelState(target))
        {
            await Notice(player, Text(164, "Target player ever asked or been asked to duel"), ct);
            return;
        }

        if (!string.Equals(target.Name, name, StringComparison.Ordinal))
        {
            await Notice(player, Text(169, "Target player name is invalid"), ct);
            return;
        }

        if (player.SelfDefense.Any(s => s >= 0))
        {
            await Notice(player, Text(170, "You can not duel with Self Defense"), ct);
            return;
        }

        if (target.SelfDefense.Any(s => s >= 0))
        {
            await Notice(player, Text(171, "Player can not target duel Self Defense"), ct);
            return;
        }

        player.DuelUserReserved = target.Index;
        player.DuelUserRequested = -1;
        player.DuelScore = 0;
        player.DuelUser = -1;
        player.DuelTick = Now;

        target.DuelUserReserved = -1;
        target.DuelUserRequested = player.Index;
        target.DuelScore = 0;
        target.DuelUser = -1;
        target.DuelTick = Now;

        await target.Session.SendAsync(DuelPacketBuilder.OkSend(player.Index, player.Name), ct);
    }

    /// <summary>Port of CGDuelOkRecv: <paramref name="player"/> answers the request of the player
    /// <paramref name="askerIndex"/> (<paramref name="accept"/> true for yes).</summary>
    public async Task HandleAnswerAsync(PlayerObject player, bool accept, int askerIndex, string name, CancellationToken ct)
    {
        var asker = Find(askerIndex);

        if (asker == null)
        {
            return;
        }

        async Task Fail(string? notice)
        {
            if (notice != null)
            {
                await Notice(player, notice, ct);
            }

            Reset(player);
            Reset(asker);
            await asker.Session.SendAsync(DuelPacketBuilder.StartSend(0, player.Index, player.Name), ct);
        }

        if (!accept)
        {
            await Fail(Text(172, "Target player refused his request duel"));
            return;
        }

        if (player.DuelUserReserved >= 0 || player.DuelUser >= 0)
        {
            await Fail(Text(163, "Have you ever asked or been asked to duel"));
            return;
        }

        if (asker.DuelUserRequested >= 0 || asker.DuelUser >= 0 || asker.DuelUserReserved != player.Index)
        {
            await Fail(Text(164, "Target player ever asked or been asked to duel"));
            return;
        }

        if (!DuelAllowedOn(asker.Map))
        {
            await Fail(Text(167, "You can not duel in this map"));
            return;
        }

        if (!string.Equals(asker.Name, name, StringComparison.Ordinal))
        {
            await Fail(Text(169, "Target player name is invalid"));
            return;
        }

        foreach (var (duelist, other) in new[] { (player, asker), (asker, player) })
        {
            duelist.DuelUserReserved = -1;
            duelist.DuelUserRequested = -1;
            duelist.DuelScore = 0;
            duelist.DuelUser = other.Index;
            duelist.DuelTick = Now;
        }

        await player.Session.SendAsync(DuelPacketBuilder.StartSend(1, asker.Index, asker.Name), ct);
        await asker.Session.SendAsync(DuelPacketBuilder.StartSend(1, player.Index, player.Name), ct);
        await SendScoreAsync(player, asker, ct);

        if (ServerInfo.DuelAnnounceSwitch != 0)
        {
            await NoticeToAllAsync(BloodCastleManager.Fmt(Text(720, "Duel between %s and %s started!"), player.Name, asker.Name), ct);
        }
    }

    /// <summary>Port of CGDuelEndRecv: the player gives a request up or ends their duel.</summary>
    public async Task HandleEndAsync(PlayerObject player, CancellationToken ct)
    {
        if (Find(player.DuelUserReserved) is { } reserved)
        {
            Reset(reserved);
            await player.Session.SendAsync(DuelPacketBuilder.StartSend(0, reserved.Index, reserved.Name), ct);
            Reset(player);
            return;
        }

        if (Find(player.DuelUserRequested) is { } requester)
        {
            Reset(requester);
            await requester.Session.SendAsync(DuelPacketBuilder.StartSend(0, player.Index, player.Name), ct);
            Reset(player);
            return;
        }

        if (Find(player.DuelUser) is { } opponent)
        {
            Reset(opponent);
            await opponent.Session.SendAsync(DuelPacketBuilder.EndSend(opponent.Index, opponent.Name), ct);
            Reset(player);
            await player.Session.SendAsync(DuelPacketBuilder.EndSend(player.Index, player.Name), ct);
            return;
        }

        Reset(player); // the other side is gone
    }

    private async Task SendScoreAsync(PlayerObject first, PlayerObject second, CancellationToken ct)
    {
        var packet = DuelPacketBuilder.ScoreSend(first.Index, second.Index, (byte)first.DuelScore, (byte)second.DuelScore);
        await first.Session.SendAsync(packet, ct);
        await second.Session.SendAsync(packet, ct);
    }

    // ---------------------------------------------------------------- during the duel

    /// <summary>Port of the start of CAttack::Attack: a blow between duelists keeps the duel alive.</summary>
    public void Touch(PlayerObject attacker, PlayerObject target)
    {
        if (CheckDuel(attacker, target))
        {
            attacker.DuelTick = Now;
            target.DuelTick = Now;
        }
    }

    /// <summary>Port of UserDieProc and CheckDuelScore: <paramref name="killer"/> has killed <paramref name="victim"/>. In a duel
    /// that is a point for the killer; at the top score the duel ends, the winner in the ranking. The loser always comes
    /// back where they fell, not in town (<see cref="PlayerObject.RespawnInPlace"/>). False when it was no duel.</summary>
    public async Task<bool> OnKilledAsync(PlayerObject victim, PlayerObject killer, CancellationToken ct)
    {
        if (!CheckDuel(victim, killer))
        {
            return false;
        }

        killer.DuelScore++;
        await SendScoreAsync(victim, killer, ct);

        victim.RespawnInPlace = true;

        if (ServerInfo.DuelAnnounceSwitch == 2)
        {
            await NoticeToAllAsync(
                BloodCastleManager.Fmt(Text(721, "Duel [%s] (%d) VS (%d) [%s]"), killer.Name, killer.DuelScore, victim.DuelScore, victim.Name), ct);
        }

        if (killer.DuelScore >= ServerInfo.DuelMaxScore)
        {
            Reset(killer);
            await killer.Session.SendAsync(DuelPacketBuilder.EndSend(killer.Index, killer.Name), ct);
            await SaveRankingAsync(killer, win: 1, lose: 0, ct);

            Reset(victim);
            await victim.Session.SendAsync(DuelPacketBuilder.EndSend(victim.Index, victim.Name), ct);
            await SaveRankingAsync(victim, win: 0, lose: 1, ct);

            // The winner's effect (fireworks), seen by whoever sees the winner too (GCServerCommandSend with Viewport).
            var effect = BloodCastlePacketBuilder.ServerCommandSend(0, killer.X, killer.Y);
            await killer.Session.SendAsync(effect, ct);

            foreach (int observer in killer.VisibleTo.ToList())
            {
                if (_players.TryGet(observer, out var watcher) && watcher.WorldEntered)
                {
                    await watcher.Session.SendAsync(effect, ct);
                }
            }

            if (ServerInfo.DuelAnnounceSwitch != 0)
            {
                await NoticeToAllAsync(BloodCastleManager.Fmt(Text(722, "Player %s won %s duel!"), killer.Name, victim.Name), ct);
            }

            Log.Add(LogColor.Blue, "[Duel] '{0}' won the duel against '{1}'", killer.Name, victim.Name);
        }

        return true;
    }

    private Task SaveRankingAsync(PlayerObject player, uint win, uint lose, CancellationToken ct)
    {
        var w = new Shared.Protocol.PacketWriter();
        w.WriteUInt16((ushort)player.Index);
        w.WriteFixedString(player.Account, 11);
        w.WriteFixedString(player.Name, 11);
        w.WriteUInt32(win);
        w.WriteUInt32(lose);
        return _sendToDataServer(Shared.Protocol.PacketBuilder.BuildC1(0x3C, w.ToArray()), ct);
    }

    /// <summary>Port of CheckDuelUser for one player, once a second: a request nobody answers lapses after 30 seconds
    /// and a duel in which nobody lands a blow for a minute ends.</summary>
    public async Task OnSecondAsync(PlayerObject player, CancellationToken ct)
    {
        if (!InAnyDuelState(player))
        {
            return;
        }

        var idle = Now - player.DuelTick;

        if (player.DuelUserReserved >= 0 && idle > RequestTime)
        {
            if (Find(player.DuelUserReserved) is { } asked)
            {
                Reset(asked);
                await player.Session.SendAsync(DuelPacketBuilder.StartSend(0, asked.Index, asked.Name), ct);
            }

            Reset(player);
            return;
        }

        if (player.DuelUserRequested >= 0 && idle > RequestTime)
        {
            if (Find(player.DuelUserRequested) is { } requester)
            {
                Reset(requester);
                await requester.Session.SendAsync(DuelPacketBuilder.StartSend(0, player.Index, player.Name), ct);
            }

            Reset(player);
            return;
        }

        if (player.DuelUser >= 0 && idle > DuelIdleTime)
        {
            if (Find(player.DuelUser) is { } opponent)
            {
                Reset(opponent);
                await opponent.Session.SendAsync(DuelPacketBuilder.EndSend(opponent.Index, opponent.Name), ct);
            }

            Reset(player);
            await player.Session.SendAsync(DuelPacketBuilder.EndSend(player.Index, player.Name), ct);
        }
    }

    /// <summary>A player who leaves the game takes their requests and duel with them: the other side is told it is over.</summary>
    public async Task OnPlayerLeftAsync(PlayerObject player, CancellationToken ct)
    {
        if (!InAnyDuelState(player))
        {
            return;
        }

        foreach (int index in new[] { player.DuelUserReserved, player.DuelUserRequested })
        {
            if (Find(index) is { } waiting)
            {
                Reset(waiting);
                await waiting.Session.SendAsync(DuelPacketBuilder.StartSend(0, player.Index, player.Name), ct);
            }
        }

        if (Find(player.DuelUser) is { } opponent)
        {
            Reset(opponent);
            await opponent.Session.SendAsync(DuelPacketBuilder.EndSend(opponent.Index, opponent.Name), ct);
        }

        Reset(player);
    }
}
