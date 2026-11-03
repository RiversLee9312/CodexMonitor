using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class SetupTests
{
    [TestMethod]
    public void SettingsSavedWithTheRetiredActivitySwitchKeepTheirOtherPreferences()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"CodexHome":"D:/history","Theme":"Dark","Language":"de-DE","ShowTools":false}""")!;
        Assert.AreEqual("D:/history", settings.CodexHome); Assert.AreEqual("Dark", settings.Theme); Assert.AreEqual("de-DE", settings.Language);
    }
    [TestMethod]
    public async Task HomeDiscoveryChecksKnownPathsDeduplicatesAndDoesNotReadCredentialFiles()
    {
        var root = Directory.CreateTempSubdirectory("CodexMonitor-discovery-").FullName;
        // An empty explicitly configured home is valid before the CLI has written any state.
        var other = Directory.CreateDirectory(Path.Combine(root, "custom")).FullName;
        try
        {
            var discovery = new CodexHomeDiscovery(_ => [root, root + Path.DirectorySeparatorChar, other, Path.Combine(root, "missing"), "relative", null]);
            var found = await discovery.FindAsync(root);
            CollectionAssert.AreEqual(new[] { root, other }, found.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task MissingHomesReturnEmptyWithoutCreatingFolders()
    {
        var missing = Path.Combine(Path.GetTempPath(), "CodexMonitor-missing-" + Guid.NewGuid());
        Assert.HasCount(0, await new CodexHomeDiscovery(_ => [missing]).FindAsync(missing));
        Assert.IsFalse(Directory.Exists(missing));
        Assert.HasCount(0, await new LocalSessionStore(missing).ListAsync());
    }
    [TestMethod]
    public async Task HomeDiscoveryHonorsCancellation()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new CodexHomeDiscovery(_ => []).FindAsync("", cancel.Token));
    }
    [TestMethod]
    public void HomeCandidatesIncludeConfiguredAndDefaultLocations()
    {
        var custom = Path.Combine(Path.GetTempPath(), "custom-codex");
        var candidates = CodexHomeDiscovery.CandidatePaths(custom).ToArray();
        CollectionAssert.Contains(candidates, custom);
        CollectionAssert.Contains(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
    }
    [TestMethod]
    public void HomePathValidationAllowsMissingAbsolutePathsAndRejectsInvalidPaths()
    {
        Assert.IsTrue(CodexHomeDiscovery.IsValidPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
        Assert.IsFalse(CodexHomeDiscovery.IsValidPath("relative"));
        Assert.IsFalse(CodexHomeDiscovery.IsValidPath("C:\\bad\0path"));
    }
    [TestMethod]
    public void MissingExecutableHasAnExplicitErrorAndDoesNotCreateHome()
    {
        var missing = Path.Combine(Path.GetTempPath(), "CodexMonitor-missing-" + Guid.NewGuid());
        Assert.Throws<FileNotFoundException>(() => StdioTransport.Start(Path.Combine(missing, "codex.exe"), missing));
        Assert.IsFalse(Directory.Exists(missing));
    }
}
