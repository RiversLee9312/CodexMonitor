using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class TurnInfoUiTests
{
    public TestContext TestContext { get; set; } = null!;
    [UITestMethod, DataRow("Light", "zh-CN"), DataRow("Dark", "de-DE"), DataRow("Dark", "ar-SA")]
    public async Task TurnDetailsFitNarrowWindowsAndUseLocalizedDatesAndTokenScopes(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        var flyout = await window.ReviewOpenTurnInfoAsync();
        try
        {
            var scroll = (ScrollViewer)flyout.Content; var panel = (TurnDetailsPanel)scroll.Content;
            var text = new LocalizationService(language);
            var labels = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            CollectionAssert.Contains(labels, text["LastRequestUsage"]); CollectionAssert.Contains(labels, text["ThreadUsage"]);
            CollectionAssert.Contains(labels, text["TurnDetails"]); CollectionAssert.Contains(labels, "turn-info");
            Assert.IsTrue(panel.ActualWidth <= window.ReviewRoot.ActualWidth - 32); Assert.IsTrue(scroll.ActualHeight > 100 && scroll.ActualHeight <= window.ReviewRoot.ActualHeight);
            Assert.AreEqual(window.ReviewTheme, panel.ActualTheme);
            Assert.IsTrue(window.ReviewVisualElements<TextBlock>().Any(t => t.Text == text.MessageTime(DateTimeOffset.Now.AddDays(-1))));
            Assert.IsFalse(labels.Any(t => t.StartsWith('{')));
            flyout.Hide(); await MainWindow.SettleAsync();
            var owner = window.ReviewVisualElements<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "TurnInfo-turn-info");
            flyout.ShowAt(owner); await MainWindow.SettleAsync(); Assert.AreSame(panel, ((ScrollViewer)flyout.Content).Content);
            // The popup is captured by the native Axe scenes; the base view is attached here.
            TestContext.AddResultFile(await window.CaptureReviewAsync($"turn-info-{theme}-{language}"));
        }
        finally { flyout.Hide(); await MainWindow.SettleAsync(); }
    }
    [UITestMethod]
    public async Task EachTurnHasOneTrailingActionIncludingSteeringAndToolOnlyTails()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var entries = new TranscriptEntry[] {
            new("u", EntryKind.User, "First input", default, TurnId: "one"), new("a", EntryKind.Assistant, "Commentary", default, TurnId: "one"),
            new("steer", EntryKind.User, "Additional input", default, TurnId: "one"), new("tool", EntryKind.Tool, "", default, Tool: new("sleep", "{\"duration_ms\":1000}"), TurnId: "one"),
            new("b", EntryKind.Assistant, "Second turn", default, TurnId: "two") };
        await window.ShowConversationPresentationReviewAsync(entries, false);
        Assert.AreEqual(2, window.ReviewMessageRows.Count(r => r.Turn is not null));
        Assert.IsNotNull(window.ReviewMessageRows.Single(r => r.Entry.Id == "tool").Turn);
        var buttons = window.ReviewVisualElements<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("TurnInfo-", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, buttons); Assert.IsTrue(buttons.All(b => b.MinWidth >= 40 && b.MinHeight >= 40 && AutomationProperties.GetName(b).Length > 0));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var nested in Descendants(VisualTreeHelper.GetChild(root,i))) yield return nested; }
}
