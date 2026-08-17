using System.Diagnostics;

namespace BlueRelay.Services.Codex;

public sealed record CodexExecutableInfo(
    string? Path,
    string Error,
    string? Version = null,
    string? AppServerHelp = null)
{
    public bool Found => !string.IsNullOrWhiteSpace(Path);
}

public sealed class CodexExecutableLocator : ICodexExecutableLocator
{
    private readonly object _cacheGate = new();
    private string? _cachedConfiguredPath;
    private Task<CodexExecutableInfo>? _cachedLookup;

    public Task<CodexExecutableInfo> LocateAsync(
        string? configuredPath = null,
        CancellationToken cancellationToken = default)
    {
        var key = configuredPath?.Trim();
        lock (_cacheGate)
        {
            if (_cachedLookup is not null &&
                string.Equals(_cachedConfiguredPath, key, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedLookup;
            }

            _cachedConfiguredPath = key;
            _cachedLookup = LocateCoreAsync(key, cancellationToken);
            return _cachedLookup;
        }
    }

    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _cachedConfiguredPath = null;
            _cachedLookup = null;
        }
    }

    private async Task<CodexExecutableInfo> LocateCoreAsync(
        string? configuredPath,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
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

        var failures = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Windows app execution aliases can appear in `where` output but
                // reject direct child-process creation. Do not select them.
                if (candidate.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{candidate}: Windows app execution alias");
                    if (!string.IsNullOrWhiteSpace(configuredPath))
                    {
                        break;
                    }

                    continue;
                }

                if (!File.Exists(candidate))
                {
                    if (!string.IsNullOrWhiteSpace(configuredPath))
                    {
                        failures.Add($"{candidate}: file does not exist");
                        break;
                    }

                    continue;
                }

                var path = Path.GetFullPath(candidate);
                var validation = await ValidateAsync(path, cancellationToken).ConfigureAwait(false);
                if (validation.Success)
                {
                    return new CodexExecutableInfo(path, string.Empty, validation.Version, validation.Help);
                }

                failures.Add($"{path}: {validation.Error}");
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    break;
                }
            }
            catch (IOException exception)
            {
                failures.Add($"{candidate}: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                failures.Add($"{candidate}: {exception.Message}");
            }
        }

        var detail = failures.Count == 0
            ? "Codex executable was not found. Install Codex or configure its executable path."
            : "No usable Codex executable was found. " + string.Join(" | ", failures.Take(4));
        return new CodexExecutableInfo(null, detail);
    }

    private static async Task<ValidationResult> ValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var version = await RunAsync(path, ["--version"], cancellationToken).ConfigureAwait(false);
        if (version.ExitCode != 0 || !version.Output.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Failure($"--version failed (exit {version.ExitCode}): {Short(version.ErrorOrOutput)}");
        }

        var help = await RunAsync(path, ["app-server", "--help"], cancellationToken).ConfigureAwait(false);
        if (help.ExitCode != 0 || !help.Output.Contains("app-server", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Failure($"app-server --help failed (exit {help.ExitCode}): {Short(help.ErrorOrOutput)}");
        }

        return new ValidationResult(true, string.Empty, Short(version.Output), Short(help.Output));
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
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
                return new CommandResult(-1, string.Empty, "process did not start");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return new CommandResult(-1, await outputTask.ConfigureAwait(false), "validation timed out");
            }

            return new CommandResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new CommandResult(-1, string.Empty, exception.Message);
        }
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

    private static string Short(string value)
    {
        value ??= string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }

    private sealed record ValidationResult(bool Success, string Error, string? Version = null, string? Help = null)
    {
        public static ValidationResult Failure(string error) => new(false, error);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error)
    {
        public string ErrorOrOutput => string.IsNullOrWhiteSpace(Error) ? Output : Error;
    }
}
