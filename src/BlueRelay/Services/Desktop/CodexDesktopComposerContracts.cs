using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using BlueRelay.Diagnostics;

namespace BlueRelay.Services.Desktop;

public sealed record UiAutomationBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed record UiAutomationMetadata(
    IntPtr Handle,
    int ProcessId,
    string WindowTitle,
    string ControlType,
    string AutomationId,
    string Name,
    string ClassName,
    string FrameworkId,
    bool IsEnabled,
    bool IsKeyboardFocusable,
    bool IsOffscreen,
    UiAutomationBounds Bounds,
    UiAutomationBounds WindowBounds,
    string ParentHierarchy,
    bool IsLikelyOpenAiWindow,
    bool IsForeground);

public sealed record CodexComposerCandidate(
    UiAutomationMetadata Metadata,
    bool IsOpenAiWindow,
    bool SupportsValuePattern,
    bool IsValueReadOnly,
    int SemanticScore,
    bool SupportsTextPattern = false);

public static class CodexComposerCandidateResolver
{
    public static bool TryResolveElement<TElement>(
        IEnumerable<(TElement Element, CodexComposerCandidate Candidate)> bindings,
        CodexComposerCandidate selected,
        out TElement? element)
        where TElement : class
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(selected);

        foreach (var binding in bindings)
        {
            if (ReferenceEquals(binding.Candidate, selected))
            {
                element = binding.Element;
                return true;
            }
        }

        element = null;
        return false;
    }
}

public sealed record OpenAiDesktopInspection(
    IReadOnlyList<UiAutomationMetadata> Windows,
    IReadOnlyList<CodexComposerCandidate> ComposerCandidates);

public sealed record CodexComposerInjectionResult(
    bool Success,
    string Code,
    string Message,
    bool UsedClipboardFallback = false,
    bool ClipboardRestoreFailed = false,
    CodexComposerInjectionMode Mode = CodexComposerInjectionMode.Unknown,
    CodexClipboardSnapshotMode ClipboardSnapshotMode = CodexClipboardSnapshotMode.Full,
    bool ClipboardRestoreUnavailable = false)
{
    public bool ClipboardWarning => ClipboardRestoreFailed || ClipboardRestoreUnavailable;

    public static CodexComposerInjectionResult Filled(
        string message,
        bool usedClipboardFallback = false,
        bool clipboardRestoreFailed = false,
        CodexComposerInjectionMode mode = CodexComposerInjectionMode.Unknown,
        CodexClipboardSnapshotMode clipboardSnapshotMode = CodexClipboardSnapshotMode.Full,
        bool clipboardRestoreUnavailable = false) =>
        new(
            true,
            "filled",
            message,
            usedClipboardFallback,
            clipboardRestoreFailed,
            mode,
            clipboardSnapshotMode,
            clipboardRestoreUnavailable);

    public static CodexComposerInjectionResult Failed(
        string code,
        string message,
        bool clipboardRestoreFailed = false,
        CodexComposerInjectionMode mode = CodexComposerInjectionMode.VerificationFailed) =>
        new(false, code, message, ClipboardRestoreFailed: clipboardRestoreFailed, Mode: mode);
}

public readonly record struct CodexKeyboardInput(ushort VirtualKey, bool KeyUp);

[StructLayout(LayoutKind.Sequential)]
public struct CodexSendInputNative
{
    public uint Type;
    public CodexSendInputUnion Data;
}

[StructLayout(LayoutKind.Explicit)]
public struct CodexSendInputUnion
{
    [FieldOffset(0)]
    public CodexSendInputMouse Mouse;

    [FieldOffset(0)]
    public CodexSendInputKeyboard Keyboard;

    [FieldOffset(0)]
    public CodexSendInputHardware Hardware;
}

[StructLayout(LayoutKind.Sequential)]
public struct CodexSendInputMouse
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct CodexSendInputKeyboard
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct CodexSendInputHardware
{
    public uint Message;
    public ushort ParameterLow;
    public ushort ParameterHigh;
}

public static class CodexKeyboardInputSequence
{
    public const ushort VirtualKeyControl = 0x11;
    public const ushort VirtualKeyA = 0x41;
    public const ushort VirtualKeyV = 0x56;

