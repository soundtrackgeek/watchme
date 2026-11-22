using Watchme.Core;

namespace Watchme.Agent;

/// <summary>Tiny file logger. The agent has no console, so this is the only place errors show up.</summary>
internal static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Paths.Log;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the agent down.
        }
    }
}
