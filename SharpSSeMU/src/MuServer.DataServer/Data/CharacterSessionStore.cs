using System.Collections.Concurrent;

namespace MuServer.DataServer.Data;

/// <summary> Port of CCharacterManager: which characters are online right now (for cross-server whisper and to
/// be able to clean everything up if the owning GameServer goes down). Key always lowercase, same as the
/// original (here it does not depend on any configuration flag). </summary>
public sealed class CharacterSessionStore
{
    private readonly ConcurrentDictionary<string, CharacterSession> _sessions = new();

    private static string Key(string name) => name.ToLowerInvariant();

    public bool TryGet(string name, out CharacterSession session) =>
        _sessions.TryGetValue(Key(name), out session!);

    public void Upsert(CharacterSession session) => _sessions[Key(session.Name)] = session;

    public void Remove(string name) => _sessions.TryRemove(Key(name), out _);

    public IReadOnlyList<CharacterSession> ClearByServerCode(int serverCode)
    {
        var removed = new List<CharacterSession>();

        foreach (var kv in _sessions)
        {
            if (kv.Value.GameServerCode == serverCode && _sessions.TryRemove(kv.Key, out var session))
            {
                removed.Add(session);
            }
        }

        return removed;
    }
}