    public static IReadOnlyList<CodexKeyboardInput> CtrlV { get; } =
    [
        new(VirtualKeyControl, KeyUp: false),
        new(VirtualKeyV, KeyUp: false),
        new(VirtualKeyV, KeyUp: true),
        new(VirtualKeyControl, KeyUp: true)
    ];

    public static IReadOnlyList<CodexKeyboardInput> CtrlA { get; } =
    [
        new(VirtualKeyControl, KeyUp: false),
        new(VirtualKeyA, KeyUp: false),
        new(VirtualKeyA, KeyUp: true),
        new(VirtualKeyControl, KeyUp: true)
    ];
}

public sealed record CodexKeyboardInputSendResult(
    int RequestedInputCount,
    int SentInputCount,
    int Win32Error)
{
    public bool Succeeded => RequestedInputCount == SentInputCount;
}

public interface ICodexKeyboardInputSender
{
    CodexKeyboardInputSendResult SendCtrlV();

    CodexKeyboardInputSendResult SendCtrlA();
}

public sealed record CodexClipboardWriteVerification(
    bool ContainsText,
    int ClipboardLength,
    int SourceLength)
{
    public bool IsVerified => ContainsText && ClipboardLength == SourceLength;
}

public static class CodexClipboardWriteVerifier
{
    public static CodexClipboardWriteVerification Verify(
        bool containsText,
        int clipboardLength,
        int sourceLength) =>
        new(containsText, clipboardLength, sourceLength);
}

public static class CodexClipboardRetryPolicy
{
    public static int GetMaximumAttempts(TimeSpan budget, TimeSpan interval)
    {
        if (budget <= TimeSpan.Zero || interval <= TimeSpan.Zero)
        {
            return 1;
        }

        var attempts = Math.Ceiling(budget.TotalMilliseconds / interval.TotalMilliseconds) + 1;
        return attempts >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)attempts);
    }

    public static TimeSpan GetDelay(TimeSpan remaining, TimeSpan interval)
    {
        if (remaining <= TimeSpan.Zero || interval <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining < interval ? remaining : interval;
    }
}

public enum CodexClipboardSnapshotMode
{
    Full,
    Unavailable,
    NotNeeded
}

public static class CodexClipboardTextComparer
{
    public static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    public static bool Matches(string? existingText, string payload) =>
        existingText is not null &&
        string.Equals(
            NormalizeLineEndings(existingText),
            NormalizeLineEndings(payload),
            StringComparison.Ordinal);
}

public static class CodexComposerInputDecision
{
    public static bool CanSendFormalCtrlV(
        bool foregroundMatchesTarget,
        bool uiaFocus,
        bool clipboardSet,
        bool clipboardVerified,
        bool clipboardSnapshotAvailable) =>
        foregroundMatchesTarget &&
        uiaFocus &&
        clipboardSet &&
        clipboardVerified;
}

public enum CodexComposerInjectionMode
{
    Unknown,
    ValuePatternVerified,
    ClipboardInlineVerified,
    ClipboardInlineTransformedAccepted,
    ClipboardReferenceAccepted,
    VerificationFailed
}

public sealed record CodexComposerWriteVerification(
    bool ValueAvailable,
    string? Value,
    bool TextAvailable,
    string? Text,
    bool ValueMatchesSource,
    bool TextMatchesSource,
    int SemanticAnchorCount = 0,
    int SemanticAnchorMatchedCount = 0,
    bool SemanticAnchorsInOrder = false)
{
    public int ValueLength => Value?.Length ?? 0;

    public int TextLength => Text?.Length ?? 0;

    public bool IsVerified =>
        (ValueAvailable || TextAvailable) &&
        (!ValueAvailable || ValueMatchesSource) &&
        (!TextAvailable || TextMatchesSource);

    public bool IsRichTextTransformedAccepted =>
        !IsVerified &&
        (ValueAvailable || TextAvailable) &&
        SemanticAnchorCount > 0 &&
        SemanticAnchorMatchedCount == SemanticAnchorCount &&
        SemanticAnchorsInOrder;
}

public sealed record CodexComposerSemanticAnchorVerification(
    int AnchorCount,
    int MatchedAnchorCount,
    bool AnchorsInOrder)
{
    public bool IsAccepted =>
        AnchorCount > 0 &&
        MatchedAnchorCount == AnchorCount &&
        AnchorsInOrder;
}

