using System;
using System.Collections.Generic;
using System.IO;
using MuServer.GameServer.Config;
using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

public sealed class MoveEntry
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required uint RequireMoney { get; init; }
    public required int MinLevel { get; init; }
    public required int MaxLevel { get; init; }
    public required int MinReset { get; init; }
    public required int MaxReset { get; init; }
    public required int GateNumber { get; init; }
}

public sealed class MoveTable
{
    private readonly Dictionary<int, MoveEntry> _byIndex = new();
    private readonly Dictionary<string, MoveEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public MoveEntry? Get(int index) => _byIndex.GetValueOrDefault(index);
    public MoveEntry? GetByName(string name) => _byName.GetValueOrDefault(name);
    public IEnumerable<MoveEntry> All => _byIndex.Values;

    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            Log.Add(LogColor.Red, "[MoveTable] File not found: {0}", path);
            return;
        }

        var script = new MemScript();
        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MoveTable] Read error: {0}", script.GetLastError());
            return;
        }

        _byIndex.Clear();
        _byName.Clear();

        while (true)
        {
            var token = script.GetToken();
            if (token == TokenResult.End) break;

            if (token == TokenResult.String && script.GetString() == "end") break;

            int index = script.GetNumber();
            string name = script.GetAsString();
            uint money = (uint)script.GetAsNumber();
            int minLevel = script.GetAsNumber();
            int maxLevel = script.GetAsNumber();
            int minReset = script.GetAsNumber();
            int maxReset = script.GetAsNumber();

            int al0 = script.GetAsNumber();
            int al1 = script.GetAsNumber();
            int al2 = script.GetAsNumber();
            int al3 = script.GetAsNumber();

            int gateNumber = script.GetAsNumber();

            var entry = new MoveEntry
            {
                Index = index,
                Name = name,
                RequireMoney = money,
                MinLevel = minLevel,
                MaxLevel = maxLevel,
                MinReset = minReset,
                MaxReset = maxReset,
                GateNumber = gateNumber
            };

            _byIndex[index] = entry;
            _byName[name] = entry;
        }

        Log.Add(LogColor.Blue, "[MoveTable] Loaded {0} warp definition(s) from {1}", _byIndex.Count, path);
    }
}
