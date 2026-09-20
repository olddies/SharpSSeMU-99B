using System.Security.Cryptography;
using System.Text;

namespace MuServer.AdminPanel.Auth;

/// <summary>The panel password. It comes from <c>Admin:Password</c> in appsettings.json (or from an
/// <c>Admin__Password</c> environment variable); if none is configured, a random one is generated at start-up
/// and printed to the console. <para>Generating one was chosen over leaving the panel open or planting a
/// default: the panel writes the server configuration, so "no password" is not a reasonable option, and a
/// well-known default password is just as bad. Generating it avoids both without being able to lock anyone out,
/// because it is in plain view on the console.</para> <para>The comparison is constant-time so as not to leak
/// the length or the prefix through how long it takes to answer.</para></summary>
public sealed class AdminPassword
{
    private readonly byte[] _expected;

    public AdminPassword(IConfiguration configuration, ILogger<AdminPassword> logger)
    {
        var configured = configuration["Admin:Password"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = GenerateReadablePassword();
            IsGenerated = true;

            logger.LogWarning(
                "No admin panel password is configured. One was generated for this run: {Password}\n" +
                "To set a permanent one, put \"Admin\": {{ \"Password\": \"loquesea\" }} en appsettings.json " +
                "(or the Admin__Password environment variable).",
                configured);
        }

        _expected = Encoding.UTF8.GetBytes(configured);
    }

    /// <summary>True if the password is the one generated at start-up (used to warn about it on the login page).</summary>
    public bool IsGenerated { get; }

    public bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _expected);
    }

    /// <summary>Random but typeable password: without characters that get confused with each other (0/O, 1/l).</summary>
    private static string GenerateReadablePassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzACDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[16];

        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }
}
