using System.Collections.Concurrent;

namespace MuServer.GameServer.World;

/// <summary>Reemplazo idiomático de CParty::m_PartyInfo[MAX_OBJECT] -- diccionario de grupos activos
/// por ID incremental (ver comentario de <see cref="PartyGroup"/> sobre por qué no se replica el
/// escaneo de slot libre del array plano original).</summary>
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