public static class CodexComposerWriteVerifier
{
    public static CodexComposerWriteVerification Verify(
        string source,
        bool valueAvailable,
        string? value,
        bool textAvailable,
        string? text)
    {
        var normalizedSource = NormalizeComposerTextForVerification(source);
        var normalizedValue = NormalizeComposerTextForVerification(value);
        var normalizedText = NormalizeComposerTextForVerification(text);
        var valueSemanticVerification = valueAvailable
            ? VerifySemanticAnchors(normalizedSource, normalizedValue)
            : new CodexComposerSemanticAnchorVerification(0, 0, false);
        var textSemanticVerification = textAvailable
            ? VerifySemanticAnchors(normalizedSource, normalizedText)
            : new CodexComposerSemanticAnchorVerification(0, 0, false);
        var semanticVerification = SelectBestSemanticVerification(
            valueSemanticVerification,
            textSemanticVerification);
        return new CodexComposerWriteVerification(
            valueAvailable,
            value,
            textAvailable,
            text,
            valueAvailable && normalizedValue.Equals(normalizedSource, StringComparison.Ordinal),
            textAvailable && normalizedText.Equals(normalizedSource, StringComparison.Ordinal),
            semanticVerification.AnchorCount,
            semanticVerification.MatchedAnchorCount,
            semanticVerification.AnchorsInOrder);
    }

    public static IReadOnlyList<string> BuildSemanticAnchors(string source)
    {
        var normalizedSource = NormalizeComposerTextForVerification(source);
        var tokens = new List<string>();
        var token = new StringBuilder();

        void FlushToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            tokens.Add(token.ToString());
            token.Clear();
        }

        foreach (var character in normalizedSource)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                token.Append(character);
            }
            else
            {
                FlushToken();
            }
        }

        FlushToken();
        var uniqueTokens = tokens
            .Where(candidate => candidate.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (uniqueTokens.Count == 0)
        {
            uniqueTokens = tokens
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (uniqueTokens.Count <= 3)
        {
            return uniqueTokens;
        }

        var maximumAnchorCount = normalizedSource.Length >= 1024 ? 5 : 3;
        var positions = maximumAnchorCount == 5
            ? new[] { 0, uniqueTokens.Count / 4, uniqueTokens.Count / 2, (uniqueTokens.Count * 3) / 4, uniqueTokens.Count - 1 }
            : new[] { 0, uniqueTokens.Count / 2, uniqueTokens.Count - 1 };
        var anchors = new List<string>(maximumAnchorCount);
        foreach (var position in positions)
        {
            var candidate = uniqueTokens[position];
            if (!anchors.Contains(candidate, StringComparer.Ordinal))
            {
                anchors.Add(candidate);
            }
        }

        return anchors;
    }

    public static CodexComposerSemanticAnchorVerification VerifySemanticAnchors(
        string source,
        string? destination)
    {
        var anchors = BuildSemanticAnchors(source);
        var normalizedDestination = NormalizeComposerTextForVerification(destination);
        var searchStart = 0;
        var matchedAnchorCount = 0;
        foreach (var anchor in anchors)
        {
            var index = normalizedDestination.IndexOf(
                anchor,
                searchStart,
                StringComparison.Ordinal);
            if (index < 0)
            {
                return new CodexComposerSemanticAnchorVerification(
                    anchors.Count,
                    matchedAnchorCount,
                    false);
            }

            matchedAnchorCount++;
            searchStart = index + anchor.Length;
        }

        return new CodexComposerSemanticAnchorVerification(
            anchors.Count,
            matchedAnchorCount,
            anchors.Count > 0);
    }

    private static CodexComposerSemanticAnchorVerification SelectBestSemanticVerification(
        CodexComposerSemanticAnchorVerification first,
        CodexComposerSemanticAnchorVerification second)
    {
        if (second.MatchedAnchorCount > first.MatchedAnchorCount)
        {
            return second;
        }

        return second.MatchedAnchorCount == first.MatchedAnchorCount &&
               second.AnchorsInOrder &&
               !first.AnchorsInOrder
            ? second
            : first;
    }

    public static string NormalizeComposerTextForVerification(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    public static bool IsEmpty(CodexComposerWriteVerification verification)
    {
        return (verification.ValueAvailable || verification.TextAvailable) &&
               (!verification.ValueAvailable || NormalizeComposerTextForVerification(verification.Value).Length == 0) &&
               (!verification.TextAvailable || NormalizeComposerTextForVerification(verification.Text).Length == 0);
    }

    public static bool HasReferencedPastedTextSignal(params string?[] values)
    {
        return values.Any(value =>
            value?.Contains("referenced pasted text", StringComparison.OrdinalIgnoreCase) == true ||
            value?.Contains("pasted text file", StringComparison.OrdinalIgnoreCase) == true);
    }
}

public sealed record CodexComposerReferenceSnapshot(
    bool IsAvailable,
    IReadOnlySet<string> ReferenceNodeIds,
    IReadOnlyDictionary<string, string>? AttachmentDetectionKinds = null)
{
    public int Count => ReferenceNodeIds.Count;

    public int AttachmentCount => AttachmentDetectionKinds?.Count ?? 0;

    public bool HasNewLegacyReferencesSince(CodexComposerReferenceSnapshot before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return IsAvailable &&
               before.IsAvailable &&
               ReferenceNodeIds.Any(referenceId => !before.ReferenceNodeIds.Contains(referenceId));
    }

    public bool HasNewAttachmentsSince(CodexComposerReferenceSnapshot before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return IsAvailable &&
               before.IsAvailable &&
               AttachmentDetectionKinds is not null &&
               AttachmentDetectionKinds.Keys.Any(attachmentId =>
                   before.AttachmentDetectionKinds is null ||
                   !before.AttachmentDetectionKinds.ContainsKey(attachmentId));
    }

    public string GetNewDetectionKindSince(CodexComposerReferenceSnapshot before)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (AttachmentDetectionKinds is not null)
        {
            var newAttachmentKinds = AttachmentDetectionKinds
                .Where(pair => before.AttachmentDetectionKinds is null ||
                               !before.AttachmentDetectionKinds.ContainsKey(pair.Key))
                .Select(pair => pair.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var preferredKind in new[] { "action", "structure" })
            {
                if (newAttachmentKinds.Contains(preferredKind))
                {
                    return preferredKind;
                }
            }
        }

        return HasNewLegacyReferencesSince(before) ? "legacy_reference" : "none";
    }

    public bool HasNewReferencesSince(CodexComposerReferenceSnapshot before)
    {
        return HasNewLegacyReferencesSince(before) || HasNewAttachmentsSince(before);
    }
}

