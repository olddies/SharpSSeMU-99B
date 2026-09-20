namespace MuServer.AdminPanel.Config;

/// <summary>Root of the GameServer's real `Data/` that this panel reads and writes. A single point of truth so
/// that each repository builds its own path (`Move/Move.txt`, `Item/Item.txt`, etc.) without repeating the
/// calculation of "where Data is".</summary>
public sealed class GameDataPaths(string root)
{
    public string Root { get; } = root;

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string StatusJson => Combine("status.json");
}
