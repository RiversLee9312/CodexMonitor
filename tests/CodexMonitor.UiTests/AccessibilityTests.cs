using System.Diagnostics;
using System.Text.Json;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class AccessibilityTests
{
    public TestContext TestContext { get; set; } = null!;
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "de-DE")]
    [DataRow("Dark", "ar-SA")]
    public async Task AxeWindowsFindsNoAccessibilityErrorsInTheProductionView(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, theme, language); await window.ShowActivityReviewAsync(true);
        var directory = Path.GetFullPath(Path.Combine(MainWindow.ScreenshotDirectory, "..", "accessibility")); Directory.CreateDirectory(directory);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "CodexMonitor.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var scanId = $"view-{theme}-{language}";
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "--project", Path.Combine(repo.FullName, "tests", "CodexMonitor.Accessibility"), "--", Environment.ProcessId.ToString(), handle.ToInt64().ToString(), directory, scanId }) start.ArgumentList.Add(arg);
        using var scanner = Process.Start(start)!;
        var stdout = scanner.StandardOutput.ReadToEndAsync(); var stderr = scanner.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await scanner.WaitForExitAsync(timeout.Token);
        var scanFile = Path.Combine(directory, scanId + ".json");
        Assert.IsTrue(File.Exists(scanFile), await stdout + await stderr); TestContext.AddResultFile(scanFile);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(scanFile));
        Assert.IsTrue(result.RootElement.GetArrayLength() > 0, "The test must actually inspect the native window");
        foreach (var scan in result.RootElement.EnumerateArray())
        {
            var file = scan.GetProperty("File").GetString(); if (file is not null && File.Exists(file)) TestContext.AddResultFile(file);
            Assert.AreEqual(0, scan.GetProperty("ErrorCount").GetInt32(), scan.GetProperty("Errors").GetRawText());
        }
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN", false)]
    [DataRow("Dark", "de-DE", false)]
    [DataRow("Dark", "ar-SA", true)]
    public async Task LargeTextReflowsSessionActionsAndStructuredDetails(string theme, string language, bool details)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 750, theme, language);
        if (details) await window.ShowStructuredActivityReviewAsync("""{"id":"m","type":"mcpToolCall","server":"github","tool":"search","arguments":{"query":"WinUI accessibility","path":"src/App.xaml"},"result":{"content":[{"type":"text","text":"Found accessible controls"}]},"status":"completed"}""");
        await window.ScaleReviewTextAsync(2.25);
        foreach (var (name, rect) in window.ReviewBounds())
            Assert.IsTrue(rect.Left >= -1 && rect.Top >= -1 && rect.Right <= window.ReviewRoot.ActualWidth + 1 && rect.Bottom <= window.ReviewRoot.ActualHeight + 1, name + ": " + rect);
        Assert.IsTrue(window.ReviewBounds()["messages"].Height >= 80, "Large text must leave a usable reading viewport");
        TestContext.AddResultFile(await window.CaptureReviewAsync($"text-225-{theme}-{language}"));
    }
    [UITestMethod]
    public async Task InteractiveSurfacesHaveNamesAndFindReturnsKeyboardFocus()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN"); await window.ShowActivityReviewAsync(true);
        foreach (var button in window.ReviewVisualElements<Button>().Where(b => b.IsEnabled && b.IsLoaded && b.Visibility == Visibility.Visible && b.ActualWidth > 0 && b.ActualHeight > 0))
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button); Assert.IsNotNull(peer);
            Assert.IsFalse(string.IsNullOrWhiteSpace(peer.GetName()), button.GetType().Name + " needs an accessible name");
            Assert.IsNotNull(peer.GetPattern(PatternInterface.Invoke)); Assert.IsTrue(button.UseSystemFocusVisuals);
        }
        window.ToggleFindReview(true); Assert.IsTrue(window.ReviewSearchHasFocus);
        window.ToggleFindReview(false); Assert.IsTrue(window.ReviewFindHasFocus);
        await MainWindow.SettleAsync();
        Assert.IsTrue(window.ReviewVisualElements<FontIcon>().All(i => !i.IsTextScaleFactorEnabled), "Decorative font icons must not grow with system text size: " +
            string.Join(", ", window.ReviewVisualElements<FontIcon>().Where(i => i.IsTextScaleFactorEnabled).Select(i => i.Name + ":" + i.Glyph + ":" + Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(i)?.GetType().Name)));
    }
    [UITestMethod]
    public void DisclosuresExposeTheirStateAndRejectActionsWhenDisabled()
    {
        var disclosure = new ActivityDisclosure(); AutomationProperties.SetName(disclosure, "工具详情");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(disclosure)!;
        var pattern = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
        pattern.Expand(); Assert.AreEqual(ExpandCollapseState.Expanded, pattern.ExpandCollapseState);
        disclosure.IsEnabled = false; Assert.ThrowsExactly<ElementNotEnabledException>(() => pattern.Collapse());
    }
    [UITestMethod]
    public void AdaptedActivityNamesNeverReadRawArgumentsInsteadOfTheirPurpose()
    {
        var text = new LocalizationService("zh-CN");
        var row = new EntryRow(new("m", EntryKind.Tool, "{\"internal\":\"PRIVATE_RAW\"}", default, Tool: new("lookup", "{\"internal\":\"PRIVATE_RAW\"}", State: ToolActivityState.Completed, SummaryOverride: "github · lookup", CategoryOverride: "ToolMcp")), text);
        StringAssert.Contains(row.AccessibleName, "MCP"); StringAssert.Contains(row.AccessibleName, "已完成"); Assert.IsFalse(row.AccessibleName.Contains("PRIVATE_RAW"));
    }
}
