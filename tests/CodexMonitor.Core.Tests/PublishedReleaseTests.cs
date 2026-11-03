using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class PublishedReleaseTests
{
    [TestMethod]
    [TestCategory("Release")]
    public async Task PublishedApplicationCanLoadXamlAndOpenItsWindow()
    {
        var directory = Environment.GetEnvironmentVariable("CODEX_MONITOR_RELEASE_ROOT");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(directory)) Assert.Inconclusive("Use scripts/Test.ps1 -IncludeRelease after publishing on Windows.");
        var start = new ProcessStartInfo(Path.Combine(directory, "CodexMonitor.App.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        using var process = Process.Start(start)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                process.Refresh();
                Assert.IsFalse(process.HasExited, "The published executable exited before opening its window. Check WinUI PRI/XBF publishing.");
                if (process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle == "Codex Monitor") return;
            }
            Assert.Fail("The published executable did not activate its window. Check the published XAML resources and crash log.");
        }
        finally
        {
            process.Refresh();
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
            }
        }
    }
}
