using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.JoinServer.Data;

/// <summary>
/// Puerto de CAllowableIpList: whitelist de IPs desde las que se acepta que un GameServer
/// se conecte al JoinServer (AllowableIpList.txt). Formato: "0" abre la sección, luego IPs entre
/// comillas, "end" la cierra.
/// </summary>
public sealed class AllowableIpStore
{
    private readonly HashSet<string> _allowed = new();
    private readonly object _sync = new();

    public void Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, script.GetLastError());
            return;
        }

        lock (_sync)
        {
            _allowed.Clear();

            while (true)
            {
                if (script.GetToken() == TokenResult.End)
                {
                    break;
                }

                var section = script.GetNumber();

                if (section != 0)
                {
                    continue;
                }

                while (true)
                {
                    var value = script.GetAsString();

                    if (value == "end")
                    {
                        break;
                    }

                    _allowed.Add(value);
                }
            }
        }

        Log.Add(LogColor.Blue, "AllowableIpList loaded: {0} allowed IPs", _allowed.Count);
    }

    public bool IsAllowed(string ipAddress)
    {
        lock (_sync)
        {
            return _allowed.Contains(ipAddress);
        }
    }
}
