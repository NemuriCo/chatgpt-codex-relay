using System.Runtime.InteropServices;
using BlueRelay.Services.Bridges;
using BlueRelay.Services.Desktop;
using BlueRelay.Services.Dialogs;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexDesktopComposerTests
{
    [TestMethod]
    public void SelectsOnlyUnambiguousOpenAiComposer()
    {
        var candidate = Candidate(
            handle: 100,
            controlType: "Edit",
            automationId: "message-composer",
            name: "Message editor",
            supportsValuePattern: true,
            className: "ProseMirror ProseMirror-focused");

        var selected = CodexComposerCandidateSelector.TrySelect([candidate], out var result);

        Assert.IsTrue(selected);
        Assert.AreSame(candidate, result);
    }

    [TestMethod]
    public void RejectsAmbiguousComposersAndDifferentOpenAiWindows()
    {
        var first = Candidate(100, "Edit", "input-one", "Prompt", supportsValuePattern: true);
        var second = Candidate(100, "Document", "input-two", "Message", supportsValuePattern: true);
        var otherWindow = Candidate(200, "Document", "message-composer", "Message editor", supportsValuePattern: true);

        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([first, second], out _));
        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([first, otherWindow], out _));
    }

    [TestMethod]
    public void RejectsControlsThatAreNotSafeEditableOpenAiCandidates()
    {
        var nonOpenAi = Candidate(100, "Edit", "message-composer", "Message editor", supportsValuePattern: true, isOpenAiWindow: false);
        var readOnly = Candidate(
            100,
            "Document",
            "message-composer",
            "Message editor",
            supportsValuePattern: true,
            isReadOnly: true,
            className: "ProseMirror");
        var sendButton = Candidate(100, "Edit", "send-input", "Send", supportsValuePattern: true);

        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([nonOpenAi], out _));
        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([readOnly], out _));
        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([sendButton], out _));
    }

    [TestMethod]
    public void UsesParentAutomationHierarchyAsAComposerSignal()
    {
        var candidate = Candidate(
            100,
            "Document",
            "",
            "",
            supportsValuePattern: false,
            supportsTextPattern: true,
            parentHierarchy: "Pane#composer-container > Document",
            frameworkId: "Win32");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([candidate], out var selected));
        Assert.AreSame(candidate, selected);
    }

    [TestMethod]
    public void ComposesFullTaskWithUnicodeUserNoteWithoutSending()
    {
        const string note = "顺便检查按钮文字 😊";
        const string payload = "# CODEX_TASK\n\n```csharp\nvar path = \"C:\\\\Projects\\\\BlueProject\";\n```";

        var result = RelayPromptComposer.Compose(note, payload);

        Assert.AreEqual(
            "用户补充：\n顺便检查按钮文字 😊\n\n完整任务：\n# CODEX_TASK\n\n```csharp\nvar path = \"C:\\\\Projects\\\\BlueProject\";\n```",
            result);
    }

    [TestMethod]
    public void ClipboardRestoreFailureIsExposedWithoutPretendingInjectionSucceeded()
    {
        var result = CodexComposerInjectionResult.Failed(
            "codex_composer_injection_failed",
            "failed",
            clipboardRestoreFailed: true);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.ClipboardRestoreFailed);
    }

    [TestMethod]
    public void RealChromeProseMirrorComposerIsPreferred()
    {
        var composer = Candidate(
            100,
            "Edit",
            "",
            "随心输入",
            supportsValuePattern: true,
            className: "ProseMirror ProseMirror-focused",
            frameworkId: "Chrome");
        var unrelatedChromeEdit = Candidate(
            100,
            "Edit",
            "toolbar-input",
            "Address bar",
            supportsValuePattern: true,
            className: "Chrome_RenderWidgetHostHWND",
            frameworkId: "Chrome",
            parentHierarchy: "Pane#toolbar");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([composer, unrelatedChromeEdit], out var selected));
        Assert.AreSame(composer, selected);
        Assert.IsTrue(CodexComposerCandidateSelector.IsHighConfidence(composer));
        Assert.IsFalse(CodexComposerCandidateSelector.IsHighConfidence(unrelatedChromeEdit));
    }

    [TestMethod]
    public void ChromeProseMirrorComposerAlwaysUsesClipboardPasteStrategy()
    {
        var proseMirror = Candidate(
            100,
            "Edit",
            "message-composer",
            "随心输入",
            supportsValuePattern: true,
            className: "ProseMirror ProseMirror-focused",
            frameworkId: "Chrome");
        var genericEditor = proseMirror with
        {
            Metadata = proseMirror.Metadata with
            {
                ClassName = "RichTextInput",
                FrameworkId = "Win32"
            }
        };

        Assert.IsTrue(CodexComposerCandidateSelector.RequiresClipboardPaste(proseMirror));
        Assert.IsFalse(CodexComposerCandidateSelector.RequiresClipboardPaste(genericEditor));
    }

    [TestMethod]
    public void CtrlVInputSequenceUsesVirtualKeyDownAndUpOrder()
    {
        var sequence = CodexKeyboardInputSequence.CtrlV;

        Assert.AreEqual(4, sequence.Count);
        Assert.AreEqual(new CodexKeyboardInput(0x11, KeyUp: false), sequence[0]);
        Assert.AreEqual(new CodexKeyboardInput(0x56, KeyUp: false), sequence[1]);
        Assert.AreEqual(new CodexKeyboardInput(0x56, KeyUp: true), sequence[2]);
        Assert.AreEqual(new CodexKeyboardInput(0x11, KeyUp: true), sequence[3]);
    }

    [TestMethod]
    public void SendInputNativeLayoutMatchesWin32Abi()
    {
        var expectedInputSize = IntPtr.Size == 8 ? 40 : 28;
        var expectedUnionSize = IntPtr.Size == 8 ? 32 : 24;
        var expectedKeyboardInputSize = IntPtr.Size == 8 ? 24 : 16;

        Assert.AreEqual(expectedInputSize, Marshal.SizeOf<CodexSendInputNative>());
        Assert.AreEqual(expectedUnionSize, Marshal.SizeOf<CodexSendInputUnion>());
        Assert.AreEqual(expectedKeyboardInputSize, Marshal.SizeOf<CodexSendInputKeyboard>());
        Assert.AreEqual(0, Marshal.OffsetOf<CodexSendInputUnion>(nameof(CodexSendInputUnion.Keyboard)).ToInt32());
        Assert.AreEqual(IntPtr.Size == 8 ? 8 : 4,
            Marshal.OffsetOf<CodexSendInputNative>(nameof(CodexSendInputNative.Data)).ToInt32());
    }

    [TestMethod]
    public void SendInputKeyboardExtraInfoIsPointerSized()
    {
        var field = typeof(CodexSendInputKeyboard).GetField(nameof(CodexSendInputKeyboard.ExtraInfo));

        Assert.IsNotNull(field);
        Assert.AreEqual(typeof(IntPtr), field!.FieldType);
        Assert.AreEqual(IntPtr.Size == 8 ? 16 : 12,
            Marshal.OffsetOf<CodexSendInputKeyboard>(nameof(CodexSendInputKeyboard.ExtraInfo)).ToInt32());
    }

    [TestMethod]
    public void PartialSendInputCountIsFailure()
    {
        var result = new CodexKeyboardInputSendResult(
            RequestedInputCount: 4,
            SentInputCount: 3,
            Win32Error: 5);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void ClipboardVerificationRequiresTextAndExactSourceLength()
    {
        Assert.IsTrue(CodexClipboardWriteVerifier.Verify(true, 24, 24).IsVerified);
        Assert.IsFalse(CodexClipboardWriteVerifier.Verify(false, 24, 24).IsVerified);
        Assert.IsFalse(CodexClipboardWriteVerifier.Verify(true, 23, 24).IsVerified);
    }

    [TestMethod]
    public void FormalComposerPasteCanProceedWhenClipboardSnapshotIsUnavailable()
    {
        Assert.IsTrue(CodexComposerInputDecision.CanSendFormalCtrlV(
            foregroundMatchesTarget: true,
            uiaFocus: true,
            clipboardSet: true,
            clipboardVerified: true,
            clipboardSnapshotAvailable: false));
        Assert.IsTrue(CodexComposerInputDecision.CanSendFormalCtrlV(
            foregroundMatchesTarget: true,
            uiaFocus: true,
            clipboardSet: true,
            clipboardVerified: true,
            clipboardSnapshotAvailable: true));
    }

    [TestMethod]
    public void ClipboardTextComparerNormalizesLineEndingsButRequiresExactText()
    {
        Assert.IsTrue(CodexClipboardTextComparer.Matches(
            "first\r\nsecond\r\n",
            "first\nsecond\n"));
        Assert.IsFalse(CodexClipboardTextComparer.Matches(
            "first\nsecond\n",
            "first\nsecond\nwith note"));
    }

    [TestMethod]
    public void SuccessfulFillWithUnavailableSnapshotIsSuccessWithWarning()
    {
        var result = CodexComposerInjectionResult.Filled(
            "filled",
            usedClipboardFallback: true,
            mode: CodexComposerInjectionMode.ClipboardInlineVerified,
            clipboardSnapshotMode: CodexClipboardSnapshotMode.Unavailable,
            clipboardRestoreUnavailable: true);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.ClipboardWarning);
        Assert.IsTrue(result.ClipboardRestoreUnavailable);
        Assert.IsFalse(result.ClipboardRestoreFailed);
    }

    [TestMethod]
    public void RestoreFailureDoesNotTurnVerifiedFillIntoFailure()
    {
        var result = CodexComposerInjectionResult.Filled(
            "filled",
            usedClipboardFallback: true,
            clipboardRestoreFailed: true,
            mode: CodexComposerInjectionMode.ClipboardInlineVerified);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.ClipboardWarning);
    }

    [TestMethod]
    public void ClipboardRetryPolicyIsBoundedAndNeverDelaysPastRemainingBudget()
    {
        Assert.AreEqual(
            21,
            CodexClipboardRetryPolicy.GetMaximumAttempts(
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(25)));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(25),
            CodexClipboardRetryPolicy.GetDelay(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(25)));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(10),
            CodexClipboardRetryPolicy.GetDelay(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(25)));
        Assert.AreEqual(
            TimeSpan.Zero,
            CodexClipboardRetryPolicy.GetDelay(
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(25)));
    }

    [TestMethod]
    public void ProseMirrorClassTokenSurvivesFocusAndBuildHashChanges()
    {
        var candidate = Candidate(
            100,
            "Edit",
            "",
            "Type your message",
            supportsValuePattern: true,
            className: "ProseMirror",
            frameworkId: "Chrome",
            parentHierarchy: "Group@_RichTextInput_newhash > Pane@_ComposerLayoutRoot_otherhash");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([candidate], out _));
    }

    [TestMethod]
    public void ProseMirrorMustBeAWhitespaceSeparatedClassToken()
    {
        var candidate = Candidate(
            100,
            "Edit",
            "message-editor",
            "随心输入",
            supportsValuePattern: true,
            className: "SomeProseMirrorThing",
            frameworkId: "Chrome",
            parentHierarchy: "Pane#unrelated");

        Assert.IsFalse(CodexComposerCandidateSelector.IsHighConfidence(candidate));
        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([candidate], out _));
    }

    [TestMethod]
    public void ResolvesSelectedCandidateToItsAutomationElementByCandidateIdentity()
    {
        var candidate = Candidate(
            100,
            "Edit",
            "message-composer",
            "Message editor",
            supportsValuePattern: true,
            className: "ProseMirror");
        var selected = CodexComposerCandidateSelector.TrySelect([candidate], out var selectedCandidate);
        var target = new FakeAutomationTarget();

        Assert.IsTrue(selected);
        Assert.AreSame(candidate, selectedCandidate);
        Assert.IsTrue(CodexComposerCandidateResolver.TryResolveElement(
            [(target, candidate)],
            selectedCandidate!,
            out var resolvedTarget));
        Assert.AreSame(target, resolvedTarget);
    }

    [TestMethod]
    public void SelectionLostReturnsSafeFailureWithoutThrowing()
    {
        var selectedCandidate = Candidate(
            100,
            "Edit",
            "message-composer",
            "Message editor",
            supportsValuePattern: true,
            className: "ProseMirror");
        var currentCandidate = selectedCandidate with
        {
            Metadata = selectedCandidate.Metadata with { Name = "A newer composer element" }
        };

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([selectedCandidate], out var selected));
        Assert.IsFalse(CodexComposerCandidateResolver.TryResolveElement(
            [(new FakeAutomationTarget(), currentCandidate)],
            selected!,
            out var resolvedTarget));
        Assert.IsNull(resolvedTarget);

        var failure = CodexComposerInjectionResult.Failed(
            "codex_composer_selection_lost",
            "The selected Codex composer could not be resolved for injection.");
        Assert.IsFalse(failure.Success);
        Assert.AreEqual("codex_composer_selection_lost", failure.Code);
    }

    [TestMethod]
    public void CustomComposerSurfaceWithEditableParentIsRecognized()
    {
        var candidate = Candidate(
            100,
            "Custom",
            "",
            "",
            supportsValuePattern: false,
            supportsTextPattern: true,
            className: "ComposerSurface",
            frameworkId: "Chrome",
            parentHierarchy: "Edit@ProseMirror > Group@ComposerLayoutRoot");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([candidate], out _));
    }

    [TestMethod]
    public void ReadOnlyValuePatternRemainsAnExactFallbackCandidate()
    {
        var candidate = Candidate(
            100,
            "Edit",
            "",
            "localized placeholder",
            supportsValuePattern: true,
            isReadOnly: true,
            className: "ProseMirror",
            frameworkId: "Chrome");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([candidate], out var selected));
        Assert.IsTrue(selected!.IsValueReadOnly);
    }

    [TestMethod]
    public void WritableValuePatternIsPreferredOverReadOnlyCandidate()
    {
        var writable = Candidate(
            100,
            "Edit",
            "",
            "",
            supportsValuePattern: true,
            className: "ProseMirror");
        var readOnly = writable with { IsValueReadOnly = true };

        Assert.IsTrue(CodexComposerCandidateSelector.Score(writable) > CodexComposerCandidateSelector.Score(readOnly));
    }

    [TestMethod]
    public void OffscreenAndDisabledProseMirrorCandidatesAreRejected()
    {
        var offscreen = Candidate(
            100,
            "Edit",
            "",
            "",
            supportsValuePattern: true,
            className: "ProseMirror",
            isOffscreen: true);
        var disabled = Candidate(
            100,
            "Edit",
            "",
            "",
            supportsValuePattern: true,
            className: "ProseMirror",
            isEnabled: false);

        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([offscreen], out _));
        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([disabled], out _));
    }

    [TestMethod]
    public void BoundedTraversalFindsComposerPastLegacyDepthAndNodeBudget()
    {
        var root = new TraversalNode();
        var current = root;
        for (var depth = 0; depth < 24; depth++)
        {
            var child = new TraversalNode();
            current.Children.Add(child);
            current = child;
        }

        current.IsEdit = true;
        current.IsProseMirror = true;
        current.IsCandidate = true;

        var result = BoundedComposerTreeTraversal.Search<TraversalNode, TraversalNode>(
            [root],
            new ComposerTraversalLimits(3000, 48, 256),
            System.Diagnostics.Stopwatch.StartNew(),
            TimeSpan.FromSeconds(2),
            node => node.Children,
            node => node.IsEdit,
            _ => false,
            node => node.IsProseMirror,
            node => node.IsCandidate ? node : null,
            node => node.IsCandidate);

        Assert.IsTrue(result.FoundHighConfidenceCandidate);
        Assert.AreEqual(25, result.Statistics.MaxDepthReached);
        Assert.AreEqual(1, result.Statistics.ProseMirrorSeen);
        Assert.AreSame(current, result.Candidates[0]);

        var largeBranch = CreateFullBinaryTree(10);
        var largeRoot = new TraversalNode();
        largeRoot.Children.Add(largeBranch);
        var lateCandidate = new TraversalNode
        {
            IsEdit = true,
            IsProseMirror = true,
            IsCandidate = true
        };
        largeRoot.Children.Add(lateCandidate);

        var largeResult = BoundedComposerTreeTraversal.Search<TraversalNode, TraversalNode>(
            [largeRoot],
            new ComposerTraversalLimits(3000, 48, 256),
            System.Diagnostics.Stopwatch.StartNew(),
            TimeSpan.FromSeconds(2),
            node => node.Children,
            node => node.IsEdit,
            _ => false,
            node => node.IsProseMirror,
            node => node.IsCandidate ? node : null,
            node => node.IsCandidate);

        Assert.IsTrue(largeResult.FoundHighConfidenceCandidate);
        Assert.IsTrue(largeResult.Statistics.VisitedNodes > 512);
        Assert.AreSame(lateCandidate, largeResult.Candidates[0]);
    }

    [TestMethod]
    public void BoundedTraversalStopsWhenControlViewFindsHighConfidenceComposer()
    {
        var childTraversalCalls = 0;
        var composer = new TraversalNode
        {
            IsEdit = true,
            IsProseMirror = true,
            IsCandidate = true
        };
        composer.Children.Add(new TraversalNode());

        var result = BoundedComposerTreeTraversal.Search<TraversalNode, TraversalNode>(
            [composer],
            new ComposerTraversalLimits(3000, 48, 256),
            System.Diagnostics.Stopwatch.StartNew(),
            TimeSpan.FromSeconds(2),
            node =>
            {
                childTraversalCalls++;
                return node.Children;
            },
            node => node.IsEdit,
            _ => false,
            node => node.IsProseMirror,
            node => node.IsCandidate ? node : null,
            node => node.IsCandidate);

        Assert.IsTrue(result.FoundHighConfidenceCandidate);
        Assert.AreEqual(0, childTraversalCalls);
    }

    [TestMethod]
    public void RawViewFallbackCanFindComposerWhenControlViewDoesNot()
    {
        var controlViewResult = BoundedComposerTreeTraversal.Search<TraversalNode, TraversalNode>(
            [new TraversalNode()],
            new ComposerTraversalLimits(3000, 48, 256),
            System.Diagnostics.Stopwatch.StartNew(),
            TimeSpan.FromSeconds(2),
            node => node.Children,
            node => node.IsEdit,
            _ => false,
            node => node.IsProseMirror,
            node => node.IsCandidate ? node : null,
            node => node.IsCandidate);

        var rawCandidate = new TraversalNode
        {
            IsEdit = true,
            IsProseMirror = true,
            IsCandidate = true
        };
        var rawViewResult = BoundedComposerTreeTraversal.Search<TraversalNode, TraversalNode>(
            [rawCandidate],
            new ComposerTraversalLimits(3000, 48, 256),
            System.Diagnostics.Stopwatch.StartNew(),
            TimeSpan.FromSeconds(2),
            node => node.Children,
            node => node.IsEdit,
            _ => true,
            node => node.IsProseMirror,
            node => node.IsCandidate ? node : null,
            node => node.IsCandidate);

        Assert.IsFalse(controlViewResult.FoundHighConfidenceCandidate);
        Assert.IsTrue(rawViewResult.FoundHighConfidenceCandidate);
        Assert.AreSame(rawCandidate, rawViewResult.Candidates[0]);
    }

    [TestMethod]
    public void MultipleEditCandidatesAreResolvedByComposerConfidence()
    {
        var composer = Candidate(
            100,
            "Edit",
            "",
            "",
            supportsValuePattern: true,
            className: "ProseMirror");
        var weakerEdit = Candidate(
            100,
            "Edit",
            "message-input",
            "",
            supportsValuePattern: true,
            className: "Chrome_RenderWidgetHostHWND",
            parentHierarchy: "Group@RichTextInput");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([weakerEdit, composer], out var selected));
        Assert.AreSame(composer, selected);
    }

    [TestMethod]
    public void ExistingComposerContentRequiresExplicitReplacementConfirmation()
    {
        Assert.IsFalse(CodexComposerContentGuard.RequiresConfirmation(
            CodexComposerContentState.Empty,
            allowReplacingExistingText: false));
        Assert.IsTrue(CodexComposerContentGuard.RequiresConfirmation(
            CodexComposerContentState.HasContent,
            allowReplacingExistingText: false));
        Assert.IsTrue(CodexComposerContentGuard.RequiresConfirmation(
            CodexComposerContentState.Unknown,
            allowReplacingExistingText: false));
        Assert.IsFalse(CodexComposerContentGuard.RequiresConfirmation(
            CodexComposerContentState.HasContent,
            allowReplacingExistingText: true));
    }

    [TestMethod]
    public void EmptyProseMirrorPlaceholderUsesEmptyTextPatternState()
    {
        var state = CodexComposerContentGuard.DetermineContentState(
            isProseMirror: true,
            textPatternAvailable: true,
            textPatternText: string.Empty,
            valuePatternAvailable: true,
            valuePatternValue: "随心输入",
            accessibilityName: "随心输入");

        Assert.AreEqual(CodexComposerContentState.Empty, state);
    }

    [TestMethod]
    public void EnglishProseMirrorPlaceholderDoesNotDependOnLocalizedText()
    {
        var state = CodexComposerContentGuard.DetermineContentState(
            isProseMirror: true,
            textPatternAvailable: true,
            textPatternText: string.Empty,
            valuePatternAvailable: true,
            valuePatternValue: "Ask anything",
            accessibilityName: "Ask anything");

        Assert.AreEqual(CodexComposerContentState.Empty, state);
    }

    [TestMethod]
    public void ProseMirrorPlaceholderWithTrailingNewlineIsEmptyForMultipleLanguages()
    {
        foreach (var placeholder in new[] { "随心输入", "Ask anything" })
        {
            var state = CodexComposerContentGuard.DetermineContentState(
                isProseMirror: true,
                textPatternAvailable: true,
                textPatternText: placeholder + "\n",
                valuePatternAvailable: true,
                valuePatternValue: placeholder + "\n",
                accessibilityName: placeholder);

            Assert.AreEqual(CodexComposerContentState.Empty, state, placeholder);
        }
    }

    [TestMethod]
    public void ProseMirrorRealTextIsHasContentEvenWithPlaceholderLikeName()
    {
        var state = CodexComposerContentGuard.DetermineContentState(
            isProseMirror: true,
            textPatternAvailable: true,
            textPatternText: "hello",
            valuePatternAvailable: true,
            valuePatternValue: "hello",
            accessibilityName: "随心输入");

        Assert.AreEqual(CodexComposerContentState.HasContent, state);
    }

    [TestMethod]
    public void ProseMirrorRealTextWithEmptyNameIsHasContent()
    {
        var state = CodexComposerContentGuard.DetermineContentState(
            isProseMirror: true,
            textPatternAvailable: true,
            textPatternText: "hello",
            valuePatternAvailable: true,
            valuePatternValue: "hello",
            accessibilityName: string.Empty);

        Assert.AreEqual(CodexComposerContentState.HasContent, state);
    }

    [TestMethod]
    public void RealTextWinsOverPlaceholderValueConflict()
    {
        var state = CodexComposerContentGuard.DetermineContentState(
            isProseMirror: true,
            textPatternAvailable: true,
            textPatternText: "hello",
            valuePatternAvailable: true,
            valuePatternValue: "Ask anything",
            accessibilityName: "Ask anything");

        Assert.AreEqual(CodexComposerContentState.HasContent, state);
    }

    [TestMethod]
    public void ContentNormalizationRemovesProviderTrailingWhitespaceAndZeroWidthCharacters()
    {
        Assert.AreEqual(
            "Ask anything",
            CodexComposerContentGuard.NormalizeComposerTextForEmptiness("Ask anything\n\u200B"));
    }

    [TestMethod]
    public void EmptyParagraphAndZeroWidthTextAreEmpty()
    {
        foreach (var text in new[] { string.Empty, "\r\n", "\n", "\u200B\u200C\u200D\u2060\uFEFF" })
        {
            var state = CodexComposerContentGuard.DetermineContentState(
                isProseMirror: true,
                textPatternAvailable: true,
                textPatternText: text,
                valuePatternAvailable: true,
                valuePatternValue: "随心输入",
                accessibilityName: "随心输入");

            Assert.AreEqual(CodexComposerContentState.Empty, state, $"Unexpected state for escaped text {text.Length}.");
        }
    }

    [TestMethod]
    public void ValuePatternIsFallbackWhenTextPatternIsUnavailable()
    {
        var state = CodexComposerContentGuard.DetermineContentState(
            isProseMirror: true,
            textPatternAvailable: false,
            textPatternText: null,
            valuePatternAvailable: true,
            valuePatternValue: "hello",
            accessibilityName: string.Empty);

        Assert.AreEqual(CodexComposerContentState.HasContent, state);
    }

    [TestMethod]
    public void ProseMirrorPlaceholderUsesValueNameFallbackWithoutChineseLiteral()
    {
        var state = CodexComposerContentGuard.DetermineContentState(
            isProseMirror: true,
            textPatternAvailable: false,
            textPatternText: null,
            valuePatternAvailable: true,
            valuePatternValue: "Ask anything",
            accessibilityName: "Ask anything");

        Assert.AreEqual(CodexComposerContentState.Empty, state);
    }

    [TestMethod]
    public void UnknownIsReturnedWhenNeitherPatternCanBeRead()
    {
        var state = CodexComposerContentGuard.DetermineContentState(
            isProseMirror: true,
            textPatternAvailable: false,
            textPatternText: null,
            valuePatternAvailable: false,
            valuePatternValue: null,
            accessibilityName: "随心输入");

        Assert.AreEqual(CodexComposerContentState.Unknown, state);
    }

    [TestMethod]
    public void ShortValuePatternWriteRequiresExactContentEquality()
    {
        var source = new string('x', 100);
        var verification = CodexComposerWriteVerifier.Verify(
            source,
            valueAvailable: true,
            value: source,
            textAvailable: true,
            text: source);

        Assert.IsTrue(verification.IsVerified);
        Assert.IsTrue(verification.ValueMatchesSource);
        Assert.IsTrue(verification.TextMatchesSource);
    }

    [TestMethod]
    public void SilentValuePatternTruncationFailsVerificationAndMustFallback()
    {
        var source = new string('x', 5000);
        var verification = CodexComposerWriteVerifier.Verify(
            source,
            valueAvailable: true,
            value: source[..1024],
            textAvailable: false,
            text: null);

        Assert.IsFalse(verification.IsVerified);
        Assert.IsFalse(verification.ValueMatchesSource);
        Assert.AreEqual(1024, verification.ValueLength);
    }

    [TestMethod]
    public void SameLengthChangedTextFailsVerification()
    {
        var source = "abcdef";
        var changed = "abcdeg";
        var verification = CodexComposerWriteVerifier.Verify(
            source,
            valueAvailable: true,
            value: changed,
            textAvailable: true,
            text: changed);

        Assert.AreEqual(source.Length, changed.Length);
        Assert.IsFalse(verification.IsVerified);
        Assert.IsFalse(verification.ValueMatchesSource);
        Assert.IsFalse(verification.TextMatchesSource);
    }

    [TestMethod]
    public void VerificationOnlyNormalizesProviderLineEndings()
    {
        const string source = "line one\r\nline two\r\n";
        const string destination = "line one\nline two\n";

        var verification = CodexComposerWriteVerifier.Verify(
            source,
            valueAvailable: true,
            value: destination,
            textAvailable: true,
            text: destination);

        Assert.IsTrue(verification.IsVerified);
        Assert.AreEqual("a\n b", CodexComposerWriteVerifier.NormalizeComposerTextForVerification("a\r\n b"));
    }

    [TestMethod]
    public void MarkdownUnicodeAndLargePayloadRemainExact()
    {
        var source = string.Join(
            "\r\n",
            "# heading",
            "**bold** `inline`",
            "```csharp",
            "var path = @\"C:\\\\Projects\\BlueRelay\";",
            "```",
            "{\"中文\":\"日本語 😊 ∞ →\"}");

        var verification = CodexComposerWriteVerifier.Verify(
            source,
            valueAvailable: true,
            value: source.Replace("\r\n", "\n", StringComparison.Ordinal),
            textAvailable: true,
            text: source.Replace("\r\n", "\n", StringComparison.Ordinal));

        Assert.IsTrue(verification.IsVerified);

        var large = string.Concat(Enumerable.Repeat("中文 English **bold** C# JSON PowerShell C:\\Projects\\BlueRelay 😊\n", 512));
        Assert.IsTrue(large.Length >= 16 * 1024);
        var largeVerification = CodexComposerWriteVerifier.Verify(
            large,
            valueAvailable: true,
            value: large,
            textAvailable: true,
            text: large);
        Assert.IsTrue(largeVerification.IsVerified);
    }

    [TestMethod]
    public void PlainTextRemainsInlineExactVerified()
    {
        const string source = "first\nmiddle\nlast";

        var verification = CodexComposerWriteVerifier.Verify(
            source,
            valueAvailable: true,
            value: source,
            textAvailable: true,
            text: source);

        Assert.IsTrue(verification.IsVerified);
        Assert.IsFalse(verification.IsRichTextTransformedAccepted);
    }

    [TestMethod]
    public void MarkdownHeadingAcceptsRichTextTransformation()
    {
        var verification = CodexComposerWriteVerifier.Verify(
            "# CODEX_TASK\n\n第一段。",
            valueAvailable: true,
            value: "CODEX_TASK\n第一段。",
            textAvailable: true,
            text: "CODEX_TASK\n第一段。");

        Assert.IsFalse(verification.IsVerified);
        Assert.IsTrue(verification.IsRichTextTransformedAccepted);
        Assert.AreEqual(2, verification.SemanticAnchorCount);
        Assert.AreEqual(2, verification.SemanticAnchorMatchedCount);
        Assert.IsTrue(verification.SemanticAnchorsInOrder);
    }

    [TestMethod]
    public void MarkdownListAcceptsRichTextTransformation()
    {
        var verification = CodexComposerWriteVerifier.Verify(
            "- item A\n- item B",
            valueAvailable: true,
            value: "item A\nitem B",
            textAvailable: true,
            text: "item A\nitem B");

        Assert.IsFalse(verification.IsVerified);
        Assert.IsTrue(verification.IsRichTextTransformedAccepted);
        Assert.IsTrue(verification.SemanticAnchorsInOrder);
    }

    [TestMethod]
    public void MarkdownFormattingAcceptsRichTextTransformation()
    {
        var verification = CodexComposerWriteVerifier.Verify(
            "**bold**\n`inline`",
            valueAvailable: true,
            value: "bold\ninline",
            textAvailable: false,
            text: null);

        Assert.IsFalse(verification.IsVerified);
        Assert.IsTrue(verification.IsRichTextTransformedAccepted);
        Assert.AreEqual(2, verification.SemanticAnchorMatchedCount);
    }

    [TestMethod]
    public void MissingLastSemanticAnchorRejectsTruncatedDestination()
    {
        var verification = CodexComposerWriteVerifier.Verify(
            "first\nmiddle\nlast",
            valueAvailable: true,
            value: "first\nmiddle",
            textAvailable: true,
            text: "first\nmiddle");

        Assert.IsFalse(verification.IsVerified);
        Assert.IsFalse(verification.IsRichTextTransformedAccepted);
        Assert.AreEqual(2, verification.SemanticAnchorMatchedCount);
        Assert.IsFalse(verification.SemanticAnchorsInOrder);
    }

    [TestMethod]
    public void LongRichTextRequiresStartAndEndAnchors()
    {
        var source = "# BLUERELAY_PAYLOAD_START_7F3A\n" +
                     string.Join("\n", Enumerable.Range(0, 160).Select(index => $"- section_{index} body_{index}")) +
                     "\n- BLUERELAY_PAYLOAD_END_91C2";
        var destination = source
            .Replace("# ", string.Empty, StringComparison.Ordinal)
            .Replace("- ", string.Empty, StringComparison.Ordinal);

        var verification = CodexComposerWriteVerifier.Verify(
            source,
            valueAvailable: true,
            value: destination,
            textAvailable: true,
            text: destination);

        Assert.IsFalse(verification.IsVerified);
        Assert.IsTrue(verification.IsRichTextTransformedAccepted);
        Assert.AreEqual(5, verification.SemanticAnchorCount);
        Assert.AreEqual(5, verification.SemanticAnchorMatchedCount);
    }

    [TestMethod]
    public void LongRichTextMissingEndAnchorIsRejected()
    {
        var source = "# BLUERELAY_PAYLOAD_START_7F3A\n" +
                     string.Join("\n", Enumerable.Range(0, 160).Select(index => $"- section_{index} body_{index}")) +
                     "\n- BLUERELAY_PAYLOAD_END_91C2";
        var destination = source
            .Replace("# ", string.Empty, StringComparison.Ordinal)
            .Replace("- ", string.Empty, StringComparison.Ordinal)
            .Replace("BLUERELAY_PAYLOAD_END_91C2", string.Empty, StringComparison.Ordinal);

        var verification = CodexComposerWriteVerifier.Verify(
            source,
            valueAvailable: true,
            value: destination,
            textAvailable: true,
            text: destination);

        Assert.IsFalse(verification.IsRichTextTransformedAccepted);
        Assert.AreEqual(4, verification.SemanticAnchorMatchedCount);
    }

    [TestMethod]
    public void EmptyDestinationIsNotAcceptedAfterSuccessfulInputSend()
    {
        var verification = CodexComposerWriteVerifier.Verify(
            "# CODEX_TASK\n\nfirst\nmiddle\nlast",
            valueAvailable: true,
            value: string.Empty,
            textAvailable: true,
            text: string.Empty);

        Assert.IsFalse(verification.IsVerified);
        Assert.IsFalse(verification.IsRichTextTransformedAccepted);
        Assert.AreEqual(0, verification.SemanticAnchorMatchedCount);
    }

    [TestMethod]
    public void ReferencedPastedTextSignalIsSeparateFromInlineEquality()
    {
        Assert.IsTrue(CodexComposerWriteVerifier.HasReferencedPastedTextSignal(
            "Referenced pasted text files:\n- pasted text file"));
        Assert.IsTrue(CodexComposerWriteVerifier.HasReferencedPastedTextSignal(
            "pasted text file: C:\\Users\\SleepyCobalt\\..."));
        Assert.IsFalse(CodexComposerWriteVerifier.HasReferencedPastedTextSignal(
            "ordinary composer text"));
    }

    [TestMethod]
    public void CurrentAttachmentActionIsDetectedWithoutRecordingTitleContent()
    {
        var match = CodexComposerAttachmentDetector.TryClassify(
            new CodexComposerAttachmentNodeMetadata(
                "ControlType.Button",
                "paste-action",
                "在文本框中显示",
                "AttachmentAction",
                "Chromium",
                "Group > Custom",
                new UiAutomationBounds(10, 700, 120, 28),
                new UiAutomationBounds(0, 500, 600, 300),
                IsOffscreen: false,
                IsWithinComposerScope: true,
                ChildCount: 0,
                HasInvokePattern: true,
                LocalizedControlType: "button",
                HelpText: string.Empty,
                ItemStatus: string.Empty,
                ItemType: string.Empty));

        Assert.AreEqual("action", match);
    }

    [TestMethod]
    public void AttachmentCardStructureIsDetectedWithStableMetadata()
    {
        var match = CodexComposerAttachmentDetector.TryClassify(
            new CodexComposerAttachmentNodeMetadata(
                "ControlType.Group",
                "pasted-text-card",
                "# CODEX_TASK hidden title",
                "CodexAttachmentCard",
                "Chromium",
                "Document > Group",
                new UiAutomationBounds(10, 600, 400, 90),
                new UiAutomationBounds(0, 500, 600, 300),
                IsOffscreen: false,
                IsWithinComposerScope: true,
                ChildCount: 2,
                HasInvokePattern: false,
                LocalizedControlType: "group",
                HelpText: string.Empty,
                ItemStatus: string.Empty,
                ItemType: string.Empty));

        Assert.AreEqual("structure", match);
    }

    [TestMethod]
    public void UnrelatedTooltipIsNotAnAttachment()
    {
        var match = CodexComposerAttachmentDetector.TryClassify(
            new CodexComposerAttachmentNodeMetadata(
                "ControlType.ToolTip",
                string.Empty,
                "Helpful tip",
                "ToolTip",
                "Chromium",
                "Pane",
                new UiAutomationBounds(10, 600, 200, 40),
                new UiAutomationBounds(0, 500, 600, 300),
                IsOffscreen: false,
                IsWithinComposerScope: true,
                ChildCount: 0,
                HasInvokePattern: false,
                LocalizedControlType: "tool tip",
                HelpText: string.Empty,
                ItemStatus: string.Empty,
                ItemType: string.Empty));

        Assert.IsNull(match);
    }

    [TestMethod]
    public void NewAttachmentCardIsAcceptedButExistingCardIsNotNew()
    {
        var before = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runtime:existing-card"] = "structure"
            });
        var unchanged = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runtime:existing-card"] = "structure"
            });
        var afterPaste = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runtime:existing-card"] = "structure",
                ["runtime:new-card-action"] = "action"
            });

        Assert.IsFalse(unchanged.HasNewAttachmentsSince(before));
        Assert.IsTrue(afterPaste.HasNewAttachmentsSince(before));
        Assert.IsTrue(afterPaste.HasNewReferencesSince(before));
        Assert.AreEqual("action", afterPaste.GetNewDetectionKindSince(before));
    }

    [TestMethod]
    public void NoNewAttachmentOrReferenceDoesNotAcceptFailedInlinePaste()
    {
        var before = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(["runtime:existing"], StringComparer.Ordinal));
        var after = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(["runtime:existing"], StringComparer.Ordinal));

        Assert.IsFalse(after.HasNewReferencesSince(before));
        Assert.AreEqual("none", after.GetNewDetectionKindSince(before));
    }

    [TestMethod]
    public void ReferenceSnapshotOnlyAcceptsNewReferenceNodes()
    {
        var before = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(["runtime:old"], StringComparer.Ordinal));
        var after = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(["runtime:old", "runtime:new"], StringComparer.Ordinal));
        var unchanged = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(["runtime:old"], StringComparer.Ordinal));

        Assert.IsTrue(after.HasNewReferencesSince(before));
        Assert.IsFalse(unchanged.HasNewReferencesSince(before));
    }

    [TestMethod]
    public void ReferenceSnapshotCannotAcceptWhenBeforeSnapshotWasUnavailable()
    {
        var unavailable = new CodexComposerReferenceSnapshot(
            false,
            new HashSet<string>(StringComparer.Ordinal));
        var after = new CodexComposerReferenceSnapshot(
            true,
            new HashSet<string>(["runtime:new"], StringComparer.Ordinal));

        Assert.IsFalse(after.HasNewReferencesSince(unavailable));
    }

    [TestMethod]
    public void ReplaceDialogHasExactlyTwoVisibleActions()
    {
        var buttons = AskDialogButtonConfiguration.ReplaceOrCancel("替换", "取消");

        Assert.AreEqual(2, buttons.VisibleActionButtonCount);
        Assert.AreEqual("替换", buttons.PrimaryButtonText);
        Assert.IsNull(buttons.SecondaryButtonText);
        Assert.IsFalse(buttons.IsSecondaryButtonEnabled);
        Assert.AreEqual("取消", buttons.CloseButtonText);
        Assert.IsTrue(buttons.IsCloseButtonEnabled);
        Assert.IsFalse(buttons.CloseButtonText.Equals("Close", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReplaceDialogOnlyPrimaryResultIsAccepted()
    {
        Assert.IsTrue(AskDialogButtonConfiguration.IsAccepted(WpfUiMessageBoxResult.Primary));
        Assert.IsFalse(AskDialogButtonConfiguration.IsAccepted(WpfUiMessageBoxResult.Secondary));
        Assert.IsFalse(AskDialogButtonConfiguration.IsAccepted(WpfUiMessageBoxResult.None));
    }

    [TestMethod]
    public void FocusedChromeNodeCanResolveCodexWindowByPidWithoutItsOwnHwnd()
    {
        var candidates = new[]
        {
            new CodexDesktopWindowCandidate(
                23280,
                new IntPtr(0x1234),
                "Blue project",
                "Chrome_WidgetWin_1",
                "Codex",
                @"C:\Program Files\Codex\Codex.exe",
                IsForeground: true)
        };

        Assert.IsTrue(CodexDesktopWindowOwnership.TrySelectForProcess(23280, candidates, out var selected));
        Assert.AreEqual(new IntPtr(0x1234), selected!.Handle);
    }

    [TestMethod]
    public void FocusedPidMismatchIsRejected()
    {
        var candidates = new[]
        {
            new CodexDesktopWindowCandidate(
                23281,
                new IntPtr(0x1234),
                "Codex",
                "Chrome_WidgetWin_1",
                "Codex",
                @"C:\Program Files\Codex\Codex.exe")
        };

        Assert.IsFalse(CodexDesktopWindowOwnership.TrySelectForProcess(23280, candidates, out _));
    }

    [TestMethod]
    public void MultipleCodexWindowsRequireSafeScoringResolution()
    {
        var ambiguous = new[]
        {
            new CodexDesktopWindowCandidate(23280, new IntPtr(0x1234), "Codex", "Chrome_WidgetWin_1", "Codex", @"C:\Codex.exe"),
            new CodexDesktopWindowCandidate(23280, new IntPtr(0x5678), "Codex", "Chrome_WidgetWin_1", "Codex", @"C:\Codex.exe")
        };
        var foreground = ambiguous[1] with { IsForeground = true };

        Assert.IsFalse(CodexDesktopWindowOwnership.TrySelectForProcess(23280, ambiguous, out _));
        Assert.IsTrue(CodexDesktopWindowOwnership.TrySelectForProcess(23280, [ambiguous[0], foreground], out var selected));
        Assert.AreEqual(foreground.Handle, selected!.Handle);
    }

    [TestMethod]
    public async Task SlowComposerWorkerTimesOutWithoutBlockingCaller()
    {
        var coordinator = CreateCoordinator(TimeSpan.FromMilliseconds(60));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await coordinator.RunAsync(() =>
        {
            Thread.Sleep(300);
            return CodexComposerInjectionResult.Filled("filled");
        });

        Assert.IsFalse(result.Success);
        Assert.AreEqual("codex_composer_probe_timeout", result.Code);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
        Thread.Sleep(350);
    }

    [TestMethod]
    public async Task ConcurrentComposerOperationsAreRejectedWhileWorkerIsActive()
    {
        using var workerStarted = new ManualResetEventSlim(false);
        using var releaseWorker = new ManualResetEventSlim(false);
        var coordinator = CreateCoordinator(TimeSpan.FromSeconds(2));

        var first = coordinator.RunAsync(() =>
        {
            workerStarted.Set();
            releaseWorker.Wait(TimeSpan.FromSeconds(1));
            return CodexComposerInjectionResult.Filled("first");
        });

        Assert.IsTrue(workerStarted.Wait(TimeSpan.FromSeconds(1)));
        var second = await coordinator.RunAsync(() => CodexComposerInjectionResult.Filled("second"));

        Assert.IsFalse(second.Success);
        Assert.AreEqual("codex_composer_busy", second.Code);
        releaseWorker.Set();
        var firstResult = await first;
        Assert.IsTrue(firstResult.Success);
    }

    [TestMethod]
    public async Task WorkerExceptionRestoresOperationGate()
    {
        var coordinator = CreateCoordinator(TimeSpan.FromSeconds(1));

        var failed = await coordinator.RunAsync(() => throw new InvalidOperationException("fake probe failure"));
        var recovered = await coordinator.RunAsync(() => CodexComposerInjectionResult.Filled("recovered"));

        Assert.IsFalse(failed.Success);
        Assert.AreEqual("codex_composer_injection_failed", failed.Code);
        Assert.IsTrue(recovered.Success);
    }

    [TestMethod]
    public async Task CancellationReturnsWithoutReleasingGateUntilWorkerCompletes()
    {
        using var workerStarted = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();
        var coordinator = CreateCoordinator(TimeSpan.FromSeconds(2));

        var pending = coordinator.RunAsync(() =>
        {
            workerStarted.Set();
            Thread.Sleep(250);
            return CodexComposerInjectionResult.Filled("late");
        }, cancellation.Token);

        Assert.IsTrue(workerStarted.Wait(TimeSpan.FromSeconds(1)));
        cancellation.Cancel();
        var cancelled = await pending;

        Assert.IsFalse(cancelled.Success);
        Assert.AreEqual("codex_composer_cancelled", cancelled.Code);
        var busyWhileWorkerFinishes = await coordinator.RunAsync(() => CodexComposerInjectionResult.Filled("busy"));
        Assert.AreEqual("codex_composer_busy", busyWhileWorkerFinishes.Code);

        Thread.Sleep(300);
        var recovered = await coordinator.RunAsync(() => CodexComposerInjectionResult.Filled("recovered"));
        Assert.IsTrue(recovered.Success);
    }

    [TestMethod]
    public async Task StaWorkerUsesIndependentStaThread()
    {
        var apartmentState = await StaAutomationWorker.RunAsync(
            () => Thread.CurrentThread.GetApartmentState());

        Assert.AreEqual(ApartmentState.STA, apartmentState);
    }

    [TestMethod]
    public void FocusedProbeDisplayContainsStructureButNoValuePayload()
    {
        var metadata = new FocusedComposerElementMetadata(
            42,
            new IntPtr(100),
            "Document",
            "document",
            "message-editor",
            "Chrome_RenderWidgetHostHWND",
            "随心输入",
            "Chromium",
            true,
            true,
            true,
            false,
            new UiAutomationBounds(10, 700, 600, 80),
            ["ValuePattern", "TextPattern", "TextPattern2"],
            false);
        var result = new FocusedComposerProbeResult(
            true,
            "focused_codex_element",
            "Focused element belongs to Codex Desktop.",
            metadata,
            [],
            new FocusedComposerWindowMetadata(42, new IntPtr(100), "Codex", "Chrome_WidgetWin_1", "Codex", true),
            TimeSpan.FromMilliseconds(12));

        var display = result.ToDisplayText();

        StringAssert.Contains(display, "ControlType=Document");
        StringAssert.Contains(display, "FrameworkId=Chromium");
        StringAssert.Contains(display, "Patterns=[ValuePattern, TextPattern, TextPattern2]");
        StringAssert.Contains(display, "Name=随心输入");
        StringAssert.Contains(display, "ValuePatternIsReadOnly=False");
        Assert.IsFalse(display.Contains("Value=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void FocusedProbeParentDepthIsExplicitlyBounded()
    {
        Assert.AreEqual(10, FocusedComposerProbeService.MaxParentDepth);
    }

    [TestMethod]
    public async Task SlowFocusedProbeTimesOutWithoutBlockingCaller()
    {
        var service = new FocusedComposerProbeService(
            TimeSpan.FromMilliseconds(60),
            _ => Task.Run(() =>
            {
                Thread.Sleep(300);
                return FocusedComposerProbeResult.Failed(
                    "fake_completed_late",
                    "late",
                    TimeSpan.FromMilliseconds(300));
            }));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await service.ProbeAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual("focused_probe_timeout", result.Code);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
        Thread.Sleep(300);
    }

    private static CodexComposerOperationCoordinator CreateCoordinator(TimeSpan timeout)
    {
        return new CodexComposerOperationCoordinator(
            timeout,
            operation => Task.Run(operation));
    }

    private static TraversalNode CreateFullBinaryTree(int depth)
    {
        var node = new TraversalNode();
        if (depth <= 0)
        {
            return node;
        }

        node.Children.Add(CreateFullBinaryTree(depth - 1));
        node.Children.Add(CreateFullBinaryTree(depth - 1));
        return node;
    }

    private sealed class TraversalNode
    {
        public List<TraversalNode> Children { get; } = [];

        public bool IsEdit { get; set; }

        public bool IsProseMirror { get; set; }

        public bool IsCandidate { get; set; }
    }

    private sealed class FakeAutomationTarget
    {
    }

    private static CodexComposerCandidate Candidate(
        long handle,
        string controlType,
        string automationId,
        string name,
        bool supportsValuePattern,
        bool isOpenAiWindow = true,
        bool isReadOnly = false,
        string parentHierarchy = "Pane#conversation > Document#message-editor",
        bool supportsTextPattern = false,
        string className = "",
        string frameworkId = "Chrome",
        bool isEnabled = true,
        bool isOffscreen = false)
    {
        var bounds = new UiAutomationBounds(10, 700, 600, 80);
        var metadata = new UiAutomationMetadata(
            new IntPtr(handle),
            42,
            "Codex",
            controlType,
            automationId,
            name,
            className,
            frameworkId,
            isEnabled,
            true,
            isOffscreen,
            bounds,
            new UiAutomationBounds(0, 0, 800, 800),
            parentHierarchy,
            true,
            false);
        return new CodexComposerCandidate(
            metadata,
            isOpenAiWindow,
            supportsValuePattern,
            isReadOnly,
            0,
            supportsTextPattern);
    }
}
