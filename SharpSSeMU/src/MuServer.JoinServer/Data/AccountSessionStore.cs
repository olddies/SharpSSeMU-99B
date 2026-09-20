using System.Collections.Concurrent;
using MuServer.JoinServer.Util;
using MuServer.Shared.Logging;

namespace MuServer.JoinServer.Data;

/// <summary>
/// Puerto de CAccountManager: registro en memoria de qué cuentas están logueadas ahora mismo y en
/// qué GameServer, para no permitir doble-login y para poder forzar desconexión cuando cae un
/// GameServer. La clave se normaliza igual que el original (según CaseSensitive del .ini).
/// </summary>
public sealed class AccountSessionStore
{
    private const int MaxAccounts = 10000;
    private readonly ConcurrentDictionary<string, AccountSession> _sessions = new();
    private readonly bool _caseSensitive;

    public AccountSessionStore(bool caseSensitive)
    {
        _caseSensitive = caseSensitive;
    }

    public int Count => _sessions.Count;
    public bool IsFull => _sessions.Count >= MaxAccounts;

    private string Key(string account) => AccountUtil.NormalizeAccount(account, _caseSensitive);

    public bool TryGet(string account, out AccountSession session) =>
        _sessions.TryGetValue(Key(account), out session!);

    public void Upsert(AccountSession session) => _sessions[Key(session.Account)] = session;

    public void Remove(string account) => _sessions.TryRemove(Key(account), out _);

    /// <summary>Puerto de ClearServerAccountInfo: al caerse un GameServer, se limpian sus sesiones.</summary>
    public IReadOnlyList<AccountSession> ClearByServerCode(int serverCode)
    {
        var removed = new List<AccountSession>();

        foreach (var kv in _sessions)
        {
            if (kv.Value.GameServerCode == serverCode && _sessions.TryRemove(kv.Key, out var session))
            {
                removed.Add(session);
            }
        }

        return removed;
    }

    /// <summary>Puerto de DisconnectProc: expira cuentas "en movimiento entre mapas/servidores"
    /// después de 30s sin completar el cambio (igual que el original).</summary>
    public IReadOnlyList<AccountSession> SweepStaleMapMoves()
    {
        var expired = new List<AccountSession>();
        var now = DateTime.UtcNow;

        foreach (var kv in _sessions)
        {
            if (kv.Value.MapServerMove && (now - kv.Value.MapServerMoveTime) >= TimeSpan.FromSeconds(30))
            {
                if (_sessions.TryRemove(kv.Key, out var session))
                {
                    expired.Add(session);
                }
            }
        }

        return expired;
    }
}
