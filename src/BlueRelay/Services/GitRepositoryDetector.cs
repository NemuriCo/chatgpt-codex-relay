using System.ComponentModel;
using System.Diagnostics;

namespace BlueRelay.Services;

public sealed class GitRepositoryDetector : IGitRepositoryDetector
{
    private readonly string _gitExecutable;
    private readonly TimeSpan _timeout;

    public GitRepositoryDetector(string gitExecutable = "git", TimeSpan? timeout = null)
    {
        _gitExecutable = gitExecutable;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<GitRepositoryInfo> DetectAsync(string selectedPath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeSelectedPath(selectedPath);
        var fallbackName = GetDirectoryName(normalizedPath);
        if (!Directory.Exists(normalizedPath))
        {
            return new GitRepositoryInfo(false, normalizedPath, null, fallbackName, null, false);
        }

        var rootResult = await RunGitAsync(normalizedPath, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (rootResult.IsUnavailable)
        {
            return new GitRepositoryInfo(false, normalizedPath, null, fallbackName, null, false);
        }

        if (!rootResult.Succeeded)
        {
            return new GitRepositoryInfo(false, normalizedPath, null, fallbackName, null, true);
        }

        var repositoryRoot = NormalizeSelectedPath(rootResult.StandardOutput);
        var remoteResult = await RunGitAsync(repositoryRoot, ["remote", "get-url", "origin"], cancellationToken);
        var originUrl = remoteResult.Succeeded ? remoteResult.StandardOutput.Trim() : null;
        var suggestedName = ExtractRepositoryName(originUrl) ?? GetDirectoryName(repositoryRoot);
        return new GitRepositoryInfo(true, normalizedPath, repositoryRoot, suggestedName, originUrl, true);
    }

    public static string? ExtractRepositoryName(string? originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            return null;
        }

        var value = originUrl.Trim().TrimEnd('/');
        var separatorIndex = value.LastIndexOfAny(['/','\\', ':']);
        var name = separatorIndex >= 0 ? value[(separatorIndex + 1)..] : value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _gitExecutable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return GitCommandResult.Unavailable;
            }
        }
        catch (Win32Exception)
        {
            return GitCommandResult.Unavailable;
        }
        catch (FileNotFoundException)
        {
            return GitCommandResult.Unavailable;
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_timeout);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCancellation.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCancellation.Token);
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            var output = (await outputTask.ConfigureAwait(false)).Trim();
            _ = await errorTask.ConfigureAwait(false);
            return new GitCommandResult(process.ExitCode == 0, false, output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new GitCommandResult(false, false, string.Empty);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static string NormalizeSelectedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private static string GetDirectoryName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return new DirectoryInfo(path).Name;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }

    private sealed record GitCommandResult(bool Succeeded, bool IsUnavailable, string StandardOutput)
    {
        public static GitCommandResult Unavailable { get; } = new(false, true, string.Empty);
    }
}
