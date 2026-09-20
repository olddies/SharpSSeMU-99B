using System.Text.Json;
using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;

namespace MuServer.GameServer;

/// <summary>Comando "mandar mensaje global" que MuServer.AdminPanel deja tirado como archivo --
/// mismo espíritu que <see cref="StatusWriter"/> pero en la otra dirección (el panel escribe, el
/// GameServer lee). Un archivo en vez de un socket/HTTP porque es un comando de baja frecuencia y
/// de un solo tipo; si en el futuro se necesitan más comandos desde el panel, esto es candidato a
/// generalizarse a una cola real -- no hace falta antes de tener un segundo caso de uso.</summary>
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
                // Un comando mal formado no debe tumbar el servidor -- se ignora y se reintenta en
                // el próximo poll (si el panel lo reescribe corregido, se recoge normalmente).
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

        // Se borra para que un GameServer que arranca después no reenvíe un mensaje viejo que el
        // panel ya dio por mandado -- el panel no necesita leer de vuelta este archivo, sólo escribirlo.
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
