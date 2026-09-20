using System.Collections.Concurrent;

namespace MuServer.GameServer.World;

/// <summary>Idiomatic replacement for CParty::m_PartyInfo[MAX_OBJECT] -- dictionary of active parties by
/// incremental ID (see the comment of <see cref="PartyGroup"/> about why the flat original array's free-slot
/// scan is not replicated).</summary>
public sealed class PartyRegistry
{
    private readonly ConcurrentDictionary<int, PartyGroup> _parties = new();
    private int _nextId;

    public PartyGroup Create()
    {
        var id = System.Threading.Interlocked.Increment(ref _nextId);
        var group = new PartyGroup { Id = id };
        _parties[id] = group;
        return group;
    }

    public bool TryGet(int id, out PartyGroup group) => _parties.TryGetValue(id, out group!);

    public void Remove(int id) => _parties.TryRemove(id, out _);

    public IEnumerable<PartyGroup> All => _parties.Values;
}
