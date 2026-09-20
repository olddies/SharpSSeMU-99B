namespace MuServer.Shared.Logging;

public enum LogColor
{
    Black,
    Red,
    Green,
    Blue,
}

/// <summary>
/// Logger de consola equivalente a LogAdd()/CLog::Output() del original (que escribía en la
/// ventana del servidor con colores + timestamp HH:mm:ss). Aquí se imprime a consola con color
/// y opcionalmente a un archivo de log rotativo por día, igual que el "LOG/" de cada proceso original.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static string? _logDirectory;

    public static void Configure(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
    }

    public static void Add(LogColor color, string format, params object?[] args)
    {
        format = Localization.Loc.T(format);
        var text = args.Length == 0 ? format : string.Format(format, args);
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"{stamp} {text}";

        lock (Sync)
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = color switch
            {
                LogColor.Red => ConsoleColor.Red,
                LogColor.Green => ConsoleColor.Green,
                LogColor.Blue => ConsoleColor.Cyan,
                _ => ConsoleColor.Gray,
            };

            Console.WriteLine(line);
            Console.ForegroundColor = prevColor;

            if (_logDirectory != null)
            {
                try
                {
                    var file = Path.Combine(_logDirectory, $"{DateTime.Now:yyyyMMdd}.txt");
                    File.AppendAllText(file, line + Environment.NewLine);
                }
                catch
                {
                    // Igual que el original: si falla la escritura a disco, no se interrumpe el servidor.
                }
            }
        }
    }
}
