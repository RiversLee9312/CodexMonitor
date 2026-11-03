using System.Text.Json;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class ProtocolParityUiTests
{
    public TestContext TestContext { get; set; } = null!;
    [UITestMethod]
    [DataRow("Light", "zh-CN", false)]
    [DataRow("Dark", "de-DE", true)]
    [DataRow("Dark", "ar-SA", false)]
    public async Task ServerSleepAndLocalSleepUseTheSameClockAndLiveWaitEndsCorrectly(string theme, string language, bool live)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        using var doc = JsonDocument.Parse("""{"id":"sleep","type":"sleep","durationMs":30000}""");
        var entry = CodexClient.ParseItem(doc.RootElement, lifecycle: live ? ToolActivityState.Running : ToolActivityState.Completed);
        await window.ShowProtocolEntryReviewAsync(entry, live: live);
        Assert.AreEqual(live, window.ReviewWaitAnimating);
        if (live)
        {
            using var done = JsonDocument.Parse("""{"threadId":"review-session","item":{"id":"sleep","type":"sleep","durationMs":30000}}""");
            window.NotifyTranscriptReview(new("item/completed", done.RootElement.Clone())); await MainWindow.SettleAsync();
            Assert.IsFalse(window.ReviewWaitAnimating);
        }
        Assert.IsTrue(window.ReviewVisualElements<FontIcon>().Any(icon => icon.Glyph == "\uE917"), "The adapted sleep must use the clock icon");
        Assert.IsFalse(window.ReviewVisualElements<Expander>().Any(e => Equals(e.Header, new LocalizationService(language)["RawData"])));
        TestContext.AddResultFile(await window.CaptureReviewAsync($"server-sleep-{theme}-{language}-{live}"));
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "ar-SA")]
    public async Task InterAgentMessagesHaveReadableDirectionsAndNoEncryptedPayload(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        using var document = JsonDocument.Parse("""{"id":"agent","type":"agent_message","author":"/root/reviewer","recipient":"/root","content":[{"type":"input_text","text":"Review complete. Ready for the next step."},{"type":"encrypted_content","encrypted_content":"SECRET_ENCRYPTED"}]}""");
        await window.ShowProtocolEntryReviewAsync(CodexClient.ParseItem(document.RootElement));
        var body = string.Join("\n", window.ReviewVisualElements<TextBlock>().Select(t => t.Text));
        StringAssert.Contains(body, "Review complete"); StringAssert.Contains(body, new LocalizationService(language)["FieldAuthor"]);
        StringAssert.Contains(body, new LocalizationService(language)["FieldRecipient"]); Assert.IsFalse(body.Contains("SECRET_ENCRYPTED"));
        var direction = window.ReviewVisualElements<TextBlock>().Single(t => t.Text == "/root/reviewer → /root");
        Assert.AreEqual(FlowDirection.LeftToRight, direction.FlowDirection, "Agent paths and the direction arrow must retain their logical order inside RTL chrome");
        Assert.IsFalse(window.ReviewVisualElements<Expander>().Any(e => Equals(e.Header, new LocalizationService(language)["RawData"])));
        foreach (var (_, bounds) in window.ReviewBounds()) Assert.IsTrue(bounds.Right <= window.ReviewRoot.ActualWidth + 1 && bounds.Bottom <= window.ReviewRoot.ActualHeight + 1);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"agent-message-{theme}-{language}"));
    }
    [UITestMethod]
    public async Task ServiceNotificationsStayInSessionDetailsWithoutLeakingTokensOrClutteringTheTranscript()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Dark", "zh-CN"); await window.ConnectReviewServerAsync();
        var count = window.ReviewMessageRows.Count;
        using var account = JsonDocument.Parse("""{"authMode":"chatgpt","planType":"pro","refreshToken":"SECRET"}""");
        window.NotifyTranscriptReview(new("account/updated", account.RootElement.Clone()));
        Assert.HasCount(count, window.ReviewMessageRows);
        var (dialog, closing) = await window.OpenProtocolDetailsReviewAsync();
        try
        {
            var section = Descendants(dialog).OfType<Expander>().Single(e => Equals(e.Header, "服务器状态")); section.IsExpanded = true; await MainWindow.SettleAsync();
            var text = string.Join("\n", Descendants(dialog).OfType<TextBlock>().Select(t => t.Text));
            StringAssert.Contains(text, "登录方式"); StringAssert.Contains(text, "chatgpt"); Assert.IsFalse(text.Contains("SECRET"));
            TestContext.AddResultFile(await window.CaptureReviewAsync("server-events-details", dialog));
        }
        finally { dialog.Hide(); await closing; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
