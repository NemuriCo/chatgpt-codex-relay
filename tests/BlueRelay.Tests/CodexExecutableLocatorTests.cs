using BlueRelay.Services.Codex;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexExecutableLocatorTests
{
    [TestMethod]
    public async Task MissingConfiguredExecutableIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "BlueRelay-missing-codex.exe");
        var result = await new CodexExecutableLocator().LocateAsync(path);

        Assert.IsFalse(result.Found);
        StringAssert.Contains(result.Error, "file does not exist");
    }

    [TestMethod]
    public async Task ExistingNonCodexExecutableIsRejected()
    {
        var path = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var result = await new CodexExecutableLocator().LocateAsync(path);

        Assert.IsFalse(result.Found);
        StringAssert.Contains(result.Error, "app-server --help failed");
    }

    [TestMethod]
    public async Task RealCodexExecutableCanBeValidatedWhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BLUERELAY_RUN_CODEX_SMOKE"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BLUERELAY_RUN_CODEX_SMOKE=1 to run the real Codex executable validation.");
        }

        var path = Environment.GetEnvironmentVariable("BLUERELAY_CODEX_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Inconclusive("Set BLUERELAY_CODEX_PATH to the real Codex executable.");
        }

        var result = await new CodexExecutableLocator().LocateAsync(path);

        Assert.IsTrue(result.Found, result.Error);
        Assert.IsTrue(result.Version?.Contains("codex", StringComparison.OrdinalIgnoreCase) == true);
        Assert.IsTrue(result.AppServerHelp?.Contains("app-server", StringComparison.OrdinalIgnoreCase) == true);
    }
}
