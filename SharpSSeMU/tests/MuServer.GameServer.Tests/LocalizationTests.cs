using MuServer.Shared.Localization;
using NUnit.Framework;

namespace MuServer.GameServer.Tests;

public class LocalizationTests
{
    [Test]
    public void A_string_argument_fills_the_placeholder_and_is_not_taken_for_a_language()
    {
        // This used to answer "zen": the string argument was read as the text and the format as the language.
        string command = "zen";
        Assert.That(Loc.F("[Server] Command '/{0}' not recognised.", command), Is.EqualTo("[Server] Command '/zen' not recognised."));
        Assert.That(Loc.F("{0} and {1}", "a", "b"), Is.EqualTo("a and b"));
        Assert.That(Loc.F("{0} and {1}", "a", 2), Is.EqualTo("a and 2"));
    }

    [Test]
    public void The_language_can_be_named_explicitly()
    {
        Assert.That(Loc.FormatIn(Loc.Spanish, "[Server] Command '/{0}' not recognised.", "zen"), Is.EqualTo("[Server] Comando '/zen' no reconocido."));
        Assert.That(Loc.FormatIn(Loc.English, "[Server] Command '/{0}' not recognised.", "zen"), Is.EqualTo("[Server] Command '/zen' not recognised."));
    }
}
