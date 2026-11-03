using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using CodexMonitor.Core;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class ComposerStateUiTests
{
    private sealed class NoServers : ISessionServerLocator
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<SessionServerMatch>> FindAsync(string id, string home, string executable, string? endpoint = null, CancellationToken ct = default)
        { Calls++; return Task.FromResult<IReadOnlyList<SessionServerMatch>>([]); }
    }
    private sealed class AvailableServer : ISessionServerLocator
    {
        public Task<IReadOnlyList<SessionServerMatch>> FindAsync(string id, string home, string executable, string? endpoint = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionServerMatch>>([new(new(new("ws://127.0.0.1:45123")), new() { Id = id, State = SessionState.Active, IsLive = true })]);
    }
    public TestContext TestContext { get; set; } = null!;
    [UITestMethod]
    [DataRow(SessionState.Completed, false, true, "zh-CN")]
    [DataRow(SessionState.RecordedActive, false, false, "zh-CN")]
    [DataRow(SessionState.Unknown, false, false, "zh-CN")]
    [DataRow(SessionState.Idle, true, false, "zh-CN")]
    [DataRow(SessionState.RecordedActive, false, false, "de-DE")]
    [DataRow(SessionState.Unknown, false, false, "ar-SA")]
    public async Task ResumeAndConnectAreMutuallyExclusive(SessionState state, bool live, bool resume, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 600, "Dark", language);
        window.SetSessionStateReview(state, live);
        window.ConfigureSessionConnectionReview(new AvailableServer(), (_, _) => throw new InvalidOperationException("A probe must not attach"));
        await window.CheckSessionAvailabilityReviewAsync();
        Assert.AreEqual(resume, window.ReviewResumeButtonVisible); Assert.AreEqual(!resume, window.ReviewConnectionButtonVisible);
        Assert.IsFalse(window.ReviewComposerVisible);
        foreach (var (name, rect) in window.ReviewBounds()) Assert.IsTrue(rect.Bottom <= window.ReviewRoot.ActualHeight + 1 && rect.Right <= window.ReviewRoot.ActualWidth + 1, name);
        TestContext.AddResultFile(await window.CaptureReviewAsync("session-action-560-" + state + "-" + live + "-" + language));
    }
    [UITestMethod]
    public async Task ResumeRechecksFreshStateBeforeOpeningAnyServer()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 600, "Light", "zh-CN");
        var lookup = new NoServers(); window.ConfigureSessionConnectionReview(lookup, (_, _) => throw new InvalidOperationException());
        window.ConfigureResumeGuardReview((session, _) => Task.FromResult(session with { State = SessionState.RecordedActive }));
        await window.ResumeSessionReviewAsync();
        Assert.IsFalse(window.ReviewSessionAttached); Assert.IsFalse(window.ReviewCanResume);
        Assert.IsFalse(window.ReviewComposerVisible);
        Assert.AreEqual(1, lookup.Calls);
    }
    [UITestMethod]
    [DataRow(false, false, "zh-CN", "Dark")]
    [DataRow(true, false, "zh-CN", "Dark")]
    [DataRow(false, true, "zh-CN", "Light")]
    [DataRow(true, false, "de-DE", "Light")]
    [DataRow(false, true, "ar-SA", "Dark")]
    public async Task WaitActivityHasAReadableStateAndOnlyAnimatesVerifiedLiveWaits(bool live, bool completed, string language, string theme)
    {
        var window = TestApp.ReviewWindow; await window.ShowWaitReviewAsync(live, completed, language, theme);
        var row = window.ReviewMessageRows.Single(); Assert.AreEqual(live, row.IsLive); Assert.AreEqual(30000L, row.Entry.Tool?.WaitDurationMs);
        Assert.AreEqual(live && !completed, window.ReviewWaitAnimating);
        Assert.AreEqual(live, window.ReviewComposerVisible); Assert.AreEqual(!live, window.ReviewSetupVisible);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"wait-{language}-{theme}-{live}-{completed}"));
    }
}
