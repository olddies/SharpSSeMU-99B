using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.ConnectServer.Data;

/// <summary>Puerto de CBlackList: lista de IPs bloqueadas cargada desde BlackList.txt.</summary>
public sealed class BlackListStore
{
    private readonly HashSet<string> _blocked = new();
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
            _blocked.Clear();

            while (true)
            {
                if (script.GetToken() == TokenResult.End)
                {
                    break;
                }

                var value = script.GetString();

                if (value == "end")
                {
                    break;
                }

                _blocked.Add(value);
            }
        }

        Log.Add(LogColor.Blue, "BlackList loaded: {0} blocked IPs", _blocked.Count);
    }

    /// <summary>true = allowed (not in the blacklist), false = blocked.</summary>
    public bool IsAllowed(string ipAddress)
    {
        lock (_sync)
        {
            return !_blocked.Contains(ipAddress);
        }
    }
}