public sealed record CodexComposerAttachmentNodeMetadata(
    string ControlType,
    string AutomationId,
    string Name,
    string ClassName,
    string FrameworkId,
    string ParentHierarchy,
    UiAutomationBounds Bounds,
    UiAutomationBounds ScopeBounds,
    bool IsOffscreen,
    bool IsWithinComposerScope,
    int ChildCount,
    bool HasInvokePattern,
    string LocalizedControlType,
    string HelpText,
    string ItemStatus,
    string ItemType);

public static class CodexComposerAttachmentDetector
{
    private static readonly string[] StableAttachmentTokens =
    [
        "attachment",
        "pasted",
        "reference",
        "upload",
        "file",
        "card"
    ];

    private static readonly string[] CardControlTokens =
    [
        "group",
        "custom",
        "pane",
        "listitem",
        "dataitem",
        "document"
    ];

    public static string? TryClassify(CodexComposerAttachmentNodeMetadata node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsWithinComposerScope || node.IsOffscreen || node.Bounds.IsEmpty)
        {
            return null;
        }

        var controlType = node.ControlType ?? string.Empty;
        var parentHierarchy = node.ParentHierarchy ?? string.Empty;
        var stableMetadata = string.Join(
            " ",
            node.AutomationId,
            node.ClassName,
            parentHierarchy,
            controlType);
        var hasStableAttachmentSignal = ContainsAny(
            stableMetadata,
            StableAttachmentTokens);
        var isCardControl = ContainsAny(controlType, CardControlTokens);
        var hasCardParent = ContainsAny(parentHierarchy, CardControlTokens) ||
                            ContainsAny(parentHierarchy, StableAttachmentTokens);
        var isActionControl = node.HasInvokePattern ||
                              ContainsAny(
                                  controlType,
                                  ["button", "hyperlink", "text", "document", "custom", "listitem"]);
        var isShowInComposerAction = IsShowInComposerAction(node);

