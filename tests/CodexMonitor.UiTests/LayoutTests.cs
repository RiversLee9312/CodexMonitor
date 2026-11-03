using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class LayoutTests
{
    public TestContext TestContext { get; set; } = null!;

    [UITestMethod]
    [DataRow(560, 600, "Light", "zh-CN")]
    [DataRow(760, 650, "Dark", "zh-CN")]
    [DataRow(900, 700, "Light", "de-DE")]
    [DataRow(1180, 800, "Dark", "de-DE")]
    [DataRow(1440, 850, "Light", "zh-CN")]
    [DataRow(760, 650, "Dark", "ar-SA")]
    [DataRow(560, 600, "Light", "he-IL")]
    [DataRow(1180, 800, "Dark", "ar-SA")]
    public async Task ControlsRemainWithinWindowAndReadingArea(int width, int height, string theme, string language)
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(width, height, theme, language);
        var screenshot = await window.CaptureReviewAsync($"{width}x{height}-{theme}-{language}"); TestContext.AddResultFile(screenshot);
        foreach (var (name, rect) in window.ReviewBounds())
        {
            Assert.IsGreaterThan(0d, rect.Width, name); Assert.IsGreaterThan(0d, rect.Height, name);
            Assert.IsTrue(rect.Left >= -1 && rect.Top >= -1 && rect.Right <= window.ReviewRoot.ActualWidth + 1 && rect.Bottom <= window.ReviewRoot.ActualHeight + 1, $"{name} outside window: {rect}");
        }
        var bounds = window.ReviewBounds();
        Assert.IsTrue(bounds["messages"].Bottom <= bounds["footer"].Top + 1, "Transcript overlaps session actions");
        if (bounds.ContainsKey("composer")) Assert.IsTrue(bounds["composer"].Bottom <= bounds["send"].Top + 1, "Composer overlaps send button");
        else
        {
            Assert.IsFalse(window.ReviewComposerVisible); Assert.IsTrue(window.ReviewSetupVisible);
            Assert.AreNotEqual(window.ReviewResumeButtonVisible, window.ReviewConnectionButtonVisible, "Exactly one session action must be offered");
            var action = bounds.ContainsKey("resume") ? bounds["resume"] : bounds["sessionConnect"];
            Assert.IsTrue(bounds["setupHelp"].Bottom <= action.Top + 1);
        }
        Assert.AreEqual(window.ReviewRoot.ActualWidth < 900, window.ReviewIsOverlay);
        Assert.AreEqual(language is "ar-SA" or "he-IL", window.ReviewIsRtl);
    }
    [UITestMethod]
    public async Task NarrowDrawerIsOpaqueAndDoesNotShowTranscriptThroughIt()
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(560, 600, "Dark", "zh-CN", openPane: true);
        TestContext.AddResultFile(await window.CaptureReviewAsync("drawer-560-dark-zh"));
        Assert.IsTrue(window.ReviewPaneOpaque, "Overlay drawer must have an opaque theme-aware background");
    }
    [UITestMethod]
    public async Task CaptionButtonsFollowActualThemeInBothDirections()
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(760, 650, "Light", "zh-CN");
        foreach (var theme in new[] { "Dark", "Light", "Dark", "Light" })
        {
            await window.SwitchReviewThemeAsync(theme);
            Assert.AreEqual(theme == "Dark" ? ElementTheme.Dark : ElementTheme.Light, window.ReviewTheme);
            Assert.AreEqual(theme == "Dark" ? TitleBarTheme.Dark : TitleBarTheme.Light, window.AppWindow.TitleBar.PreferredTheme, "Native caption theme must update after ActualThemeChanged");
            Assert.IsNull(window.AppWindow.TitleBar.ButtonForegroundColor, "Native caption foreground must not be pinned to the previous theme");
            TestContext.AddResultFile(await window.CaptureReviewAsync("switch-" + theme));
        }
    }
    [UITestMethod]
    public async Task NarrowSettingsDialogFitsWindowWithLongGermanLabels()
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(560, 600, "Dark", "de-DE");
        TestContext.AddResultFile(await window.CaptureSettingsReviewAsync());
    }
}
