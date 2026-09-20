using System.Text.Json;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Deja un comando "mensaje global" para que el GameServer lo recoja -- ver
/// GlobalMessagePoller.cs del lado del servidor (poll cada 2s, borra el archivo una vez que lo
/// procesa). El panel no necesita leer de vuelta este archivo: "Guardado" acá sólo significa que el
/// comando quedó escrito, no que ya llegó a los jugadores.</summary>
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
