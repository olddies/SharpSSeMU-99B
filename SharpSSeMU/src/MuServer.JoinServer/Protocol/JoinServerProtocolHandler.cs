using MuServer.JoinServer.Data;
using MuServer.JoinServer.Db;
using MuServer.JoinServer.Util;
using MuServer.Shared.Logging;

namespace MuServer.JoinServer.Protocol;

/// <summary>Puerto de JoinServerProtocolCore + los GJXxxRecv/JGXxxSend del original.</summary>
public sealed class JoinServerProtocolHandler
{
    private const int MaxAccount = 10000;

    private readonly IAccountRepository _repo;
    private readonly AccountSessionStore _sessions;
    private readonly GameServerRegistry _servers;
    private readonly bool _caseSensitive;
    private readonly bool _md5Encryption;

    public JoinServerProtocolHandler(IAccountRepository repo, AccountSessionStore sessions, GameServerRegistry servers, bool caseSensitive, bool md5Encryption)
    {
        _repo = repo;
        _sessions = sessions;
        _servers = servers;
        _caseSensitive = caseSensitive;
        _md5Encryption = md5Encryption;
    }

    public async Task HandlePacketAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        if (packet.Length < 3)
        {
            return;
        }

        byte head = packet[2];

        switch (head)
        {
            case 0x00 when packet.Length >= ServerInfoRecv.Size:
                OnServerInfo(link, ServerInfoRecv.Parse(packet));
                break;

            case 0x01 when packet.Length >= ConnectAccountRecv.Size:
                await OnConnectAccountAsync(link, ConnectAccountRecv.Parse(packet), ct);
                break;

            case 0x02 when packet.Length >= DisconnectAccountRecv.Size:
                await OnDisconnectAccountAsync(link, DisconnectAccountRecv.Parse(packet), ct);
                break;

            case 0x05 when packet.Length >= AccountLevelRecv.Size:
                await OnAccountLevelAsync(link, AccountLevelRecv.Parse(packet), ct);
                break;

            case 0x11 when packet.Length >= AccountLevelSaveRecv.Size:
                await OnAccountLevelSaveAsync(AccountLevelSaveRecv.Parse(packet), ct);
                break;

            case 0x20 when packet.Length >= ServerUserInfoRecv.Size:
                OnServerUserInfo(link, ServerUserInfoRecv.Parse(packet));
                break;

            case 0x30 when packet.Length >= ExternalDisconnectAccountRecv.Size:
                await OnExternalDisconnectAsync(ExternalDisconnectAccountRecv.Parse(packet), ct);
                break;
        }
    }

    private void OnServerInfo(GameServerLink link, ServerInfoRecv msg)
    {
        link.ServerName = msg.ServerName;
        link.ServerPort = msg.ServerPort;
        link.ServerCode = msg.ServerCode;

        Log.Add(LogColor.Green, "[SocketUDP] GameServer connected [{0}] [{1}:{2}][{3}]",
            link.ServerName, link.IpAddress, link.ServerPort, link.ServerCode);
    }

    private void OnServerUserInfo(GameServerLink link, ServerUserInfoRecv msg)
    {
        link.CurUserCount = msg.CurUserCount;
        link.MaxUserCount = msg.MaxUserCount;
    }

    private async Task OnConnectAccountAsync(GameServerLink link, ConnectAccountRecv msg, CancellationToken ct)
    {
        // result: 1=ok by default in the original until something explicitly fails.
        byte result = 1;

        if (!AccountUtil.CheckTextSyntax(msg.Account))
        {
            await link.SendAsync(JoinServerPacketBuilder.ConnectAccountSend(msg.Index, msg.Account, "", 2, 0, 0, ""), ct);
            return;
        }

        if (_sessions.TryGet(msg.Account, out var existing))
        {
            await link.SendAsync(JoinServerPacketBuilder.ConnectAccountSend(msg.Index, msg.Account, "", 3, 0, 0, ""), ct);

            var otherLink = _servers.FindByCode(existing.GameServerCode);
            if (otherLink != null)
            {
                await otherLink.SendAsync(JoinServerPacketBuilder.AccountAlreadyConnectedSend(existing.UserIndex, existing.Account), ct);
            }

            return;
        }

        if (_sessions.IsFull)
        {
            await link.SendAsync(JoinServerPacketBuilder.ConnectAccountSend(msg.Index, msg.Account, "", 4, 0, 0, ""), ct);
            return;
        }

        var credentials = await _repo.GetCredentialsAsync(msg.Account, ct);

        if (credentials == null || credentials.Account != msg.Account)
        {
            await link.SendAsync(JoinServerPacketBuilder.ConnectAccountSend(msg.Index, msg.Account, "", 2, 0, 0, ""), ct);
            return;
        }

        if (_md5Encryption)
        {
            // The original's MD5 mode uses a "key index" scheme of its own (MD5_KEYVAL) over the password hash,
            // not plain standard MD5. It is not ported yet — the connection is left in plain-text mode
            // (MD5Encryption=0) as the only supported mode for now.
            Log.Add(LogColor.Red, "MD5Encryption=1 is not supported yet in this port; use MD5Encryption=0.");
            await link.SendAsync(JoinServerPacketBuilder.ConnectAccountSend(msg.Index, msg.Account, "", 2, 0, 0, ""), ct);
            return;
        }

        if (credentials.Password != msg.Password)
        {
            await link.SendAsync(JoinServerPacketBuilder.ConnectAccountSend(msg.Index, msg.Account, "", 0, 0, 0, ""), ct);
            return;
        }

        var personal = await _repo.GetPersonalInfoAsync(msg.Account, ct);

        if (personal == null)
        {
            await link.SendAsync(JoinServerPacketBuilder.ConnectAccountSend(msg.Index, msg.Account, "", 2, 0, 0, ""), ct);
            return;
        }

        var levelInfo = await _repo.GetAccountLevelAsync(msg.Account, ct);

        await _repo.ConnectMembAsync(msg.Account, link.ServerName, msg.IpAddress, ct);

        var expireText = levelInfo.AccountExpire.ToString("yyyy-MM-dd HH:mm:ss");

        await link.SendAsync(JoinServerPacketBuilder.ConnectAccountSend(
            msg.Index, msg.Account, personal.PersonalCode, result, (byte)personal.BlockCode, (ushort)levelInfo.AccountLevel, expireText), ct);

        var session = new AccountSession
        {
            Account = msg.Account,
            IpAddress = msg.IpAddress,
            UserIndex = msg.Index,
            GameServerCode = link.ServerCode,
        };

        _sessions.Upsert(session);

        Log.Add(LogColor.Black, "[AccountInfo] Account connected (Account: {0}, IpAddress: {1}, GameServerCode: {2})",
            session.Account, session.IpAddress, session.GameServerCode);
    }

    private async Task OnDisconnectAccountAsync(GameServerLink link, DisconnectAccountRecv msg, CancellationToken ct)
    {
        byte result = 1;

        if (!_sessions.TryGet(msg.Account, out var session)
            || session.UserIndex != msg.Index
            || session.GameServerCode != link.ServerCode
            || (session.MapServerMove && (DateTime.UtcNow - session.MapServerMoveTime) < TimeSpan.FromSeconds(30)))
        {
            await link.SendAsync(JoinServerPacketBuilder.DisconnectAccountSend(msg.Index, msg.Account, 0), ct);
            return;
        }

        await _repo.DisconnectMembAsync(msg.Account, ct);

        await link.SendAsync(JoinServerPacketBuilder.DisconnectAccountSend(msg.Index, msg.Account, result), ct);

        _sessions.Remove(msg.Account);

        Log.Add(LogColor.Black, "[AccountInfo] Account disconnected (Account: {0}, IpAddress: {1}, GameServerCode: {2})",
            session.Account, session.IpAddress, session.GameServerCode);
    }

    private async Task OnAccountLevelAsync(GameServerLink link, AccountLevelRecv msg, CancellationToken ct)
    {
        if (!_sessions.TryGet(msg.Account, out _))
        {
            return;
        }

        var levelInfo = await _repo.GetAccountLevelAsync(msg.Account, ct);
        var expireText = levelInfo.AccountExpire.ToString("yyyy-MM-dd HH:mm:ss");

        await link.SendAsync(JoinServerPacketBuilder.AccountLevelSend(msg.Index, msg.Account, (ushort)levelInfo.AccountLevel, expireText), ct);
    }

    private async Task OnAccountLevelSaveAsync(AccountLevelSaveRecv msg, CancellationToken ct)
    {
        await _repo.SetAccountLevelAsync(msg.Account, msg.AccountLevel, (int)msg.AccountExpireTime, ct);
    }

    private async Task OnExternalDisconnectAsync(ExternalDisconnectAccountRecv msg, CancellationToken ct)
    {
        if (!_sessions.TryGet(msg.Account, out var session))
        {
            return;
        }

        var link = _servers.FindByCode(session.GameServerCode);

        if (link == null)
        {
            return;
        }

        await link.SendAsync(JoinServerPacketBuilder.DisconnectAccountSend(session.UserIndex, session.Account, 0), ct);
    }

    /// <summary>Puerto de ClearServerAccountInfo: se llama cuando un GameServer se desconecta,
    /// fuerza logout (en BD y en memoria) de todas sus cuentas activas.</summary>
    public async Task ClearServerAccountsAsync(GameServerLink link, CancellationToken ct)
    {
        if (link.ServerCode == 0xFFFF)
        {
            return;
        }

        foreach (var session in _sessions.ClearByServerCode(link.ServerCode))
        {
            await _repo.DisconnectMembAsync(session.Account, ct);

            Log.Add(LogColor.Black, "[AccountInfo] Account disconnected by clear (Account: {0}, IpAddress: {1}, GameServerCode: {2})",
                session.Account, session.IpAddress, session.GameServerCode);
        }
    }

    /// <summary>Port of CAccountManager::DisconnectProc: runs every 1s (the original's TIMER_1000), expires
    /// accounts whose "movement between servers" got stuck for more than 30s.</summary>
    public async Task RunStaleMapMoveSweepAsync(CancellationToken ct)
    {
        foreach (var session in _sessions.SweepStaleMapMoves())
        {
            await _repo.DisconnectMembAsync(session.Account, ct);

            Log.Add(LogColor.Black, "[AccountInfo] Account disconnected by proc (Account: {0}, IpAddress: {1}, GameServerCode: {2})",
                session.Account, session.IpAddress, session.GameServerCode);

            var link = _servers.FindByCode(session.GameServerCode);

            if (link != null)
            {
                await link.SendAsync(JoinServerPacketBuilder.DisconnectAccountSend(session.UserIndex, session.Account, 0), ct);
            }
        }
    }
}
