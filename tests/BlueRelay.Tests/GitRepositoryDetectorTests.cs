using BlueRelay.Services;

namespace BlueRelay.Tests;

[TestClass]
public sealed class GitRepositoryDetectorTests
{
    private string _testDirectory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "BlueRelayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("https://github.com/NemuriCo/chatgpt-codex-relay.git")]
    [DataRow("git@github.com:NemuriCo/chatgpt-codex-relay.git")]
    public void RepositoryNameIsExtractedFromHttpsAndSshRemotes(string remote)
    {
        Assert.AreEqual("chatgpt-codex-relay", GitRepositoryDetector.ExtractRepositoryName(remote));
    }

    [TestMethod]
    public async Task NonGitDirectoryFallsBackToSelectedFolderName()
    {
        var selectedDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "Tool With Spaces")).FullName;
        var detector = new GitRepositoryDetector(gitExecutable: "git");

        var result = await detector.DetectAsync(selectedDirectory);

        Assert.IsFalse(result.IsGitRepository);
        Assert.AreEqual(selectedDirectory, result.SelectedPath);
        Assert.AreEqual("Tool With Spaces", result.SuggestedName);
    }

    [TestMethod]
    public async Task MissingGitExecutableDoesNotFailDetection()
    {
        var selectedDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "NoGit")).FullName;
        var detector = new GitRepositoryDetector(gitExecutable: "blue-relay-git-that-does-not-exist");

        var result = await detector.DetectAsync(selectedDirectory);

        Assert.IsFalse(result.IsGitRepository);
        Assert.IsFalse(result.GitAvailable);
        Assert.AreEqual("NoGit", result.SuggestedName);
    }

    [TestMethod]
    public async Task InvalidPathUsesSafeFallback()
    {
        var selectedPath = Path.Combine(_testDirectory, "missing", "Folder With Spaces");
        var detector = new GitRepositoryDetector(gitExecutable: "blue-relay-git-that-does-not-exist");

        var result = await detector.DetectAsync(selectedPath);

        Assert.IsFalse(result.IsGitRepository);
        StringAssert.Contains(result.SuggestedName, "Folder With Spaces");
    }
}
