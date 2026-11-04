using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class ActivityDetailsUiTests
{
    public TestContext TestContext { get; set; } = null!;
    [UITestMethod]
    [DataRow("mcp", "Light", "zh-CN", """{"id":"m","type":"mcpToolCall","server":"github","tool":"search","arguments":{"query":"WinUI","path":"src/App.xaml"},"result":{"content":[{"type":"text","text":"Found 2 files"}]},"status":"completed"}""", null, "Found 2 files")]
    [DataRow("plan", "Dark", "zh-CN", """{"threadId":"review-session","turnId":"t","explanation":"验证发布前的三个步骤。","plan":[{"step":"Build","status":"completed"},{"step":"Test","status":"inProgress"},{"step":"Package","status":"pending"}]}""", "turn/plan/updated", "Package")]
    [DataRow("web", "Light", "de-DE", """{"id":"w","type":"webSearch","action":{"type":"findInPage","url":"https://developers.openai.com/codex/app-server","pattern":"thread/loaded/list"}}""", null, "thread/loaded/list")]
    [DataRow("agent", "Dark", "ar-SA", """{"id":"c","type":"collabToolCall","tool":"spawnAgent","newThreadId":"child-agent","prompt":"Review the layout","agentStatus":{"status":"completed"}}""", null, "Review the layout")]
    [DataRow("retry", "Light", "zh-CN", """{"threadId":"review-session","willRetry":true,"error":{"message":"The server is temporarily unavailable.","additionalDetails":"The current turn will retry."}}""", "error", "The current turn will retry.")]
    [DataRow("review", "Dark", "zh-CN", """{"id":"r","type":"exitedReviewMode","review":"The layout fits narrow and wide windows."}""", null, "The layout fits narrow and wide windows.")]
    [DataRow("image", "Light", "zh-CN", """{"id":"i","type":"imageView","path":"D:/Images/app-icon.png"}""", null, "D:/Images/app-icon.png")]
    [DataRow("diff", "Dark", "de-DE", """{"id":"d","type":"fileChange","changes":[{"path":"src/App.xaml","kind":"update","diff":"@@\n-old\n+new"}],"status":"completed"}""", null, "+new")]
    public async Task AdaptedDetailsAreReadableAndFitNarrowWindows(string name, string theme, string language, string json, string? method, string expected)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 650, theme, language);
        await window.ShowStructuredActivityReviewAsync(json, method);
        var visibleText = string.Join("\n", window.ReviewVisualElements<TextBlock>().Select(t => t.Text));
        StringAssert.Contains(visibleText, expected); Assert.IsFalse(visibleText.Contains("\"type\":")); Assert.IsFalse(visibleText.Contains("\"arguments\":"));
        var disclosures = window.ReviewVisualElements<ActivityDisclosure>();
        Assert.IsFalse(disclosures.Any(d => AutomationProperties.GetAutomationId(d) == "ActivityToggle-public-summary"), "The reasoning summary must be directly readable");
        Assert.IsTrue(window.ReviewVisualElements<RichTextBlock>().Count > 0, "The inline summary must render as readable text");
        Assert.IsFalse(window.ReviewVisualElements<Expander>().Any(e => Equals(e.Header, new LocalizationService(language)["RawData"])), "Adapted content should not need a raw-data fallback");
        foreach (var (_, bounds) in window.ReviewBounds()) Assert.IsTrue(bounds.Right <= window.ReviewRoot.ActualWidth + 1 && bounds.Bottom <= window.ReviewRoot.ActualHeight + 1);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"details-{name}-560-{theme}-{language}"));
    }
    [UITestMethod]
    public async Task StreamingPlanIsReplacedByTheFinalItemAndUsageUpdatesDoNotAccumulateRows()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN"); await window.ConnectReviewServerAsync();
        void Notify(string method, string json) { using var doc = System.Text.Json.JsonDocument.Parse(json); window.NotifyTranscriptReview(new(method, doc.RootElement.Clone())); }
        Notify("item/plan/delta", """{"threadId":"review-session","itemId":"p","delta":"Draft plan"}""");
        Notify("item/completed", """{"threadId":"review-session","item":{"id":"p","type":"plan","text":"Final plan"}}""");
        Notify("thread/tokenUsage/updated", """{"threadId":"review-session","tokenUsage":{"total":{"totalTokens":100}}}""");
        Notify("thread/tokenUsage/updated", """{"threadId":"review-session","tokenUsage":{"total":{"totalTokens":200}}}""");
        var activities = window.ReviewMessageRows.SelectMany(r => r.Activities).ToArray();
        Assert.HasCount(1, activities.Where(e => e.Id == "p").ToArray()); Assert.AreEqual("Final plan", activities.Single(e => e.Id == "p").Tool?.Input);
        Assert.HasCount(1, activities.Where(e => e.Tool?.CategoryKey == "EventUsage").ToArray());
        Notify("model/safetyBuffering/updated", """{"threadId":"review-session","showBufferingUi":true}""");
        Notify("model/safetyBuffering/updated", """{"threadId":"review-session","showBufferingUi":false}""");
        Assert.IsFalse(window.ReviewMessageRows.SelectMany(r => r.Activities).Any(e => e.Tool?.CategoryKey == "EventBuffering"));
    }
}
