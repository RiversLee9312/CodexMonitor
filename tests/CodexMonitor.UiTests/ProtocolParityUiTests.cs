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
    [DataRow("Dark", "ar-SA", false)]
    [DataRow("Light", "zh-CN", true)]
    [DataRow("Dark", "ar-SA", true)]
    public async Task ProgramExpressionsRenderCommandsAndBatchesWithoutSourceWrappers(string theme, string language, bool batch)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        var code = batch ? "await Promise.all(['first','second'].map(async cmd=>text((await tools.exec_command({cmd})).output)));" : "const {exec_command:run}=tools; const cmd=`Write-Output 'Readable'`; text(JSON.stringify(await run({cmd})));";
        var entry = new TranscriptEntry("program", EntryKind.Tool, code, DateTimeOffset.Now, Tool: ToolActivity.FromCall("exec", code, "Readable output", ToolActivityState.Completed));
        await window.ShowProtocolEntryReviewAsync(entry);
        var content = string.Join("\n", window.ReviewVisualElements<TextBlock>().Select(t => t.Text));
        StringAssert.Contains(content, batch ? "first" : "Write-Output 'Readable'"); if (batch) StringAssert.Contains(content, "second");
        StringAssert.Contains(content, "Readable output");
        Assert.IsFalse(content.Contains("tools.exec_command", StringComparison.Ordinal)); Assert.IsFalse(content.Contains("JSON.stringify", StringComparison.Ordinal));
        Assert.IsFalse(window.ReviewVisualElements<Expander>().Any(e => Equals(e.Header, new LocalizationService(language)["RawData"])));
        foreach (var (_, bounds) in window.ReviewBounds()) Assert.IsTrue(bounds.Right <= window.ReviewRoot.ActualWidth + 1);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"program-expression-{theme}-{language}-{batch}"));
    }

    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "ar-SA")]
    public async Task ProgramSleepUsesTheClockInsteadOfTheGenericCommandIcon(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        const string code = "const {sleep}=tools.clock; text(await sleep({duration_ms:30000}));";
        await window.ShowProtocolEntryReviewAsync(new("program-sleep", EntryKind.Tool, code, DateTimeOffset.Now, Tool: ToolActivity.FromCall("exec", code, "Sleep completed.", ToolActivityState.Completed)));
        Assert.IsTrue(window.ReviewVisualElements<FontIcon>().Any(icon => icon.Glyph == "\uE917"));
        Assert.IsFalse(window.ReviewVisualElements<TextBlock>().Any(t => t.Text.Contains("tools.clock", StringComparison.Ordinal)));
        TestContext.AddResultFile(await window.CaptureReviewAsync($"program-sleep-{theme}-{language}"));
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "de-DE")]
    public async Task ThemeChangesKeepReadingRowsAndTheVisibleAnchor(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, theme, language);
        var before = await window.TranscriptAnchorReviewAsync(false);
        var anchor = window.ReviewMessageRows.Single(row => row.Entry.Id == "message-18");
        await window.SwitchReviewThemeAsync(theme == "Light" ? "Dark" : "Light");
        Assert.AreSame(anchor, window.ReviewMessageRows.Single(row => row.Entry.Id == "message-18"));
        var after = await window.TranscriptAnchorReviewAsync(true); Assert.AreEqual(before, after, 2);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"theme-reading-anchor-{theme}-{language}"));
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN", false)]
    [DataRow("Dark", "ar-SA", false)]
    [DataRow("Light", "zh-CN", true)]
    [DataRow("Dark", "ar-SA", true)]
    public async Task InternalCommandArraysHaveReadableHeadersAndExpandedOutput(string theme, string language, bool wrapped)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        using var document = JsonDocument.Parse("""{"id":"command","type":"CommandExecution","command":["pwsh.exe","-Command","Write-Output \"Hello world\""],"aggregated_output":"Hello world","status":"Completed","duration":{"secs":1,"nanos":200000000}}""");
        var entry = CodexClient.ParseItem(document.RootElement);
        if (wrapped) entry = entry with { Tool = ToolActivity.FromCall("exec", """text(await tools.exec_command({cmd:"Write-Output \"Hello world\""}));""", "ORCHESTRATION ENVELOPE", ToolActivityState.Completed) with { DisplayOutput = "Hello world" } };
        await window.ShowProtocolEntryReviewAsync(entry);
        Assert.IsTrue(window.ReviewVisualElements<TextBlock>().Any(block => block.Text.Contains("Write-Output", StringComparison.Ordinal)));
        Assert.IsTrue(window.ReviewVisualElements<TextBlock>().Any(block => block.Text == "Hello world"));
        Assert.IsFalse(window.ReviewVisualElements<TextBlock>().Any(block => block.Text.Contains("tools.exec_command", StringComparison.Ordinal)), "Adapted command details should show the command rather than its orchestration wrapper");
        Assert.IsFalse(window.ReviewVisualElements<TextBlock>().Any(block => block.Text.Contains("ORCHESTRATION ENVELOPE", StringComparison.Ordinal)), "The readable native output should be displayed while the original envelope remains in the transcript");
        Assert.IsFalse(window.ReviewVisualElements<Expander>().Any(e => Equals(e.Header, new LocalizationService(language)["RawData"])));
        TestContext.AddResultFile(await window.CaptureReviewAsync($"native-command-array-{theme}-{language}-{wrapped}"));
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "ar-SA")]
    public async Task InternalFileChangesRenderAsReadableFileRowsAndDiffs(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        using var document = JsonDocument.Parse("""{"id":"patch","type":"FileChange","status":"Completed","changes":{"D:/New.cs":{"type":"Add","content":"first\nsecond\n"}},"stdout":"Applied"}""");
        await window.ShowProtocolEntryReviewAsync(CodexClient.ParseItem(document.RootElement));
        foreach (var disclosure in window.ReviewVisualElements<ActivityDisclosure>()) disclosure.SetExpanded(true);
        await MainWindow.SettleAsync();
        var content = string.Join("\n", window.ReviewVisualElements<TextBlock>().Select(t => t.Text));
        StringAssert.Contains(content, "New.cs"); StringAssert.Contains(content, "+first"); StringAssert.Contains(content, "+second"); StringAssert.Contains(content, "Applied");
        Assert.IsFalse(window.ReviewVisualElements<Expander>().Any(e => Equals(e.Header, new LocalizationService(language)["RawData"])));
        TestContext.AddResultFile(await window.CaptureReviewAsync($"native-file-map-{theme}-{language}"));
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN", false)]
    [DataRow("Dark", "de-DE", true)]
    [DataRow("Dark", "ar-SA", false)]
    public async Task ServerSleepAndLocalSleepUseTheSameClockAndLiveWaitEndsCorrectly(string theme, string language, bool live)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        using var doc = JsonDocument.Parse(language == "zh-CN" ? """{"id":"sleep","type":"Extension","kind":"clock.sleep","durationMs":30000}""" : """{"id":"sleep","type":"sleep","durationMs":30000}""");
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
