namespace MuServer.GameServer.World;

/// <summary>
/// The level and experience rules of the Dark Horse and the Dark Raven (gPetExperience in User.cpp and the pet
/// settings of Common.dat). A pet's level goes from 1 to <see cref="MaxLevel"/>; each level starts at a number of
/// experience points given by <see cref="Experience"/>, and what a kill gives the pet is the experience the player
/// got divided by <see cref="RateDivisor"/>.
/// </summary>
public sealed class PetRules
{
    /// <summary>MAX_CHARACTER_LEVEL: the size of gPetExperience.</summary>
    public const int TableSize = 1000;

    private readonly uint[] _experience = new uint[TableSize + 2];

    /// <summary>The rules in force (set by Program from Common.dat).</summary>
    public static PetRules Current { get; set; } = new(100, 50, 10);

    public int MaxLevel { get; }

    /// <summary>PetExperienceRateDivisor; zero in the file is read as 1 (the original would divide by zero).</summary>
    public int RateDivisor { get; }

    public PetRules(int experienceMultiplierConstA, int maxLevel, int rateDivisor)
    {
        MaxLevel = Math.Clamp(maxLevel, 0, TableSize);
        RateDivisor = Math.Max(rateDivisor, 1);

        // gObjSetExperienceTable: levels 0 to 2 start at nothing; from 3 on ((n+10)*n*n*n) * ConstA, in the 32-bit
        // arithmetic of the original (it wraps for the very high levels nobody reaches).
        for (int n = 3; n <= TableSize; n++)
        {
            _experience[n] = unchecked((uint)(((n + 10) * n * n * n) * experienceMultiplierConstA));
        }
    }

    /// <summary>The experience at which <paramref name="level"/> starts (gPetExperience[level]).</summary>
    public uint Experience(int level) => level >= 0 && level < _experience.Length ? _experience[level] : uint.MaxValue;
}
