namespace WindowSearcher.App.Services;

/// <summary>All data lives under %LOCALAPPDATA% so it never syncs to OneDrive.</summary>
internal static class AppPaths
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSearcher");

    public static readonly string Database = Path.Combine(Root, "index.db");
    public static readonly string Thumbs = Path.Combine(Root, "thumbs");
    public static readonly string Settings = Path.Combine(Root, "settings.json");
    public static readonly string Log = Path.Combine(Root, "log.txt");

    /// <summary>Arguments handed from a second launch to the running instance.</summary>
    public static readonly string Command = Path.Combine(Root, "command.txt");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Thumbs);
    }
}

/// <summary>Small append-only log with a size cap; used for errors and per-capture timings.</summary>
internal static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(AppPaths.Log);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(AppPaths.Log, Path.ChangeExtension(AppPaths.Log, ".old.txt"), overwrite: true);
                File.AppendAllText(AppPaths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
