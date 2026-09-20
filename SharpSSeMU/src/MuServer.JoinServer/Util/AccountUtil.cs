namespace MuServer.JoinServer.Util;

/// <summary>Puerto de las funciones sueltas de Util.cpp relacionadas a cuentas.</summary>
public static class AccountUtil
{
    /// <summary>Puerto de CheckTextSyntax: rechaza espacio, comilla doble o comilla simple
    /// (protección anti-inyección "de época" del original — ya no hace falta para SQL porque
    /// ahora usamos parámetros, pero se mantiene para no aceptar cuentas con esos caracteres,
    /// tal como hacía el cliente/servidor original).</summary>
    public static bool CheckTextSyntax(string text)
    {
        foreach (var ch in text)
        {
            if (ch == 0x20 || ch == 0x22 || ch == 0x27)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Puerto de CheckAccountCaseSensitive: normaliza a minúsculas si CaseSensitive=0.</summary>
    public static string NormalizeAccount(string account, bool caseSensitive) =>
        caseSensitive ? account : account.ToLowerInvariant();
}
