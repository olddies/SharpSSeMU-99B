using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> Port of CMessage (Message.cpp:10-90) -- loads and provides translated messages by index from
/// Data/Message.txt. </summary>
public sealed class MessageTable
{
    private readonly Dictionary<int, string> _messages = new();

    public string GetMessage(int index) => _messages.GetValueOrDefault(index) ?? $"Could not find message {index}!";

    public void Load(string path)
    {
        _messages.Clear();

        var script = new MemScript();
        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MessageTable] Could not load {0}", path);
            return;
        }

        while (true)
        {
            var token = script.GetToken();
            if (token == TokenResult.End)
            {
                break;
            }

            if (token == TokenResult.String && script.GetString() == "end")
            {
                break;
            }

            int index = script.GetNumber();
            string message = script.GetAsString();

            _messages[index] = message;
        }

        Log.Add(LogColor.Blue, "[MessageTable] Loaded {0} messages from {1}", _messages.Count, path);
    }
}
