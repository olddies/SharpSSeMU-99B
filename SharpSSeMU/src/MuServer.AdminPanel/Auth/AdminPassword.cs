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
                "No hay contraseña configurada para el panel. Se generó una para esta ejecución: {Password}\n" +
                "Para fijar una permanente, poné \"Admin\": {{ \"Password\": \"loquesea\" }} en appsettings.json " +
                "(o la variable de entorno Admin__Password).",
                configured);
        }

        _expected = Encoding.UTF8.GetBytes(configured);
    }

    /// <summary>True si la contraseña es la generada al arrancar (sirve para avisarlo en el login).</summary>
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
