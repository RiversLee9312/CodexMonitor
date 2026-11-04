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
    [DataRow("Light", "zh-CN", false, false, false, false)]
    [DataRow("Dark", "de-DE", false, false, false, false)]
    [DataRow("Dark", "ar-SA", false, false, false, false)]
    [DataRow("Dark", "zh-CN", true, false, false, false)]
    [DataRow("Light", "zh-CN", false, true, false, false)]
    [DataRow("Light", "zh-CN", false, false, true, false)]
    [DataRow("Dark", "ar-SA", false, false, true, false)]
    [DataRow("Light", "zh-CN", false, false, false, true)]
    [DataRow("Dark", "de-DE", false, false, false, true)]
    [DataRow("Dark", "ar-SA", false, false, false, true)]
    public async Task AxeWindowsFindsNoAccessibilityErrorsInTheProductionView(string theme, string language, bool licenses, bool serverStatus, bool question, bool dates)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, theme, language); await window.ShowActivityReviewAsync(true);
        ContentDialog? licenseDialog = null;
        Windows.Foundation.IAsyncOperation<ContentDialogResult>? licenseClosing = null;
        ContentDialog? statusDialog = null; Task? statusClosing = null;
        if (licenses)
        {
            licenseDialog = new ContentDialog { Title = new LocalizationService(language)["OpenSourceLicenses"], Content = new LicensePanel(new(language)), CloseButtonText = new LocalizationService(language)["Close"], XamlRoot = window.ReviewRoot.XamlRoot, RequestedTheme = window.ReviewTheme };
            licenseClosing = licenseDialog.ShowAsync(); await MainWindow.SettleAsync();
        }
        if (serverStatus)
        {
            await window.ConnectReviewServerAsync();
            using var account = JsonDocument.Parse("""{"authMode":"chatgpt","planType":"pro"}""");
            window.NotifyTranscriptReview(new("account/updated", account.RootElement.Clone()));
            (statusDialog, statusClosing) = await window.OpenProtocolDetailsReviewAsync();
            foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(window.ReviewRoot.XamlRoot)) Expand(popup.Child);
            await MainWindow.SettleAsync();
        }
        if (question)
        {
            using var q = JsonDocument.Parse("""{"threadId":"review-session","questions":[{"id":"mode","header":"Mode","question":"Choose how to continue","isOther":true,"options":[{"label":"Review first","description":"Keep changes ready for review."}]},{"id":"value","header":"Private value","question":"Enter a private value","isSecret":true}]}""");
            var panel = new UserInputPanel(UserInputRequest.Parse(q.RootElement), new(language), "Example conversation");
            licenseDialog = new ContentDialog { Title = new LocalizationService(language)["UserInputTitle"], Content = new ScrollViewer { Content = panel, MaxHeight = 400 }, PrimaryButtonText = new LocalizationService(language)["SubmitAnswers"], CloseButtonText = new LocalizationService(language)["Cancel"], IsPrimaryButtonEnabled = false, XamlRoot = window.ReviewRoot.XamlRoot, RequestedTheme = window.ReviewTheme, FlowDirection = window.ReviewRoot.FlowDirection, Language = language };
            licenseClosing = licenseDialog.ShowAsync(); await MainWindow.SettleAsync();
        }
        FrameworkElement? dateSurface = null;
        if (dates)
        {
            var panel = await window.ReviewOpenDateFilterAsync();
            panel.Children.OfType<ComboBox>().Last().SelectedIndex = 4;
            await MainWindow.SettleAsync(); dateSurface = panel;
            while (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(dateSurface) is FrameworkElement parent) { dateSurface = parent; if (dateSurface is FlyoutPresenter) break; }
        }
        try
        {
            var directory = Path.GetFullPath(Path.Combine(MainWindow.ScreenshotDirectory, "..", "accessibility")); Directory.CreateDirectory(directory);
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "CodexMonitor.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            var scanId = $"view-{theme}-{language}" + (dates ? "-dates" : question ? "-question" : licenses ? "-licenses" : serverStatus ? "-server-status" : "");
            // Flyouts are separate native popup surfaces; RenderTargetBitmap cannot capture their presenter.
            // Axe.Windows stores native screenshots for all scanned popup roots in its .a11ytest files.
            if (!dates) TestContext.AddResultFile(await window.CaptureReviewAsync(scanId, statusDialog ?? licenseDialog));
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "run", "--project", Path.Combine(repo.FullName, "tests", "CodexMonitor.Accessibility"), "--", Environment.ProcessId.ToString(), handle.ToInt64().ToString(), directory, scanId }) start.ArgumentList.Add(arg);
            using var scanner = Process.Start(start)!;
            var stdout = scanner.StandardOutput.ReadToEndAsync(); var stderr = scanner.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await scanner.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, scanner.ExitCode, await stdout + await stderr);
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
        finally
        {
            if (dates) window.ReviewCloseDateFilter();
            if (licenseDialog is not null) { licenseDialog.Hide(); await licenseClosing!; }
            if (statusDialog is not null) { statusDialog.Hide(); await statusClosing!; }
        }
        static void Expand(DependencyObject parent)
        {
            if (parent is Expander section) section.IsExpanded = true;
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++) Expand(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i));
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
