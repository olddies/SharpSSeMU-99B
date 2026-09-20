using System.Security.Cryptography;
using System.Text;

namespace MuServer.AdminPanel.Auth;

/// <summary>La contraseña del panel. Sale de <c>Admin:Password</c> en appsettings.json (o de una
/// variable de entorno <c>Admin__Password</c>); si no hay ninguna configurada se genera una al azar
/// al arrancar y se imprime en la consola.
///
/// <para>Se eligió generar una en vez de dejar el panel abierto o de plantar una por defecto: el panel
/// escribe la configuración del servidor, así que "sin contraseña" no es una opción razonable, y una
/// contraseña por defecto conocida es igual de mala. Generarla evita las dos cosas sin poder dejar a
/// nadie afuera, porque queda a la vista en la consola.</para>
///
/// <para>La comparación es de tiempo constante para no filtrar el largo ni el prefijo por cuánto tarda
/// en responder.</para></summary>
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

    /// <summary>Contraseña al azar pero tipeable: sin caracteres que se confundan entre sí (0/O, 1/l).</summary>
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
