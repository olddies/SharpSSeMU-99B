using MuServer.GameServer.Config;
using NUnit.Framework;

namespace MuServer.GameServer.Tests;

/// <summary>The start-up check for the game data that is not shipped with the project.</summary>
public class GameDataCheckTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gamedata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    private GameServerConfig Config() => GameServerConfig.Load(Path.Combine(_dir, "GameServer.ini"), _dir);

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "end\n");
    }

    [Test]
    public void A_fresh_folder_is_missing_everything_including_the_encryption_keys()
    {
        var missing = GameDataCheck.FindMissing(Config());

        Assert.That(missing, Has.Count.EqualTo(18));
        Assert.That(missing, Has.Some.EndsWith(Path.Combine("Hack", "Enc2.dat")));
        Assert.That(missing, Has.Some.EndsWith(Path.Combine("Data", "Item", "Item.txt")));
        Assert.That(missing, Has.Some.EndsWith(Path.Combine("Data", "Terrain")));
    }

    [Test]
    public void Nothing_is_reported_once_the_files_are_there()
    {
        foreach (var path in GameDataCheck.FindMissing(Config()))
        {
            if (Path.HasExtension(path))
            {
                Touch(path);
            }
            else
            {
                Directory.CreateDirectory(path);
            }
        }

        Assert.That(GameDataCheck.FindMissing(Config()), Is.Empty);
    }

    [Test]
    public void A_path_overridden_in_the_ini_is_the_one_checked()
    {
        File.WriteAllText(Path.Combine(_dir, "GameServer.ini"), "[GameServerInfo]\nItemPath = elsewhere/MyItems.txt\n");

        Assert.That(GameDataCheck.FindMissing(Config()), Has.Some.EndsWith("MyItems.txt"));
    }
}
