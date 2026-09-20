using System.Reflection;
using System.Text.Json;

namespace MuServer.Shared.Localization;

/// <summary>
/// Minimal localisation shared by every server process and the AdminPanel.
/// English is the source language: the English text itself is the lookup key, so code reads naturally
/// and a missing translation simply falls back to English. Spanish lives in the embedded JSON files
/// under <c>Localization/es/*.json</c> (flat objects: English text -> Spanish text).
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Spanish = "es";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> SpanishTable = new(LoadSpanish);

    /// <summary>Process-wide language, used for server console/log output. Default: English.</summary>
    public static string Language { get; set; } = English;

    public static IReadOnlyList<string> Supported { get; } = new[] { English, Spanish };

    /// <summary>Maps "es", "es-AR", "ES" ... to a supported code; anything unknown becomes English.</summary>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return English;
        }

        return language.Trim().StartsWith("es", StringComparison.OrdinalIgnoreCase) ? Spanish : English;
    }

    /// <summary>Sets <see cref="Language"/> from the MUSERVER_LANG environment variable, else from the given value.</summary>
    public static void Configure(string? configured)
    {
        var fromEnv = Environment.GetEnvironmentVariable("MUSERVER_LANG");
        Language = Normalize(string.IsNullOrWhiteSpace(fromEnv) ? configured : fromEnv);
    }

    public static string T(string english) => T(Language, english);

    public static string T(string language, string english)
    {
        if (language == Spanish && SpanishTable.Value.TryGetValue(english, out var spanish))
        {
            return spanish;
        }

        return english;
    }

    public static string F(string english, params object?[] args) => F(Language, english, args);

    public static string F(string language, string english, params object?[] args) =>
        string.Format(T(language, english), args);

    /// <summary>All Spanish keys currently loaded (used by the consistency check).</summary>
    public static IReadOnlyCollection<string> SpanishKeys => SpanishTable.Value.Keys.ToArray();

    private static IReadOnlyDictionary<string, string> LoadSpanish()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(Loc).Assembly;

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(".Localization.es.", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            var part = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);

            if (part == null)
            {
                continue;
            }

            foreach (var (key, value) in part)
            {
                table[key] = value;
            }
        }

        return table;
    }
}
