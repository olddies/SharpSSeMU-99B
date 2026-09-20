using System.Collections.Concurrent;
using MuServer.Shared.Logging;

namespace MuServer.JoinServer.Data;

/// <summary>Registro de GameServers conectados (máximo 20, igual que MAX_SERVER del original).</summary>
public sealed class GameServerRegistry
{
    private const int MaxServers = 20;
    private readonly ConcurrentDictionary<Guid, GameServerLink> _links = new();

    public int Count => _links.Count;

    public bool TryAdd(GameServerLink link)
    {
        if (_links.Count >= MaxServers)
        {
            return false;
        }

        return _links.TryAdd(link.Id, link);
    }

    public void Remove(GameServerLink link)
    {
        if (_links.TryRemove(link.Id, out _) && link.ServerCode != 0xFFFF)
        {
            Log.Add(LogColor.Red, "[SocketUDP] GameServer disconnected [{0}] [{1}:{2}][{3}]",
                link.ServerName, link.IpAddress, link.ServerPort, link.ServerCode);
        }
    }

    public GameServerLink? FindByCode(int serverCode) =>
        _links.Values.FirstOrDefault(l => l.ServerCode == serverCode);

    public IReadOnlyCollection<GameServerLink> Snapshot() => _links.Values.ToArray();
}
