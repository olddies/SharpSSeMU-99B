using System.Collections.Concurrent;

namespace MuServer.ConnectServer.Net;

/// <summary>Cupo global de conexiones (equivalente a MAX_CLIENT=100 del original) + registro de sesiones activas.</summary>
public sealed class ClientSessionManager
{
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();
    private readonly int _maxClients;

    public ClientSessionManager(int maxClients = 100)
    {
        _maxClients = maxClients;
    }

    public int Count => _sessions.Count;

    public bool TryAdd(ClientSession session)
    {
        if (_sessions.Count >= _maxClients)
        {
            return false;
        }

        return _sessions.TryAdd(session.Id, session);
    }

    public void Remove(ClientSession session)
    {
        _sessions.TryRemove(session.Id, out _);
    }

    public IReadOnlyCollection<ClientSession> Snapshot() => _sessions.Values.ToArray();
}