        if (isShowInComposerAction && isActionControl && hasCardParent)
        {
            return "action";
        }

        if (hasStableAttachmentSignal &&
            isCardControl &&
            (node.ChildCount > 0 || hasCardParent))
        {
            return "structure";
        }

        return null;
    }

    public static string BuildFingerprint(CodexComposerAttachmentNodeMetadata node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return string.Join(
            "|",
            NormalizeFingerprintPart(node.ControlType),
            NormalizeFingerprintPart(node.AutomationId),
            NormalizeFingerprintPart(node.ClassName),
            NormalizeFingerprintPart(node.ParentHierarchy),
            IsShowInComposerAction(node) ? "show_in_composer_action" : "other_name",
            BuildRelativeArea(node.Bounds, node.ScopeBounds));
    }

    private static bool IsShowInComposerAction(CodexComposerAttachmentNodeMetadata node) =>
        ContainsAny(
            string.Join(
                " ",
                node.Name,
                node.LocalizedControlType,
                node.HelpText,
                node.ItemStatus,
                node.ItemType),
            [
                "在文本框中显示",
                "show in text box",
                "show in composer",
                "show in editor",
                "display in text box",
                "display in composer",
                "open in text box",
                "open in composer",
                "view in text box",
                "view in composer"
            ]);

    private static string BuildRelativeArea(
        UiAutomationBounds bounds,
        UiAutomationBounds scopeBounds)
    {
        if (bounds.IsEmpty || scopeBounds.IsEmpty)
        {
            return "unknown";
        }

        var horizontalBand = QuantizeRelativePosition(
            (bounds.Left - scopeBounds.Left) / scopeBounds.Width);
        var verticalBand = QuantizeRelativePosition(
            (bounds.Top - scopeBounds.Top) / scopeBounds.Height);
        var widthBand = QuantizeRelativePosition(bounds.Width / scopeBounds.Width);
        var heightBand = QuantizeRelativePosition(bounds.Height / scopeBounds.Height);
        return $"{horizontalBand}:{verticalBand}:{widthBand}:{heightBand}";
    }

    private static int QuantizeRelativePosition(double value) =>
        Math.Clamp((int)Math.Floor(value * 10), -20, 20);

    private static string NormalizeFingerprintPart(string value) =>
        (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim()
            .ToLowerInvariant();

    private static bool ContainsAny(string value, IEnumerable<string> tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}

public interface ICodexDesktopComposerInjector
{
    Task<OpenAiDesktopInspection> InspectOpenAiDesktopWindowsAsync(CancellationToken cancellationToken = default);

    Task<CodexComposerInjectionResult> InjectAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool allowReplacingExistingText = false);
}

public enum CodexComposerContentState
{
    Empty,
    HasContent,
    Unknown
}

public static class CodexComposerContentGuard
{
    public static bool RequiresConfirmation(
        CodexComposerContentState state,
        bool allowReplacingExistingText) =>
        !allowReplacingExistingText && state is not CodexComposerContentState.Empty;

    public static CodexComposerContentState DetermineContentState(
        bool isProseMirror,
        bool textPatternAvailable,
        string? textPatternText,
        bool valuePatternAvailable,
        string? valuePatternValue,
        string? accessibilityName)
    {
        var normalizedText = NormalizeComposerTextForEmptiness(textPatternText);
        var normalizedValue = NormalizeComposerTextForEmptiness(valuePatternValue);
        var normalizedName = NormalizeComposerTextForEmptiness(accessibilityName);

        if (isProseMirror)
        {
            if (textPatternAvailable && valuePatternAvailable &&
                normalizedText.Length == 0 && normalizedValue.Length == 0)
            {
                return CodexComposerContentState.Empty;
            }

            var valueLooksLikePlaceholder = valuePatternAvailable &&
                                            normalizedName.Length > 0 &&
                                            normalizedValue.Equals(normalizedName, StringComparison.Ordinal);
            var textLooksLikePlaceholder = textPatternAvailable &&
                                           normalizedName.Length > 0 &&
                                           normalizedText.Equals(normalizedName, StringComparison.Ordinal);
            if (valueLooksLikePlaceholder &&
                (!textPatternAvailable || normalizedText.Length == 0 || textLooksLikePlaceholder))
            {
                return CodexComposerContentState.Empty;
            }

            if (textPatternAvailable && normalizedText.Length > 0)
            {
                return CodexComposerContentState.HasContent;
            }

            if (valuePatternAvailable && normalizedValue.Length > 0)
            {
                return CodexComposerContentState.HasContent;
            }

            return textPatternAvailable || valuePatternAvailable
                ? CodexComposerContentState.Empty
                : CodexComposerContentState.Unknown;
        }

        if (valuePatternAvailable)
        {
            return normalizedValue.Length == 0
                ? CodexComposerContentState.Empty
                : CodexComposerContentState.HasContent;
        }

        return CodexComposerContentState.Unknown;
    }

