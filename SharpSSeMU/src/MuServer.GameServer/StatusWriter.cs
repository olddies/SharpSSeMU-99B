using System.Text.Json;

namespace MuServer.GameServer;

/// <summary>Estado en vivo del servidor, en un JSON chico que otros procesos pueden leer sin
/// acoplarse al GameServer. Se eligió un archivo en vez de abrir un puerto HTTP acá: el GameServer
/// ya tiene su propio ciclo de vida y su propio socket de juego, y agregarle un servidor web propio
/// para esto es más riesgo del que vale un status de lectura. El panel de administración
/// (MuServer.AdminPanel) lo lee por polling.</summary>
/// <summary>Una fila de la lista de "Cuentas conectadas" del panel -- <c>Class</c> es el índice 0-4
/// (DW/DK/FE/MG/DL) que ya usa <see cref="World.PlayerObject.Class"/>, no el byte crudo de
/// evolución del protocolo real (este servidor no distingue 2da/3ra clase todavía).</summary>
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
                // Escritura atómica: un panel leyendo justo en el medio de un write directo podría
                // encontrar un JSON a mitad de escribir. Se escribe a un archivo temporal y se
                // reemplaza, que en la mayoría de los filesystems es una operación atómica.
                var tmpPath = _path + ".tmp";
                await File.WriteAllTextAsync(tmpPath, json, ct);
                File.Move(tmpPath, _path, overwrite: true);
            }
            catch (Exception)
            {
                // Un fallo al escribir el status no debe tirar abajo el servidor -- es información
                // secundaria, no algo de lo que dependa el juego.
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
