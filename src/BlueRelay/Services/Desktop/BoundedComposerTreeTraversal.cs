using System.Diagnostics;

namespace BlueRelay.Services.Desktop;

public sealed record ComposerTraversalLimits(int MaxNodes, int MaxDepth, int MaxSiblings);

public sealed record ComposerTraversalStatistics(
    int VisitedNodes,
    int MaxDepthReached,
    int EditControlsSeen,
    int ProseMirrorSeen,
    bool HitNodeLimit,
    bool HitDepthLimit,
    bool HitBudget);

public sealed record ComposerTraversalResult<TCandidate>(
    IReadOnlyList<TCandidate> Candidates,
    ComposerTraversalStatistics Statistics,
    bool FoundHighConfidenceCandidate);

public static class BoundedComposerTreeTraversal
{
    public static ComposerTraversalResult<TCandidate> Search<TNode, TCandidate>(
        IReadOnlyList<TNode> roots,
        ComposerTraversalLimits limits,
        Stopwatch budgetStopwatch,
        TimeSpan totalBudget,
        Func<TNode, IReadOnlyList<TNode>> getChildren,
        Func<TNode, bool> isEditControl,
        Func<TNode, bool> isFallbackControl,
        Func<TNode, bool> hasProseMirror,
        Func<TNode, TCandidate?> tryCreateCandidate,
        Func<TCandidate, bool> isHighConfidence,
        CancellationToken cancellationToken = default)
        where TCandidate : class
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(budgetStopwatch);
        ArgumentNullException.ThrowIfNull(getChildren);
        ArgumentNullException.ThrowIfNull(isEditControl);
        ArgumentNullException.ThrowIfNull(isFallbackControl);
        ArgumentNullException.ThrowIfNull(hasProseMirror);
        ArgumentNullException.ThrowIfNull(tryCreateCandidate);
        ArgumentNullException.ThrowIfNull(isHighConfidence);

        var pending = new Stack<(TNode Node, int Depth)>();
        var rootCount = Math.Min(roots.Count, limits.MaxSiblings);
        for (var index = rootCount - 1; index >= 0; index--)
        {
            pending.Push((roots[index], 1));
        }

        var candidates = new List<TCandidate>();
        var visitedNodes = 0;
        var maxDepthReached = 0;
        var editControlsSeen = 0;
        var proseMirrorSeen = 0;
        var hitNodeLimit = false;
        var hitDepthLimit = false;
        var hitBudget = false;
        var foundHighConfidenceCandidate = false;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (visitedNodes >= limits.MaxNodes)
            {
                hitNodeLimit = true;
                break;
            }

            if (budgetStopwatch.Elapsed > totalBudget)
            {
                hitBudget = true;
                break;
            }

            var (node, depth) = pending.Pop();
            visitedNodes++;
            maxDepthReached = Math.Max(maxDepthReached, depth);

            var isEdit = isEditControl(node);
            if (isEdit)
            {
                editControlsSeen++;
            }

            if (hasProseMirror(node))
            {
                proseMirrorSeen++;
            }

            if (isEdit || isFallbackControl(node))
            {
                var candidate = tryCreateCandidate(node);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                    if (isHighConfidence(candidate))
                    {
                        foundHighConfidenceCandidate = true;
                        break;
                    }
                }
            }

            if (depth >= limits.MaxDepth)
            {
                hitDepthLimit = true;
                continue;
            }

            var children = getChildren(node);
            var childCount = Math.Min(children.Count, limits.MaxSiblings);
            for (var index = childCount - 1; index >= 0; index--)
            {
                pending.Push((children[index], depth + 1));
            }
        }

        return new ComposerTraversalResult<TCandidate>(
            candidates,
            new ComposerTraversalStatistics(
                visitedNodes,
                maxDepthReached,
                editControlsSeen,
                proseMirrorSeen,
                hitNodeLimit,
                hitDepthLimit,
                hitBudget),
            foundHighConfidenceCandidate);
    }
}
