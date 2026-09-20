using System.Text.Json;
using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;

namespace MuServer.GameServer;

/// <summary>"Send global message" command that MuServer.AdminPanel leaves lying around as a file -- same spirit
/// as <see cref="StatusWriter"/> but in the other direction (the panel writes, the GameServer reads). A file
/// instead of a socket/HTTP because it is a low-frequency, single-type command; if more commands are needed
/// from the panel in the future, this is a candidate to be generalised into a real queue -- there is no need
/// before having a second use case.</summary>
public sealed record GlobalMessageCommand(string Id, string Message, byte Type);

public sealed class GlobalMessagePoller
{
    private readonly string _path;
    private readonly PlayerRegistry _players;
    private string? _lastProcessedId;

    public GlobalMessagePoller(string dataDirectory, PlayerRegistry players)
    {
        _path = Path.Combine(dataDirectory, "global-message.json");
        _players = players;
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
                await CheckOnceAsync(ct);
            }
            catch (Exception)
            {
                // A malformed command must not take the server down -- it is ignored and retried on the next
                // poll (if the panel rewrites it corrected, it is picked up normally).
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        GlobalMessageCommand? command;
        using (var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            command = await JsonSerializer.DeserializeAsync<GlobalMessageCommand>(stream, cancellationToken: ct);
        }

        if (command is null || command.Id == _lastProcessedId)
        {
            return;
        }

        _lastProcessedId = command.Id;

        var packet = ChatPacketBuilder.NoticeSend(command.Message, command.Type);
        foreach (var player in _players.All)
        {
            await player.Session.SendAsync(packet, ct);
        }

        // It is deleted so that a GameServer that starts later does not resend an old message the panel already
        // considered sent -- the panel does not need to read this file back, only write it.
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