    public static string NormalizeComposerTextForEmptiness(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return new string(value
                .Where(character => !IsZeroWidthCharacter(character))
                .ToArray())
            .Trim();
    }

    private static bool IsZeroWidthCharacter(char character) =>
        character is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF';
}

public static class CodexComposerDiagnostics
{
    public static void WriteStage(string stage, Stopwatch? stopwatch = null)
    {
        var elapsed = stopwatch?.ElapsedMilliseconds ?? 0;
        StartupDiagnostics.Write($"Codex composer stage={stage} elapsedMs={elapsed}");
    }

    public static void WritePayloadMetadata(string stage, string text)
    {
        var normalized = CodexComposerWriteVerifier.NormalizeComposerTextForVerification(text);
        var lineCount = normalized.Length == 0 ? 0 : normalized.Count(character => character == '\n') + 1;
        StartupDiagnostics.Write(
            $"Codex composer {stage} " +
            $"sourceLength={text.Length} " +
            $"sourceLines={lineCount} " +
            $"sourceUtf8ByteCount={Encoding.UTF8.GetByteCount(text)}");
    }
}

public static class CodexComposerCandidateSelector
{
    private static readonly string[] ComposerClassTokens =
    [
        "ProseMirror",
        "RichTextInput",
        "ComposerLayout",
        "composer"
    ];

    private static readonly string[] ParentComposerTokens =
    [
        "ProseMirror",
        "RichTextInput",
        "ComposerLayout",
        "composer",
        "prompt",
        "message",
        "input",
        "editor"
    ];

    private static readonly string[] StrongChromiumComposerTokens =
    [
        "ProseMirror",
        "RichTextInput",
        "ComposerLayout",
        "thread-scroll-container"
    ];

    private static readonly string[] SemanticTokens =
    [
        "composer",
        "prompt",
        "message",
        "input",
        "editor",
        "chat"
    ];

    private static readonly string[] NonComposerTokens =
    [
        "send",
        "submit",
        "search",
        "settings",
        "navigation"
    ];

    public static bool TrySelect(
        IReadOnlyList<CodexComposerCandidate> candidates,
        out CodexComposerCandidate? selected)
    {
        selected = null;
        var eligible = candidates
            .Where(IsEligible)
            .Select(candidate => (Candidate: candidate, Score: Score(candidate)))
            .OrderByDescending(item => item.Score)
            .ToList();

        if (eligible.Count == 0)
        {
            return false;
        }

        // Never guess across multiple OpenAI windows. The user may have more
        // than one project or session open, and this MVP has no safe way to
        // infer which one they intended after clicking BlueRelay.
        if (eligible.Select(item => item.Candidate.Metadata.Handle).Distinct().Count() != 1)
        {
            return false;
        }

        var best = eligible[0];
        var second = eligible.Count > 1 ? eligible[1] : default;
        if (eligible.Count > 1 && best.Score - second.Score < 15)
        {
            return false;
        }

        if (best.Score < 60)
        {
            return false;
        }

        selected = best.Candidate;
        return true;
    }

