using System.Text.Json;
using MuServer.AdminPanel.Config;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Una cuenta conectada -- mismo shape que <c>MuServer.GameServer.OnlinePlayerInfo</c>
/// (no se comparte el tipo entre proyectos a propósito, ver doc-comment de <see cref="ServerStatusInfo"/>).
/// <c>Class</c> es el índice 0-4 (DW/DK/FE/MG/DL); <see cref="ClassName"/> lo traduce para mostrar.</summary>
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
    /// <summary>El GameServer reescribe este archivo cada 3s (ver StatusWriter.cs). Si hace más de
    /// 15s que no se actualiza, lo más probable es que el proceso esté caído o colgado -- se
    /// interpreta como "sin conexión" en vez de mostrar un número de jugadores que podría ya no
    /// ser cierto.</summary>
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
            // Leído justo en el medio de una escritura (el GameServer usa temp+move, así que esto
            // debería ser rarísimo, pero un status viejo en la próxima consulta es preferible a
            // tirar una excepción al usuario).
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
