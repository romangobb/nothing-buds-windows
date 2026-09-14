// Best-effort file log for a tray app with no console.
using System.IO;

namespace NothingBuds.Services;

public static class Log
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NothingBuds", "app.log");
    private static readonly object Gate = new();

    public static void Info(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (Gate)
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
