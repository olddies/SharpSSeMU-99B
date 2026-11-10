using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>Port of CSkillUseArea (SkillUseArea.cpp): Data/Skill/SkillUseArea.txt. Standing in a safe zone a player can cast
/// nothing, except the skills of the first list and only inside one of the areas of the second: the starting places of the
/// events (the Devil Square, the Blood Castle and the Chaos Castle), where the buffs must still work before the fight.</summary>
public sealed class SkillUseAreaTable
{
    private readonly HashSet<int> _skills = new();
    private readonly List<(int Map, int X, int Y, int TX, int TY)> _areas = new();

    public int SkillCount => _skills.Count;

    public int AreaCount => _areas.Count;

    public void AddSkill(int skill) => _skills.Add(skill);

    public void AddArea(int map, int x, int y, int tx, int ty) => _areas.Add((map, x, y, tx, ty));

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[SkillUseAreaTable] {0}", script.GetLastError());
            return 0;
        }

        while (script.GetToken() != TokenResult.End)
        {
            int section = script.GetNumber();

            while (section is 0 or 1)
            {
                if (script.GetAsString() == "end")
                {
                    break;
                }

                if (section == 0)
                {
                    _skills.Add(script.GetNumber());
                }
                else
                {
                    int map = script.GetNumber();
                    int x = script.GetAsNumber();
                    int y = script.GetAsNumber();
                    int tx = script.GetAsNumber();
                    int ty = script.GetAsNumber();
                    _areas.Add((map, x, y, tx, ty));
                }
            }
        }

        Log.Add(LogColor.Blue, "[SkillUseAreaTable] {0} skill(s) and {1} area(s) loaded from {2}", _skills.Count, _areas.Count, path);
        return _skills.Count + _areas.Count;
    }

    /// <summary>CheckUseSkill: whether a player standing in a safe zone at this place may cast this skill.</summary>
    public bool CheckUseSkill(int map, int x, int y, int skill)
    {
        if (!_skills.Contains(skill))
        {
            return false;
        }

        foreach (var area in _areas)
        {
            if (area.Map == map && x >= area.X && x <= area.TX && y >= area.Y && y <= area.TY)
            {
                return true;
            }
        }

        return false;
    }
}
