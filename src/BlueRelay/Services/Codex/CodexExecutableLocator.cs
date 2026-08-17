using System.Diagnostics;

namespace BlueRelay.Services.Codex;

public sealed record CodexExecutableInfo(string? Path, string Error)
{
    public bool Found => !string.IsNullOrWhiteSpace(Path);
}

public sealed class CodexExecutableLocator
{
    public CodexExecutableInfo Locate(string? configuredPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath.Trim());
        }

        var pathCandidate = TryFindOnPath();
        if (pathCandidate is not null)
        {
            candidates.Add(pathCandidate);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, ".codex", "plugins", ".plugin-appserver", "codex.exe"));
            candidates.Add(Path.Combine(userProfile, ".codex", ".sandbox-bin", "codex.exe"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Programs", "Codex", "codex.exe"));
            candidates.Add(Path.Combine(localAppData, "Programs", "codex", "codex.exe"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Windows app execution aliases commonly appear in `where` output
                // but reject direct child-process creation. Prefer the real Codex
                // installation candidates below in that case.
                if (candidate.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return new CodexExecutableInfo(Path.GetFullPath(candidate), string.Empty);
                }
            }
            catch (IOException)
            {
                // Continue looking at the next candidate.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue looking at the next candidate.
            }
        }

        return new CodexExecutableInfo(
            null,
            "Codex executable was not found. Install Codex or configure its executable path.");
    }

    private static string? TryFindOnPath()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
                    Arguments = "codex",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim())
                .FirstOrDefault(File.Exists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
