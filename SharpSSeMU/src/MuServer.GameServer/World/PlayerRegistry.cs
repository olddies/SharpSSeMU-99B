using System.Collections.Concurrent;

namespace MuServer.GameServer.World;

/// <summary>Idiomatic replacement for the "users" slice of gObj[10000] (indices 9000-9999,
/// OBJECT_START_USER..MAX_OBJECT) -- here simply a dictionary of online players.</summary>
public sealed class PlayerRegistry
{
    private readonly ConcurrentDictionary<int, PlayerObject> _players = new();

    public void Add(PlayerObject player) => _players[player.Index] = player;

    public void Remove(int index) => _players.TryRemove(index, out _);

    public bool TryGet(int index, out PlayerObject player) => _players.TryGetValue(index, out player!);

    public IEnumerable<PlayerObject> All => _players.Values;

    public int Count => _players.Count;
}
