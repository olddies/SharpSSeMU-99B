using System.Text.Json;
using MuServer.AdminPanel.Config;

namespace MuServer.AdminPanel.Repositories;

/// <summary>A connected account -- same shape as <c>MuServer.GameServer.OnlinePlayerInfo</c> (the type is
/// deliberately not shared between projects, see the doc-comment of <see cref="ServerStatusInfo"/>).
/// <c>Class</c> is the 0-4 index (DW/DK/FE/MG/DL); <see cref="ClassName"/> translates it for display.</summary>
public sealed record OnlinePlayerRow(string Account, string Name, int Level, int Reset, byte Class, byte Map)
{
    public string ClassName => Class switch
    {
        0 => "Dark Wizard",
        1 => "Dark Knight",
        2 => "Fairy Elf",
        3 => "Magic Gladiator",
        4 => "Dark Lord",
        _ => $"Clase {Class}",
    };
}

public sealed record ServerStatusInfo(
    string ServerName, int PlayerCount, int MaxPlayers, DateTime StartedAtUtc, DateTime UpdatedAtUtc,
    IReadOnlyList<OnlinePlayerRow> Players)
{
    /// <summary>The GameServer rewrites this file every 3s (see StatusWriter.cs). If it has not been updated
    /// for more than 15s, the process is most likely down or hung -- it is interpreted as "offline" instead of
    /// showing a player count that might no longer be true.</summary>
    public bool IsStale => DateTime.UtcNow - UpdatedAtUtc > TimeSpan.FromSeconds(15);

    public TimeSpan Uptime => DateTime.UtcNow - StartedAtUtc;
}

public sealed class ServerStatusRepository(GameDataPaths dataPaths)
{
    public ServerStatusInfo? Read()
    {
        try
        {
            if (!File.Exists(dataPaths.StatusJson))
            {
                return null;
            }

            using var stream = File.Open(dataPaths.StatusJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<ServerStatusInfo>(stream);
        }
        catch (IOException)
        {
            // Read right in the middle of a write (the GameServer uses temp+move, so this should be extremely
            // rare, but a stale status on the next query is preferable to throwing an exception at the user).
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
