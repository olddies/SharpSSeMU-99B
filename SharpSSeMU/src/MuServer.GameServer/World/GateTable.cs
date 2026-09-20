using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto de GATE_INFO (Gate.h:10-24) -- representa una fila de Data/Move/Gate.txt.
/// Defines gate entrance boundaries and spawn destination locations across maps.
/// </summary>
public sealed class GateEntry
{
    public required int Index { get; init; }
    public int Flag { get; init; } // 0=target location, 1=entrance portal, 2=spawn location
    public int Map { get; init; }
    public int StartX { get; init; }
    public int StartY { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public int TargetGate { get; init; }
    public int TargetDir { get; init; }
    public int MinLevel { get; init; }
    public int MaxLevel { get; init; }
}

/// <summary>
/// Puerto de CGate (Gate.cpp:12-242) -- maneja la carga y consulta de portales y puertas de entrada a mapas.
/// </summary>
public sealed class GateTable
{
    private readonly Dictionary<int, GateEntry> _gates = new();

    public GateEntry? Get(int index) => _gates.GetValueOrDefault(index);

    public void Load(string path)
    {
        _gates.Clear();

        var script = new MemScript();
        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[GateTable] Could not load {0}", path);
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
            int flag = script.GetAsNumber();
            int map = script.GetAsNumber();
            int startX = script.GetAsNumber();
            int startY = script.GetAsNumber();
            int endX = script.GetAsNumber();
            int endY = script.GetAsNumber();
            int targetGate = script.GetAsNumber();
            int targetDir = script.GetAsNumber();
            int minLevel = script.GetAsNumber();
            int maxLevel = script.GetAsNumber();

            // Skip MinReset, MaxReset tokens if present on line
            script.GetAsNumber();
            script.GetAsNumber();

            _gates[index] = new GateEntry
            {
                Index = index,
                Flag = flag,
                Map = map,
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                TargetGate = targetGate,
                TargetDir = targetDir,
                MinLevel = minLevel,
                MaxLevel = maxLevel,
            };
        }

        Log.Add(LogColor.Blue, "[GateTable] Loaded {0} gate definitions from {1}", _gates.Count, path);
    }
}
