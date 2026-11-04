using System.Text.Json;
using System.Threading.Channels;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class SessionConnectionUiTests
{
    public TestContext TestContext { get; set; } = null!;
    private sealed class Locator(bool found) : ISessionServerLocator
    {
        public Task<IReadOnlyList<SessionServerMatch>> FindAsync(string id, string home, string executable, string? endpoint = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionServerMatch>>(found ? [new(new(new("ws://127.0.0.1:45123")), new() { Id = id, State = SessionState.Active, IsLive = true })] : []);
    }
    private sealed class Runtime(bool owns = true, bool turnKnown = true) : IRpcTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        public List<string> Methods { get; } = [];
        public Task SendAsync(string message, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(message); var request = doc.RootElement; var method = request.Str("method"); Methods.Add(method);
            object result = method switch
            {
                "thread/loaded/list" => new { data = owns ? new[] { "review-session" } : [] },
                "thread/read" or "thread/resume" => new { thread = new { id = "review-session", status = new { type = "active" } } },
                "thread/turns/list" => new { data = turnKnown ? new[] { new { id = "turn", status = "inProgress", items = Array.Empty<object>() } } : [] },
                "thread/items/list" => new { data = Array.Empty<object>() },
                _ => throw new InvalidOperationException("Unexpected action: " + method)
            };
            _incoming.Writer.TryWrite(JsonSerializer.Serialize(new { id = request.Field("id"), result })); return Task.CompletedTask;
        }
        public async Task<string?> ReceiveAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DeferredLocator : ISessionServerLocator
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<IReadOnlyList<SessionServerMatch>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<SessionServerMatch>> FindAsync(string id, string home, string executable, string? endpoint = null, CancellationToken ct = default)
        { Calls++; return Result.Task; } // Deliberately ignores cancellation to exercise late replies.
    }
    [UITestMethod]
    [DataRow("zh-CN", "Dark")]
    [DataRow("zh-CN", "Light")]
    [DataRow("de-DE", "Light")]
    [DataRow("ar-SA", "Dark")]
    public async Task SelectionChecksAvailabilityBeforeOfferingConnectAndCachesNoOwner(string language, string theme)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 600, theme, language);
        window.SetSessionStateReview(SessionState.RecordedActive);
        var lookup = new DeferredLocator(); var opened = false;
        window.ConfigureSessionConnectionReview(lookup, (_, _) => { opened = true; throw new InvalidOperationException(); });
        var checking = window.CheckSessionAvailabilityReviewAsync();
        Assert.IsTrue(window.ReviewServerChecking); Assert.IsFalse(window.ReviewConnectionButtonVisible);
        Assert.IsFalse(window.ReviewConnectionHelpVisible); Assert.IsFalse(window.ReviewComposerVisible);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"session-checking-560-{theme}-{language}"));
        lookup.Result.SetResult([]); await checking;
        Assert.IsFalse(window.ReviewServerChecking); Assert.IsFalse(window.ReviewConnectionButtonVisible);
        Assert.AreEqual(new LocalizationService(language)["SessionServerNotFound"], window.ReviewConnectionReason);
        Assert.IsFalse(opened); Assert.IsFalse(window.ReviewBannerOpen); Assert.IsFalse(window.ReviewSessionAttached);
        await window.CheckSessionAvailabilityReviewAsync(); Assert.AreEqual(1, lookup.Calls, "A transcript refresh must reuse the recent result");
        foreach (var (name, rect) in window.ReviewBounds()) Assert.IsTrue(rect.Right <= window.ReviewRoot.ActualWidth + 1 && rect.Bottom <= window.ReviewRoot.ActualHeight + 1, name);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"session-unavailable-560-{theme}-{language}"));
    }
    [UITestMethod]
    public async Task AvailabilityProbeOffersConnectWithoutOpeningOrResumingAndRetryOnlyChecks()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        window.SetSessionStateReview(SessionState.RecordedActive);
        var opened = false;
        window.ConfigureSessionConnectionReview(new Locator(true), (_, _) => { opened = true; throw new InvalidOperationException(); });
        await window.CheckSessionAvailabilityReviewAsync();
        Assert.IsTrue(window.ReviewConnectionButtonVisible); Assert.IsTrue(window.ReviewConnectionHelpVisible);
        Assert.IsFalse(window.ReviewResumeButtonVisible); Assert.IsFalse(opened); Assert.IsFalse(window.ReviewSessionAttached);
        window.ConfigureSessionConnectionReview(new Locator(false), (_, _) => throw new InvalidOperationException());
        await window.RetrySessionActionReviewAsync(); Assert.IsFalse(window.ReviewConnectionButtonVisible);
        window.ConfigureSessionConnectionReview(new Locator(true), (_, _) => throw new InvalidOperationException());
        await window.RetrySessionActionReviewAsync(); Assert.IsTrue(window.ReviewConnectionButtonVisible);
        Assert.IsFalse(window.ReviewComposerVisible); Assert.IsFalse(window.ReviewSessionAttached);
    }
    [UITestMethod]
    public async Task LateAvailabilityResultCannotOverwriteTheNextSelectedSession()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        window.SetSessionStateReview(SessionState.RecordedActive);
        var old = new DeferredLocator(); window.ConfigureSessionConnectionReview(old, (_, _) => throw new InvalidOperationException());
        var checking = window.CheckSessionAvailabilityReviewAsync();
        window.ChangeSelectedSessionReview("next-session");
        window.ConfigureSessionConnectionReview(new Locator(true), (_, _) => throw new InvalidOperationException());
        await window.CheckSessionAvailabilityReviewAsync();
        old.Result.SetResult([]); await checking;
        Assert.IsTrue(window.ReviewConnectionButtonVisible); Assert.AreEqual("", window.ReviewConnectionReason);
    }
    [UITestMethod]
    public async Task HistoryCompletionReplacesAnOldAvailabilityFailureWithRestore()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        window.SetSessionStateReview(SessionState.RecordedActive);
        window.ConfigureSessionConnectionReview(new Locator(false), (_, _) => throw new InvalidOperationException());
        await window.CheckSessionAvailabilityReviewAsync(); Assert.IsFalse(window.ReviewResumeButtonVisible);
        window.SetSessionStateReview(SessionState.Completed); await window.CheckSessionAvailabilityReviewAsync();
        Assert.IsTrue(window.ReviewResumeButtonVisible); Assert.AreEqual("", window.ReviewConnectionReason);
    }
    [UITestMethod]
    public async Task NoOwnerLeavesSessionReadOnlyAndShowsAnAccurateExplanation()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 600, "Light", "zh-CN");
        window.SetSessionStateReview(SessionState.RecordedActive);
        var opened = false;
        window.ConfigureSessionConnectionReview(new Locator(false), (_, _) => { opened = true; throw new InvalidOperationException(); });
        await window.ConnectSessionReviewAsync();
        Assert.IsFalse(opened); Assert.IsFalse(window.ReviewSessionAttached); Assert.IsFalse(window.ReviewCanCompose);
        StringAssert.Contains(window.ReviewConnectionReason, "应用服务器"); Assert.IsFalse(window.ReviewCanResume);
        Assert.IsFalse(window.ReviewBannerOpen); Assert.IsFalse(window.ReviewConnectionButtonVisible); Assert.IsFalse(window.ReviewConnectionHelpVisible);
        TestContext.AddResultFile(await window.CaptureReviewAsync("session-no-owner-560-light-zh"));
        var runtime = new Runtime();
        window.ConfigureSessionConnectionReview(new Locator(true), (_, _) => Task.FromResult(new CodexClient(new JsonRpcClient(runtime))));
        await window.ConnectSessionReviewAsync();
        Assert.IsTrue(window.ReviewComposerVisible); Assert.IsFalse(window.ReviewSetupVisible); Assert.AreEqual("", window.ReviewConnectionReason);
    }
    [UITestMethod]
    [DataRow(true)] [DataRow(false)]
    public async Task OwningServerIsJoinedWithoutStartingATurnAndSteerWaitsForTheRuntimeId(bool turnKnown)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        var runtime = new Runtime(turnKnown: turnKnown);
        window.ConfigureSessionConnectionReview(new Locator(true), (_, _) => Task.FromResult(new CodexClient(new JsonRpcClient(runtime))));
        await window.ConnectSessionReviewAsync();
        Assert.IsTrue(window.ReviewSessionAttached); Assert.AreEqual(turnKnown, window.ReviewCanCompose);
        Assert.AreEqual(!turnKnown, window.ReviewSetupVisible); Assert.AreEqual(!turnKnown, window.ReviewConnectionButtonVisible); Assert.AreEqual(turnKnown, window.ReviewComposerVisible);
        Assert.IsFalse(runtime.Methods.Contains("turn/start")); Assert.IsFalse(runtime.Methods.Contains("turn/steer"));
        TestContext.AddResultFile(await window.CaptureReviewAsync("session-owner-760-dark-" + turnKnown));
    }
    [UITestMethod]
    public async Task OwnerDisappearingBeforeJoinDoesNotResumeOnTheWrongServer()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        var runtime = new Runtime(owns: false);
        window.ConfigureSessionConnectionReview(new Locator(true), (_, _) => Task.FromResult(new CodexClient(new JsonRpcClient(runtime))));
        await window.ConnectSessionReviewAsync();
        Assert.IsFalse(window.ReviewSessionAttached); Assert.IsFalse(runtime.Methods.Contains("thread/resume"));
    }
    [UITestMethod]
    public async Task UnrelatedGlobalConnectionIsKeptButDoesNotEnableThisSession()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        var unrelated = new Runtime(owns: false); window.SetSessionServerReview(new(new JsonRpcClient(unrelated)));
        window.ConfigureSessionConnectionReview(new Locator(false), (_, _) => throw new InvalidOperationException());
        await window.ConnectSessionReviewAsync();
        Assert.IsTrue(window.ReviewHasServer); Assert.IsFalse(window.ReviewSessionAttached); Assert.IsFalse(window.ReviewCanCompose);
        Assert.IsFalse(unrelated.Methods.Contains("thread/resume")); Assert.IsFalse(window.ReviewConnectionButtonVisible); Assert.IsFalse(window.ReviewBannerOpen);
    }
    [UITestMethod]
    public async Task CurrentOwningServerIsReusedWithoutScanningOtherServers()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        var runtime = new Runtime(); window.SetSessionServerReview(new(new JsonRpcClient(runtime)));
        window.ConfigureSessionConnectionReview(new Locator(false), (_, _) => throw new InvalidOperationException());
        await window.ConnectSessionReviewAsync();
        Assert.IsTrue(window.ReviewSessionAttached); Assert.IsTrue(window.ReviewCanCompose); Assert.IsTrue(runtime.Methods.Contains("thread/resume"));
        Assert.IsFalse(runtime.Methods.Contains("turn/start"));
    }
    [UITestMethod]
    public async Task RestoreRechecksAgainAfterServerConnectionAndNeverResumesAChangedActiveThread()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        var runtime = new Runtime(owns: false); window.SetSessionServerReview(new(new JsonRpcClient(runtime)));
        window.ConfigureSessionConnectionReview(new Locator(false), (_, _) => throw new InvalidOperationException());
        var checks = 0;
        window.ConfigureResumeGuardReview((session, _) => Task.FromResult(session with { State = ++checks == 1 ? SessionState.Completed : SessionState.RecordedActive }));
        await window.ResumeSessionReviewAsync();
        Assert.AreEqual(2, checks); Assert.IsFalse(runtime.Methods.Contains("thread/resume")); Assert.IsFalse(window.ReviewSessionAttached);
    }
    [UITestMethod]
    public async Task StreamedSummaryAndOutputStayGroupedAndCompletionIsAuthoritative()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        await window.ShowActivityReviewAsync(); await window.ConnectReviewServerAsync();
        void Notify(string method, object values) { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(values)); window.NotifyTranscriptReview(new(method, doc.RootElement.Clone())); }
        Notify("item/reasoning/summaryTextDelta", new { threadId = "review-session", itemId = "stream-summary", delta = "Checking " });
        Notify("item/reasoning/summaryTextDelta", new { threadId = "review-session", itemId = "stream-summary", delta = "the server." });
        Notify("item/reasoning/textDelta", new { threadId = "review-session", itemId = "stream-summary", delta = "PRIVATE RAW REASONING" });
        Notify("item/started", new { threadId = "review-session", item = new { id = "stream-command", type = "commandExecution", command = "dotnet test", status = "inProgress" } });
        Notify("item/commandExecution/outputDelta", new { threadId = "review-session", itemId = "stream-command", delta = "partial" });
        Notify("item/completed", new { threadId = "review-session", item = new { id = "stream-command", type = "commandExecution", command = "dotnet test", status = "completed", aggregatedOutput = "final", exitCode = 0 } });
        var last = window.ReviewMessageRows.Last(); Assert.HasCount(2, last.Activities);
        Assert.AreEqual("Checking the server.", last.Activities[0].Text); Assert.AreEqual("final", last.Activities[1].Tool?.Output);
        Assert.AreEqual("PRIVATE RAW REASONING", last.Activities[0].Reasoning?.FullText);
        Assert.AreEqual(ToolActivityState.Completed, last.Activities[1].Tool?.State);
    }
    [UITestMethod]
    [DataRow(560, 600, "Dark", "zh-CN", false)]
    [DataRow(1180, 800, "Light", "zh-CN", true)]
    [DataRow(560, 650, "Light", "de-DE", true)]
    [DataRow(560, 650, "Dark", "ar-SA", true)]
    public async Task ReasoningAndToolsShareACompactGroupAndFitTheWindow(int width, int height, string theme, string language, bool expand)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(width, height, theme, language);
        await window.ShowActivityReviewAsync(expand);
        Assert.HasCount(3, window.ReviewMessageRows);
        var activity = window.ReviewMessageRows[1]; Assert.HasCount(6, activity.Activities);
        Assert.AreEqual(EntryKind.Reasoning, activity.Activities[0].Kind);
        Assert.AreEqual(5, activity.Activities.Count(e => e.Kind == EntryKind.Tool));
        var action = window.ReviewBounds()["resume"]; Assert.IsTrue(action.Right <= window.ReviewRoot.ActualWidth + 1);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"activities-{width}-{theme}-{language}-{expand}"));
    }
    [UITestMethod]
    public void ActivityGroupingPreservesMessageBoundariesAndExpansionAcrossUpdates()
    {
        var expanded = new HashSet<string> { "t" }; var text = new LocalizationService("zh-CN");
        TranscriptEntry[] entries = [new("r", EntryKind.Reasoning, "Summary", default), new("t", EntryKind.Tool, "command", default), new("a", EntryKind.Assistant, "Reply", default), new("t2", EntryKind.Tool, "next", default)];
        var rows = EntryRow.Project(entries, text, expanded); Assert.HasCount(3, rows); Assert.HasCount(2, rows[0].Activities);
        var changed = EntryRow.Project(entries.Select(e => e.Id == "t" ? e with { Text = "result" } : e), text, expanded);
        Assert.IsFalse(rows[0].Matches(changed[0])); Assert.IsTrue(changed[0].Expanded.Contains("t"));
        Assert.AreEqual(EntryKind.Assistant, changed[1].Entry.Kind);
    }
    [UITestMethod]
    public void InlineDisclosuresExposeExpandCollapseWithoutASelectedAccentBackground()
    {
        var disclosure = new ActivityDisclosure();
        AutomationProperties.SetName(disclosure, "已运行 dotnet test");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(disclosure);
        Assert.IsNotNull(peer);
        var pattern = peer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider;
        Assert.IsNotNull(pattern); Assert.AreEqual(ExpandCollapseState.Collapsed, pattern.ExpandCollapseState);
        pattern.Expand(); Assert.IsTrue(disclosure.IsExpanded); Assert.AreEqual(ExpandCollapseState.Expanded, pattern.ExpandCollapseState);
        pattern.Collapse(); Assert.IsFalse(disclosure.IsExpanded);
    }
}
