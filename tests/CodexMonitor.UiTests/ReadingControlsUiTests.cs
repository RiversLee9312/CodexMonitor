using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class ReadingControlsUiTests
{
    public TestContext TestContext { get; set; } = null!;
    [UITestMethod]
    [DataRow("Light", "zh-CN", true)]
    [DataRow("Dark", "de-DE", false)]
    [DataRow("Dark", "ar-SA", true)]
    public async Task SummaryIsReadableAndFullThinkingStartsCollapsed(string theme, string language, bool summary)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        await window.ReviewReasoning(summary ? "Public summary stays readable." : "", "Detailed thinking is folded by default.");
        var toggle = window.ReviewVisualElements<ActivityDisclosure>().Single(); Assert.IsFalse(toggle.IsExpanded);
        var text = string.Join(" ", window.ReviewVisualElements<TextBlock>().Select(t => t.Text));
        Assert.IsFalse(text.Contains("Detailed thinking"));
        Assert.AreEqual(summary, window.ReviewReasoningHasInlineSummary);
        toggle.SetExpanded(true); await MainWindow.SettleAsync(); Assert.IsTrue(window.ReviewVisualElements<RichTextBlock>().Any());
        TestContext.AddResultFile(await window.CaptureReviewAsync($"thinking-{theme}-{language}"));
    }
    [UITestMethod]
    [DataRow("Dark", "zh-CN")]
    [DataRow("Light", "de-DE")]
    [DataRow("Dark", "ar-SA")]
    public async Task LatestFloatsWithoutResizingTheReadingAreaAndHidesAtTheEnd(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        await window.TranscriptAnchorReviewAsync(false); await MainWindow.SettleAsync();
        Assert.IsTrue(window.ReviewLatestVisible, window.ReviewScrollDebug); Assert.AreEqual(1d, window.ReviewLatestOpacity, 0.01);
        var before = window.ReviewBounds()["messages"]; var button = window.ReviewLatestBounds;
        Assert.AreEqual(40d, button.Width, 0.1); Assert.AreEqual(before.Left + before.Width / 2, button.Left + button.Width / 2, 1d);
        Assert.IsTrue(button.Bottom < window.ReviewBounds()["footer"].Top);
        Assert.AreEqual(new LocalizationService(language)["JumpToLatest"], window.ReviewLatestName);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"latest-floating-{theme}-{language}"));
        window.ReviewJumpLatest(); await MainWindow.SettleAsync(); Assert.IsFalse(window.ReviewLatestVisible);
        Assert.AreEqual(before.Height, window.ReviewBounds()["messages"].Height, 0.1);
        await window.TranscriptAnchorReviewAsync(false); window.ReviewToggleLatest(false); window.ReviewToggleLatest(true);
        await MainWindow.SettleAsync(); Assert.IsTrue(window.ReviewLatestVisible); Assert.AreEqual(1d, window.ReviewLatestOpacity, 0.01);
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "de-DE")]
    [DataRow("Dark", "ar-SA")]
    public async Task LicensesFitAndReturnToUnsavedSettings(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        var (dialog, closing) = await window.ReviewOpenSettings(); var text = new LocalizationService(language);
        var combo = Children(dialog).OfType<ComboBox>().First(c => Equals(c.Header, text["Theme"])); combo.SelectedIndex = 1;
        var button = Children(dialog).OfType<Button>().First(b => Equals(b.Content, text["OpenSourceLicenses"])); Invoke(button);
        await MainWindow.SettleAsync(); Assert.AreEqual(text["OpenSourceLicenses"], dialog.Title); Assert.AreEqual("", dialog.PrimaryButtonText);
        Assert.IsTrue(Children(dialog).OfType<LicensePanel>().Any()); Assert.IsTrue(dialog.ActualWidth <= window.ReviewRoot.ActualWidth + 1 && dialog.ActualHeight <= window.ReviewRoot.ActualHeight + 1);
        var scroller = Children(dialog).OfType<LicensePanel>().SelectMany(Children).OfType<ScrollViewer>().Single();
        Assert.AreEqual(FlowDirection.LeftToRight, scroller.FlowDirection, "Original license text must retain its direction inside RTL chrome");
        foreach (var original in Children(scroller).OfType<TextBlock>().Where(t => t.IsTextSelectionEnabled))
        {
            var origin = original.TransformToVisual(scroller).TransformPoint(new(0, 0));
            Assert.IsTrue(origin.X >= -1 && origin.X + original.ActualWidth <= scroller.ActualWidth + 1, "License text must fit the viewport horizontally");
        }
        TestContext.AddResultFile(await window.CaptureReviewAsync($"licenses-{theme}-{language}", dialog));
        var back = Children(dialog).OfType<Button>().First(b => FrameworkElementAutomationPeer.CreatePeerForElement(b)?.GetName() == text["Back"]); Invoke(back);
        await MainWindow.SettleAsync(); Assert.AreEqual(text["Settings"], dialog.Title); Assert.AreEqual(1, combo.SelectedIndex);
        dialog.Hide(); await closing;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button)!.GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var next in Children(child)) yield return next; }
    }
}
