namespace BlueRelay.Services;

public interface IGitRepositoryDetector
{
    Task<GitRepositoryInfo> DetectAsync(string selectedPath, CancellationToken cancellationToken = default);
}
