namespace MuServer.Shared.Config;

/// <summary> .ini file reader equivalent to Win32's GetPrivateProfileInt/GetPrivateProfileString, so that the
/// same .ini files as the original server (ConnectServer.ini, JoinServer.ini, DataServer.ini) can keep being
/// used without changing the deployment format. </summary>
public class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Load(string path)
    {
        var ini = new IniFile();

        if (!File.Exists(path))
        {
            return ini;
        }

        string? currentSection = null;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (!ini._sections.ContainsKey(currentSection))
                {
                    ini._sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            if (currentSection == null)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            ini._sections[currentSection][key] = value;
        }

        return ini;
    }

    public int GetInt(string section, string key, int defaultValue)
    {
        if (_sections.TryGetValue(section, out var kv) && kv.TryGetValue(key, out var raw) && int.TryParse(raw, out var value))
        {
            return value;
        }

        return defaultValue;
    }

    public string GetString(string section, string key, string defaultValue)
    {
        if (_sections.TryGetValue(section, out var kv) && kv.TryGetValue(key, out var raw))
        {
            return raw;
        }

        return defaultValue;
    }
}
