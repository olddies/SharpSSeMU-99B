namespace MuServer.GameServer.Config;

/// <summary>Start-up check that the game data the GameServer cannot run without is there. The game tables
/// (items, monsters, spawns, shops, terrain, the encryption keys...) are not part of this project: they come from
/// the user's own original package, so a fresh checkout or a release zip starts with none of them. Without this
/// check the server used to die on whichever file it happened to read first, with a single line that did not say
/// what to do. Paths come from <see cref="GameServerConfig"/>, so an override in GameServer.ini is honoured.
/// </summary>
public static class GameDataCheck
{
    /// <summary>Every required file or folder that does not exist, as the full paths the server would read.</summary>
    public static List<string> FindMissing(GameServerConfig config)
    {
        // Gate.txt, Move.txt and Message.txt are looked for next to the item table, as Program.cs does.
        string dataDir = Path.GetDirectoryName(config.ItemPath) is { Length: > 0 } itemDir
            ? Path.GetFullPath(Path.Combine(itemDir, ".."))
            : "Data";

        var files = new[]
        {
            config.EncryptionKeyPath, config.DecryptionKeyPath,
            config.ItemPath, config.MonsterListPath, config.SkillListPath, config.ShopManagerPath,
            config.QuestPath, config.QuestObjectivePath, config.QuestRewardPath,
            Path.Combine(config.EventPath, "DevilSquare.dat"),
            Path.Combine(config.EventPath, "EventEntryLevel.dat"),
            Path.Combine(config.EventPath, "EventStageSpawn.dat"),
            Path.Combine(dataDir, "Move", "Gate.txt"),
            Path.Combine(dataDir, "Move", "Move.txt"),
            Path.Combine(dataDir, "Message.txt"),
        };

        var folders = new[] { config.TerrainPath, config.MonsterSpawnPath, config.ShopDataPath };

        var missing = files.Where(path => !File.Exists(path)).ToList();
        missing.AddRange(folders.Where(path => !Directory.Exists(path)));
        return missing;
    }
}
