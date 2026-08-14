namespace BlueRelay.Services;

public sealed record GitRepositoryInfo(
    bool IsGitRepository,
    string SelectedPath,
    string? RepositoryRoot,
    string SuggestedName,
    string? OriginUrl,
    bool GitAvailable);
