using System.Text.Json;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Leaves a "global message" command for the GameServer to pick up -- see GlobalMessagePoller.cs on
/// the server side (polls every 2s, deletes the file once it has processed it). The panel does not need to read
/// this file back: "Saved" here only means the command was written, not that it has reached the players
/// yet.</summary>
public static class GlobalMessageRepository
{
    /// <summary>0 = noticia dorada en pantalla, 1 = mensaje azul en el chatbox -- mismos valores que
    /// el byte "type" de ChatPacketBuilder.NoticeSend.</summary>
    public static void Send(string dataDirectory, string message, byte type)
    {
        var command = new { Id = Guid.NewGuid().ToString("N"), Message = message, Type = type };
        var json = JsonSerializer.Serialize(command, new JsonSerializerOptions { WriteIndented = true });

        var path = Path.Combine(dataDirectory, "global-message.json");
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }
}
