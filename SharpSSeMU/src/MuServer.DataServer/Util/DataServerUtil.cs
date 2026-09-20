namespace MuServer.DataServer.Util;

/// <summary>Port of the standalone CheckTextSyntax function of DataServer/Util.cpp.</summary>
public static class DataServerUtil
{
    /// <summary>Rechaza espacio, comilla doble o comilla simple en nombres de personaje.</summary>
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
}
