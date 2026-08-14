using System.Text;

namespace BlueRelay.Diagnostics;

public static class StartupDiagnostics
{
    private static readonly object SyncRoot = new();
    private static string? _activeLogPath;

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
        lock (SyncRoot)
        {
            if (TryAppend(GetPreferredPath(), line))
            {
                return;
            }

            TryAppend(GetFallbackPath(), line);
        }
    }

    public static void WriteException(string stage, Exception exception)
    {
        Write($"ERROR stage={stage} type={exception.GetType().FullName} message={exception.Message}{Environment.NewLine}{exception}");
    }

    private static string GetPreferredPath()
    {
        return _activeLogPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueRelay",
            "logs",
            "startup.log");
    }

    private static string GetFallbackPath()
    {
        return Path.Combine(Path.GetTempPath(), "BlueRelay", "logs", "startup.log");
    }

    private static bool TryAppend(string path, string content)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            File.AppendAllText(path, content, Encoding.UTF8);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
