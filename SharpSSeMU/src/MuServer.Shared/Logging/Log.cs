namespace MuServer.Shared.Logging;

public enum LogColor
{
    Black,
    Red,
    Green,
    Blue,
}

/// <summary> Console logger equivalent to the original's LogAdd()/CLog::Output() (which wrote to the server
/// window with colours + HH:mm:ss timestamp). Here it is printed to the console with colour and optionally to a
/// daily-rotating log file, like the "LOG/" of each original process. </summary>
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
                    // Same as the original: if writing to disk fails, the server is not interrupted.
                }
            }
        }
    }
}
