namespace MuServer.JoinServer.Util;

/// <summary>Puerto de las funciones sueltas de Util.cpp relacionadas a cuentas.</summary>
public static class AccountUtil
{
    /// <summary>Port of CheckTextSyntax: rejects space, double quote or single quote (the original's "period"
    /// anti-injection protection — no longer needed for SQL because we now use parameters, but it is kept so as
    /// not to accept accounts with those characters, just as the original client/server did).</summary>
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

    /// <summary>Port of CheckAccountCaseSensitive: normalises to lowercase if CaseSensitive=0.</summary>
    public static string NormalizeAccount(string account, bool caseSensitive) =>
        caseSensitive ? account : account.ToLowerInvariant();
}
