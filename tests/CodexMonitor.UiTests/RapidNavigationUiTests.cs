using System.Diagnostics;
using System.Text.Json;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class RapidNavigationUiTests
{
    public TestContext TestContext { get; set; } = null!;

    [UITestMethod]
    public async Task NativeScrollPatternThenSelectionKeepsDispatcherResponsiveWithPromptSizedTitles()
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        // Real metadata includes full prompts as titles: the largest observed was 242,415 characters.
        var prompt = string.Concat(Enumerable.Repeat("## Task\n请检查所有实现、注释和测试。 **Preserve state** and avoid stale results.\n", 3800));
        var file = Path.Combine(Path.GetTempPath(), "CodexMonitor-navigation-" + Guid.NewGuid() + ".jsonl");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { type = "event_msg", timestamp = DateTimeOffset.UtcNow, payload = new { type = "agent_message", message = "Selected conversation is ready." } }) + "\n");
        var sessions = Enumerable.Range(0, 1000).Select(i => new SessionInfo
        {
            Id = "rapid-" + i, Title = "Conversation " + i + (i % 3 == 0 ? "\n" + prompt : ""), FilePath = file,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-i), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i), State = SessionState.Completed
        }).ToArray();
        var clock = Stopwatch.StartNew(); double previous = clock.Elapsed.TotalMilliseconds, maxGap = 0;
        var ticks = 0; var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - previous); previous = now; ticks++; };
        timer.Start();
        try
        {
            await window.ReviewSessionCatalogAsync(sessions);
            for (int i = 0; i < 60; i++)
            {
                window.ReviewNativeSessionScroll(i % 4 < 2 ? 100 : 0);
                await Task.Delay(16);
            }
            window.ReviewNativeSessionScroll(0);
            var click = Stopwatch.StartNew(); window.ReviewSelectSession(3);
            var synchronousClickMs = click.Elapsed.TotalMilliseconds;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (window.ReviewTranscriptLoading) await Task.Delay(10, timeout.Token);
            await MainWindow.SettleAsync();
            TestContext.WriteLine($"Navigation sample: max dispatcher gap={maxGap:F1} ms; synchronous selection={synchronousClickMs:F1} ms; content ready={click.Elapsed.TotalMilliseconds:F1} ms; ticks={ticks}; rows={window.ReviewSessionRows.Count}");
            Assert.Contains("Selected conversation is ready.", window.ReviewTranscriptText);
            Assert.AreEqual(window.ReviewSessionRows.Count, window.ReviewSessionRows.Select(s => s.Session.Id).Distinct().Count());
            Assert.IsLessThan(500d, maxGap, "A dispatcher stall must fail even if the test eventually finishes. Physical wheel bursts are also exercised with --manual-review-navigation.");
            Assert.IsLessThan(150d, synchronousClickMs);
            Assert.IsTrue(window.ReviewSessionRows.All(row => row.Title.Length <= 181 && row.AccessibleName.Length < 600));
            TestContext.AddResultFile(await window.CaptureReviewAsync("rapid-navigation-prompt-titles"));
        }
        finally { timer.Stop(); File.Delete(file); }
    }
}
