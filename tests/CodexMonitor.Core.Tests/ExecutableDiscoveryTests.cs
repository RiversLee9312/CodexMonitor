using System.Runtime.InteropServices;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ExecutableDiscoveryTests
{
    private static string FileAt(string root, params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic native binary"); return path;
    }
    [TestMethod]
    public void DesktopVersionDirectoriesAreFoundWithoutProcessPathAndUpdatesAreNotPinned()
    {
        var root = Directory.CreateTempSubdirectory("CodexMonitor-cli-").FullName;
        try
        {
            var discovery = new CodexExecutableDiscovery(() => [], () => [root]);
            var older = FileAt(root, "older-hash", "codex.exe"); Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(older)!, DateTime.UtcNow.AddDays(-1));
            Assert.AreEqual(older, discovery.Resolve("codex"));
            var latest = FileAt(root, "new-hash", "codex.exe");
            Directory.CreateDirectory(Path.Combine(root, "incomplete-update"));
            Assert.AreEqual(latest, discovery.Resolve("codex")); Assert.AreEqual(latest, discovery.Resolve("codex.exe"));
            Assert.Throws<FileNotFoundException>(() => discovery.Resolve(Path.Combine(root, "explicit-missing.exe")), "An explicit user path must not silently select another installation");
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void QuotedSearchDirectoriesEmptyEntriesAndExeNamesAreHandledWithoutCurrentDirectorySearch()
    {
        var root = Directory.CreateTempSubdirectory("CodexMonitor-cli-").FullName;
        try
        {
            var native = FileAt(root, "with space", "codex.exe");
            var discovery = new CodexExecutableDiscovery(() => ["", " ", "relative", "\"" + Path.GetDirectoryName(native) + "\""], () => []);
            Assert.AreEqual(native, discovery.Resolve("codex.exe")); Assert.AreEqual(native, discovery.Resolve("codex"));
            Assert.AreEqual(native, discovery.Resolve("\"" + native + "\""));
            Assert.Throws<FileNotFoundException>(() => new CodexExecutableDiscovery(() => ["", "relative"], () => []).Resolve("codex"));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod, DataRow(false), DataRow(true), DataRow(null)]
    public void NpmHoistedNestedAndLegacyPackagesResolveWithoutExecutingAShell(bool? nested)
    {
        var root = Directory.CreateTempSubdirectory("CodexMonitor-cli-").FullName;
        try
        {
            var shim = FileAt(root, "codex.cmd");
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var package = arm ? "codex-win32-arm64" : "codex-win32-x64"; var target = arm ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
            var packageRoot = nested is null ? Path.Combine("node_modules", "@openai", "codex") : nested.Value ? Path.Combine("node_modules", "@openai", "codex", "node_modules", "@openai", package) : Path.Combine("node_modules", "@openai", package);
            var native = FileAt(root, packageRoot, "vendor", target, "codex", "codex.exe");
            var discovery = new CodexExecutableDiscovery(() => [root], () => []);
            Assert.AreEqual(native, discovery.Resolve("codex")); Assert.AreEqual(native, discovery.Resolve(shim));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void KnownDirectoriesIncludeDesktopAndNpmEvenWhenExplorerHasAStalePath()
    {
        CollectionAssert.Contains(CodexExecutableDiscovery.InstallationDirectories().ToArray(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin"));
        CollectionAssert.Contains(CodexExecutableDiscovery.SearchDirectories().ToArray(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            foreach (var path in (Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(Path.PathSeparator))
                CollectionAssert.Contains(CodexExecutableDiscovery.SearchDirectories().ToArray(), path);
    }
}
