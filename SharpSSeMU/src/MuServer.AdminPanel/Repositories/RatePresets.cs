namespace MuServer.AdminPanel.Repositories;

/// <summary>A ready-made set of values for the Rates page. Applying a preset only fills the page's pending
/// changes -- nothing is written until the admin reviews it and presses Save. Keys are the
/// <see cref="RateSetting.Key"/> of <see cref="RateSettings"/> (per-account-level ones are written to AL0-3).</summary>
public sealed record RatePreset(string Id, string Name, string Description, IReadOnlyDictionary<string, int> Values);

/// <summary>Presets for the settings that decide how fast a server feels. Only experience, drops, zen and the
/// levels-per-gain caps are touched; jewels, fruits, durability and everything else stay as they are.</summary>
public static class RatePresets
{
    public static readonly RatePreset[] All =
    [
        new("classic", "Classic (x1)",
            "Original pace: 1x experience, normal drops. Long levelling, a slow economy.",
            new Dictionary<string, int>
            {
                ["AddExperienceRate"] = 1,
                ["AddEventExperienceRate"] = 1,
                ["MaxLevelUp"] = 1,
                ["MaxLevelUpEvent"] = 1,
                ["ItemDropRate"] = 300,
                ["MoneyAmountDropRate"] = 100,
            }),

        new("soft", "Soft (x10)",
            "A relaxed server: 10x experience, slightly better drops and double zen.",
            new Dictionary<string, int>
            {
                ["AddExperienceRate"] = 10,
                ["AddEventExperienceRate"] = 10,
                ["MaxLevelUp"] = 2,
                ["MaxLevelUpEvent"] = 2,
                ["ItemDropRate"] = 400,
                ["MoneyAmountDropRate"] = 200,
            }),

        new("fast", "Fast (x100)",
            "A high-rate server: 100x experience, generous drops and triple zen.",
            new Dictionary<string, int>
            {
                ["AddExperienceRate"] = 100,
                ["AddEventExperienceRate"] = 100,
                ["MaxLevelUp"] = 5,
                ["MaxLevelUpEvent"] = 5,
                ["ItemDropRate"] = 500,
                ["MoneyAmountDropRate"] = 300,
            }),

        new("testing", "Testing (very fast)",
            "For trying things out: characters level almost instantly and drops are frequent. Not meant for a real server.",
            new Dictionary<string, int>
            {
                ["AddExperienceRate"] = 10000,
                ["AddEventExperienceRate"] = 100,
                ["MaxLevelUp"] = 10,
                ["MaxLevelUpEvent"] = 10,
                ["ItemDropRate"] = 1000,
                ["MoneyAmountDropRate"] = 1000,
            }),
    ];
}
