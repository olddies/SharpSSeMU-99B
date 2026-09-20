using System.Text.Json;

namespace MuServer.GameServer;

/// <summary>Live state of the server, in a small JSON that other processes can read without coupling to the
/// GameServer. A file was chosen instead of opening an HTTP port here: the GameServer already has its own life
/// cycle and its own game socket, and adding its own web server for this is more risk than a read-only status
/// is worth. The administration panel (MuServer.AdminPanel) reads it by polling.</summary> <summary>A row of
/// the panel's "Connected accounts" list -- <c>Class</c> is the 0-4 index (DW/DK/FE/MG/DL) that <see
/// cref="World.PlayerObject.Class"/> already uses, not the raw evolution byte of the real protocol (this server
/// does not distinguish 2nd/3rd class yet).</summary>
public sealed record OnlinePlayerInfo(string Account, string Name, int Level, int Reset, byte Class, byte Map);

public sealed record GameServerStatus(
    string ServerName, int PlayerCount, int MaxPlayers, DateTime StartedAtUtc, DateTime UpdatedAtUtc,
    IReadOnlyList<OnlinePlayerInfo> Players);

public sealed class StatusWriter
{
    private readonly string _path;
    private readonly string _serverName;
    private readonly int _maxPlayers;
    private readonly Func<int> _getPlayerCount;
    private readonly Func<IReadOnlyList<OnlinePlayerInfo>> _getPlayers;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public StatusWriter(string dataDirectory, string serverName, int maxPlayers, Func<int> getPlayerCount,
        Func<IReadOnlyList<OnlinePlayerInfo>> getPlayers)
    {
        _path = Path.Combine(dataDirectory, "status.json");
        _serverName = serverName;
        _maxPlayers = maxPlayers;
        _getPlayerCount = getPlayerCount;
        _getPlayers = getPlayers;
    }

    public void Start(CancellationToken ct)
    {
        _ = RunAsync(ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = new GameServerStatus(
                    _serverName, _getPlayerCount(), _maxPlayers, _startedAt, DateTime.UtcNow, _getPlayers());
                var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
                // Atomic write: a panel reading right in the middle of a direct write could find a half-written
                // JSON. It is written to a temporary file and replaced, which on most filesystems is an atomic
                // operation.
                var tmpPath = _path + ".tmp";
                await File.WriteAllTextAsync(tmpPath, json, ct);
                File.Move(tmpPath, _path, overwrite: true);
            }
            catch (Exception)
            {
                // A failure writing the status must not bring the server down -- it is secondary information,
                // not something the game depends on.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
