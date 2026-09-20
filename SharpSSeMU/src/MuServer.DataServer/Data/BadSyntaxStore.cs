using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.DataServer.Data;

/// <summary>Puerto de CBadSyntax: lista de substrings prohibidos en nombres de personaje (BadSyntax.txt).</summary>
public sealed class BadSyntaxStore
{
    private readonly List<string> _banned = new();
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
            _banned.Clear();

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

                _banned.Add(value);
            }
        }

        Log.Add(LogColor.Blue, "BadSyntax loaded: {0} entries", _banned.Count);
    }

    public bool CheckSyntax(string text)
    {
        lock (_sync)
        {
            return !_banned.Any(b => text.Contains(b, StringComparison.Ordinal));
        }
    }
}
