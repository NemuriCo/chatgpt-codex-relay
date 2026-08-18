using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BlueRelay.Services.Desktop;

public sealed record CodexDesktopWindowCandidate(
    int ProcessId,
    IntPtr Handle,
    string WindowTitle,
    string ClassName,
    string ProcessName,
    string? ProcessPath,
    bool IsVisible = true,
    bool IsEnabled = true,
    bool IsForeground = false);

/// <summary>
/// Keeps UI Automation ownership independent from the HWND carried by a DOM
/// accessibility node. Chromium nodes commonly expose HWND=0, while their
/// process still owns a real top-level window.
/// </summary>
public static class CodexDesktopWindowOwnership
{
    private const int SafeSelectionMargin = 15;

    public static bool TrySelectForProcess(
        int processId,
        IReadOnlyList<CodexDesktopWindowCandidate> candidates,
        out CodexDesktopWindowCandidate? selected)
    {
        selected = null;
        var eligible = candidates
            .Where(candidate => candidate.ProcessId == processId &&
                                candidate.Handle != IntPtr.Zero &&
                                candidate.IsVisible &&
                                candidate.IsEnabled &&
                                IsTrustedOpenAiIdentity(candidate))
            .Select(candidate => (Candidate: candidate, Score: Score(candidate)))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.IsForeground)
            .ToList();

        if (eligible.Count == 0)
        {
            return false;
        }

        var best = eligible[0];
        var second = eligible.Count > 1 ? eligible[1] : default;
        if (eligible.Count > 1 && best.Score - second.Score < SafeSelectionMargin)
        {
            // A PID can own more than one visible window. Do not guess unless
            // foreground/identity signals resolve the intended top-level host.
            return false;
        }

        selected = best.Candidate;
        return true;
    }

    public static bool IsTrustedOpenAiIdentity(CodexDesktopWindowCandidate candidate)
    {
        var signals = string.Join(
            ' ',
            candidate.WindowTitle,
            candidate.ClassName,
            candidate.ProcessName,
            candidate.ProcessPath);
        return ContainsIdentityToken(signals);
    }

    public static int Score(CodexDesktopWindowCandidate candidate)
    {
        var score = candidate.IsForeground ? 100 : 0;
        var signals = string.Join(
            ' ',
            candidate.WindowTitle,
            candidate.ClassName,
            candidate.ProcessName,
            candidate.ProcessPath);

        if (signals.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        if (signals.Contains("openai", StringComparison.OrdinalIgnoreCase))
        {
            score += 45;
        }

        if (signals.Contains("chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (!string.IsNullOrWhiteSpace(candidate.WindowTitle))
        {
            score += 5;
        }

        return score;
    }

    public static bool TryResolveForProcess(
        int processId,
        CancellationToken cancellationToken,
        out CodexDesktopWindowCandidate? selected)
    {
        selected = null;
        if (processId <= 0)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (processName, processPath) = ReadProcessIdentity(processId);
        var candidates = new List<CodexDesktopWindowCandidate>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (handle == IntPtr.Zero ||
                !NativeMethods.IsWindowVisible(handle) ||
                !NativeMethods.IsWindowEnabled(handle))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(handle, out var windowProcessId);
            if (windowProcessId != processId)
            {
                return true;
            }

            candidates.Add(new CodexDesktopWindowCandidate(
                processId,
                handle,
                NativeMethods.GetWindowText(handle),
                NativeMethods.GetClassName(handle),
                processName,
                processPath,
                IsForeground: handle == NativeMethods.GetForegroundWindow()));
            return candidates.Count < 64;
        }, IntPtr.Zero);

        cancellationToken.ThrowIfCancellationRequested();
        return TrySelectForProcess(processId, candidates, out selected);
    }

    private static bool ContainsIdentityToken(string signals) =>
        signals.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
        signals.Contains("openai", StringComparison.OrdinalIgnoreCase) ||
        signals.Contains("chatgpt", StringComparison.OrdinalIgnoreCase);

    private static (string ProcessName, string? ProcessPath) ReadProcessIdentity(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            string? processPath = null;
            try
            {
                processPath = process.MainModule?.FileName;
            }
            catch (Exception)
            {
                // Process path access can be denied across elevation levels.
            }

            return (processName, processPath);
        }
        catch (Exception)
        {
            return (string.Empty, null);
        }
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowEnabled(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

        public static string GetWindowText(IntPtr handle)
        {
            var text = new StringBuilder(512);
            GetWindowText(handle, text, text.Capacity);
            return text.ToString();
        }

        public static string GetClassName(IntPtr handle)
        {
            var className = new StringBuilder(256);
            GetClassName(handle, className, className.Capacity);
            return className.ToString();
        }
    }
}
