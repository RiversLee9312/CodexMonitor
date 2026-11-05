using System.Text.Json;
using System.Threading.Channels;
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
public sealed class PagingAndInputUiTests
{
    public TestContext TestContext { get; set; } = null!;
    private static JsonElement J(string value) { using var d = JsonDocument.Parse(value); return d.RootElement.Clone(); }
    private sealed class InputServer : IRpcTransport
    {
        public List<JsonElement> Sent { get; } = [];
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        public Task SendAsync(string value, CancellationToken ct) { Sent.Add(J(value)); return Task.CompletedTask; }
        public async Task<string?> ReceiveAsync(CancellationToken ct) { try { return await _messages.Reader.ReadAsync(ct); } catch (ChannelClosedException) { return null; } }
        public ValueTask DisposeAsync() { _messages.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    { if (root is T found) yield return found; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "ar-SA")]
    public async Task QuestionsHaveNoDefaultAnswerMaskSecretsAndSendOnlyOnExplicitSubmit(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        var server = new InputServer(); var client = new CodexClient(new JsonRpcClient(server)); window.ReviewAttachTranscriptServer(client);
        var handling = window.ReviewUserInputAsync(client, new("item/tool/requestUserInput", J("""{"threadId":"review-session","turnId":"turn","itemId":"input","questions":[{"id":"mode","header":"Mode","question":"Choose how to proceed","options":[{"label":"Review first","description":"Keep the result available for review."},{"label":"Continue","description":"Proceed with the selected changes."}]},{"id":"value","header":"Private value","question":"Enter a private value","isSecret":true}]}"""), J("42")));
        await MainWindow.SettleAsync(); var dialog = window.ReviewInputDialog!;
        Assert.IsFalse(dialog.IsPrimaryButtonEnabled); Assert.AreEqual(0, server.Sent.Count); Assert.AreEqual(ContentDialogButton.None, dialog.DefaultButton);
        All<RadioButton>(dialog).First().IsChecked = true; var secret = All<PasswordBox>(dialog).Single(); secret.Password = "synthetic-private-value"; await MainWindow.SettleAsync();
        Assert.IsTrue(dialog.IsPrimaryButtonEnabled); Assert.IsFalse(All<TextBox>(dialog).Any(t => t.Text.Contains("synthetic-private", StringComparison.Ordinal)));
        TestContext.AddResultFile(await window.CaptureReviewAsync($"question-{theme}-{language}", dialog));
        var submit = All<Button>(dialog).Single(b => b.Name == "PrimaryButton"); ((IInvokeProvider)new ButtonAutomationPeer(submit).GetPattern(PatternInterface.Invoke)).Invoke();
        await handling; Assert.AreEqual(1, server.Sent.Count); Assert.AreEqual(42L, server.Sent[0].Field("id").GetInt64());
        Assert.AreEqual("Review first", server.Sent[0].Field("result").Field("answers").Field("mode").Field("answers").Items().Single().GetString());
    }
    [UITestMethod]
    public async Task ResolvedRequestsAndDisconnectClosePromptsWithoutSendingAStaleAnswer()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Dark", "zh-CN");
        var server = new InputServer(); var client = new CodexClient(new JsonRpcClient(server)); window.ReviewAttachTranscriptServer(client);
        var question = J("""{"threadId":"review-session","turnId":"turn","itemId":"input","questions":[{"id":"free","header":"Details","question":"What should happen next?"}]}""");
        var first = window.ReviewUserInputAsync(client, new("tool/requestUserInput", question, J("\"request\""))); await MainWindow.SettleAsync(); Assert.IsNotNull(window.ReviewInputDialog);
        window.NotifyTranscriptReview(new("serverRequest/resolved", J("""{"requestId":"request","threadId":"review-session"}"""))); await first; await MainWindow.SettleAsync(); Assert.IsNull(window.ReviewInputDialog); Assert.AreEqual(0, server.Sent.Count);
        var second = window.ReviewUserInputAsync(client, new("item/tool/requestUserInput", question, J("43"))); await MainWindow.SettleAsync();
        await window.ReviewDisconnectTranscriptServer(); await second; Assert.AreEqual(0, server.Sent.Count);
    }
    [UITestMethod, DataRow(false), DataRow(true)]
    public async Task LocalHistoryAutomaticallyPrependsOnePageAndKeepsTheVisibleMessage(bool compressed)
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexMonitor-pages-" + Guid.NewGuid() + ".jsonl");
        try
        {
            await using (var writer = new StreamWriter(path))
                for (int i = 0; i < 2400; i++) await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "message", role = i % 2 == 0 ? "user" : "assistant", content = new[] { new { type = "text", text = "Paged message " + i + "\n\nSelectable **message** with `code` and a [link](https://example.com).\nSelectable **message** with `code` and a [link](https://example.com)." } } } }));
            if (compressed) await CompressFixture(path);
            var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Dark", "zh-CN"); window.ReviewTranscriptFile(path); window.ReviewUsePaging(); await window.ReviewReloadTranscript(open: true); await MainWindow.SettleAsync();
            Assert.AreEqual(300, window.ReviewLoadedCount); Assert.IsTrue(window.ReviewHasEarlier);
            var anchor = await window.ReviewScrollToEarlierAsync();
            for (int i = 0; i < 30 && window.ReviewLoadingEarlier; i++) await Task.Delay(30);
            await MainWindow.SettleAsync(); TestContext.WriteLine(window.ReviewHistoryState); Assert.AreEqual(600, window.ReviewLoadedCount); Assert.AreEqual(anchor.Y, window.ReviewItemY(anchor.Item), 2);
            await Task.Delay(300);
            Assert.AreEqual(anchor.Y, window.ReviewItemY(anchor.Item), 2, "Deferred layout must not move the preserved Markdown message after paging settles");
            Assert.IsLessThan(24d, window.ReviewFirstMessageY, "The settled transcript must not leave a blank gap above its first visible message");
            Assert.IsFalse(window.ReviewVisualElements<Button>().Any(b => b.Content is string s && s.Contains("Load earlier", StringComparison.OrdinalIgnoreCase)));
            TestContext.AddResultFile(await window.CaptureReviewAsync("automatic-history-dark"));
        }
        finally { File.Delete(path); File.Delete(path + ".zst"); }
    }
    [UITestMethod, DataRow(false), DataRow(true)]
    public async Task PagingInsideAToolGroupKeepsTheGroupDisclosureAndNestedAnchor(bool compressed)
    {
        ScrollAnchor.Diagnostics = TestContext.WriteLine;
        var path = Path.Combine(Path.GetTempPath(), "CodexMonitor-tool-pages-" + Guid.NewGuid() + ".jsonl");
        try
        {
            await using (var writer = new StreamWriter(path))
                for (int i = 0; i < 1200; i++) await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "function_call", call_id = "c" + i, name = "exec_command", arguments = "{\"cmd\":\"echo " + i + "\"}" } }));
            if (compressed) await CompressFixture(path);
            var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Light", "zh-CN"); window.ReviewTranscriptFile(path); window.ReviewUsePaging(); await window.ReviewReloadTranscript(open: true); await MainWindow.SettleAsync();
            var disclosure = window.ReviewVisualElements<ActivityDisclosure>().First(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("ActivityGroup-", StringComparison.Ordinal));
            var initial = window.ReviewVisualElements<ActivityGroupView>().Single().List;
            for (int i = 0; i < 80 && initial.Scroll.ScrollableHeight - initial.Scroll.VerticalOffset >= 2; i++) await Task.Delay(25);
            TestContext.WriteLine("Initial work viewport: " + window.ReviewScrollDebug + ", " + window.ReviewHistoryState);
            Assert.IsLessThan(2d, initial.Scroll.ScrollableHeight - initial.Scroll.VerticalOffset, "A newly opened work process starts at its latest activity");
            var anchor = await window.ReviewScrollToolHistoryAsync(); for (int i = 0; i < 30 && window.ReviewLoadingEarlier; i++) await Task.Delay(30); await MainWindow.SettleAsync();
            Assert.AreEqual(600, window.ReviewLoadedCount); Assert.AreSame(disclosure, window.ReviewVisualElements<ActivityDisclosure>().First(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("ActivityGroup-", StringComparison.Ordinal)));
            Assert.IsInstanceOfType<FrameworkElement>(anchor.List.ContainerFromItem(anchor.Item));
            var y = ((FrameworkElement)anchor.List.ContainerFromItem(anchor.Item)!).TransformToVisual(anchor.List).TransformPoint(new(0, 0)).Y; Assert.AreEqual(anchor.Y, y, 2);
            TestContext.AddResultFile(await window.CaptureReviewAsync("automatic-tool-history-light"));
            await window.ReviewNativeWorkScrollAsync();
            Assert.AreEqual(900, window.ReviewLoadedCount, "Accessibility scrolling inside the group must request the next page without calling the paging entry point");
        }
        finally { ScrollAnchor.Diagnostics = null; File.Delete(path); File.Delete(path + ".zst"); }
    }
    private static async Task CompressFixture(string path)
    {
        await Task.Run(async () =>
        {
            await using (var output = File.Create(path + ".zst"))
            await using (var encoder = new ZstdSharp.CompressionStream(output))
            await using (var input = File.OpenRead(path)) await input.CopyToAsync(encoder);
            File.Delete(path);
        });
    }
}