    public static int Score(CodexComposerCandidate candidate)
    {
        if (!IsEligible(candidate))
        {
            return int.MinValue;
        }

        var metadata = candidate.Metadata;
        var score = metadata.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            ? 58
            : metadata.ControlType.Equals("Document", StringComparison.OrdinalIgnoreCase)
                ? 54
                : 42;

        score += candidate.IsOpenAiWindow ? 18 : 0;
        score += metadata.IsKeyboardFocusable ? 10 : 0;
        score += candidate.SupportsValuePattern && !candidate.IsValueReadOnly ? 10 : 0;
        score += candidate.SupportsTextPattern ? 8 : 0;
        score += IsChromiumFramework(metadata.FrameworkId) ? 12 : 0;
        score += HasClassTokenAny(metadata.ClassName, ComposerClassTokens) ? 55 : 0;
        score += ContainsToken(metadata.ParentHierarchy, ParentComposerTokens) ? 20 : 0;
        score += SemanticSignal(metadata.AutomationId, metadata.Name, metadata.ClassName);
        score += RelativeBottomSignal(metadata);
        return score;
    }

    public static bool IsHighConfidence(CodexComposerCandidate candidate)
    {
        if (!IsEligible(candidate))
        {
            return false;
        }

        var metadata = candidate.Metadata;
        return metadata.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
               metadata.FrameworkId.Equals("Chrome", StringComparison.OrdinalIgnoreCase) &&
               HasClassToken(metadata.ClassName, "ProseMirror");
    }

    public static bool RequiresClipboardPaste(CodexComposerCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Metadata.FrameworkId.Equals("Chrome", StringComparison.OrdinalIgnoreCase) &&
               HasClassToken(candidate.Metadata.ClassName, "ProseMirror");
    }

    public static bool HasClassToken(string className, string token)
    {
        return className
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(value => value.Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEligible(CodexComposerCandidate candidate)
    {
        var metadata = candidate.Metadata;
        var isEditableControl = metadata.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                                metadata.ControlType.Equals("Document", StringComparison.OrdinalIgnoreCase) ||
                                metadata.ControlType.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        var hasEditablePattern = candidate.SupportsValuePattern || candidate.SupportsTextPattern;
        var hasStrongComposerSignal = HasClassTokenAny(metadata.ClassName, StrongChromiumComposerTokens) ||
                                      ContainsToken(
                                          string.Empty,
                                          metadata.AutomationId,
                                          metadata.Name,
                                          metadata.ParentHierarchy,
                                          StrongChromiumComposerTokens);
        var isChromiumCandidate = IsChromiumFramework(metadata.FrameworkId);
        return candidate.IsOpenAiWindow &&
               metadata.Handle != IntPtr.Zero &&
               isEditableControl &&
               metadata.IsEnabled &&
               metadata.IsKeyboardFocusable &&
               !metadata.IsOffscreen &&
               !metadata.Bounds.IsEmpty &&
               hasEditablePattern &&
               (!isChromiumCandidate || hasStrongComposerSignal) &&
               !ContainsToken(string.Empty, metadata.AutomationId, metadata.Name, metadata.ParentHierarchy, NonComposerTokens);
    }

    private static int SemanticSignal(string automationId, string name, string className)
    {
        var signal = 0;
        if (ContainsToken(className, automationId, name, string.Empty, SemanticTokens))
        {
            signal += 22;
        }

        return signal;
    }

    private static int RelativeBottomSignal(UiAutomationMetadata metadata)
    {
        if (metadata.Bounds.IsEmpty)
        {
            return 0;
        }

        // A composer is commonly near the bottom of its window. This is only a
        // relative accessibility-tree signal; it is never a screen coordinate.
        var windowHeight = metadata.WindowBounds.Height;
        var relativePosition = windowHeight <= 0
            ? 0
            : (metadata.Bounds.Bottom - metadata.WindowBounds.Top) / windowHeight;
        return relativePosition >= 0.75 ? 5 : 0;
    }

    private static bool IsChromiumFramework(string frameworkId) =>
        frameworkId.Equals("Chrome", StringComparison.OrdinalIgnoreCase) ||
        frameworkId.Equals("Chromium", StringComparison.OrdinalIgnoreCase) ||
        frameworkId.Equals("WebView2", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsToken(string value, IEnumerable<string> tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool HasClassTokenAny(string className, IEnumerable<string> tokens) =>
        tokens.Any(token => HasClassToken(className, token));

    private static bool ContainsToken(
        string className,
        string automationId,
        string name,
        string parentHierarchy,
        IEnumerable<string> tokens)
    {
        return ContainsToken($"{className} {automationId} {name} {parentHierarchy}", tokens);
    }
}
