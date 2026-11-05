using System.Diagnostics;
using System.Text.Json;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class RapidNavigationUiTests
{
    public TestContext TestContext { get; set; } = null!;

    [UITestMethod]
    public async Task RecycledCardShowsChangesMadeWhileItWasUnloaded()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var entry = new TranscriptEntry("recycled", EntryKind.Assistant, "Before update", default);
        var text = new LocalizationService("zh-CN"); var row = new EntryRow(entry, text);
        var card = new TranscriptCard { Row = row };
        var root = (Grid)window.ReviewRoot;
        try
        {
            root.Children.Add(card); await MainWindow.SettleAsync();
            root.Children.Remove(card); await MainWindow.SettleAsync();
            row.Update(new EntryRow(entry with { Text = "After update" }, text));
            root.Children.Add(card); await MainWindow.SettleAsync();
            Assert.IsTrue(window.ReviewVisualElements<RichTextBlock>().Any(block => block.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().Any(run => run.Text == "After update")));
        }
        finally { root.Children.Remove(card); }
    }

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
        // This benchmark follows large transcript/automation fixtures in the same process.
        // Retire their garbage before timing; all collections caused by this workload still
        // count in maxGap. Never hide a pause or relax the interactive latency threshold.
        await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
        var clock = Stopwatch.StartNew(); double previous = clock.Elapsed.TotalMilliseconds, maxGap = 0;
        string phase = "catalog", slowestPhase = ""; var pause = GC.GetTotalPauseDuration(); double gapPause = 0; double slowestInput = 0;
        var ticks = 0; var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; var nextPause = GC.GetTotalPauseDuration(); if (now - previous > maxGap) { maxGap = now - previous; slowestPhase = phase; gapPause = (nextPause - pause).TotalMilliseconds; } pause = nextPause; previous = now; ticks++; };
        timer.Start();
        try
        {
            await window.ReviewSessionCatalogAsync(sessions);
            phase = "scroll";
            for (int i = 0; i < 60; i++)
            {
                phase = "scroll " + i;
                var inputStart = Stopwatch.GetTimestamp();
                window.ReviewNativeSessionScroll(i % 4 < 2 ? 100 : 0);
                slowestInput = Math.Max(slowestInput, Stopwatch.GetElapsedTime(inputStart).TotalMilliseconds);
                await Task.Delay(16);
            }
            window.ReviewNativeSessionScroll(0);
            phase = "selection";
            var click = Stopwatch.StartNew(); window.ReviewSelectSession(3);
            var synchronousClickMs = click.Elapsed.TotalMilliseconds;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (window.ReviewTranscriptLoading) await Task.Delay(10, timeout.Token);
            await MainWindow.SettleAsync();
            TestContext.WriteLine($"Navigation sample: max dispatcher gap={maxGap:F1} ms ({slowestPhase}), GC pause within gap={gapPause:F1} ms, longest synchronous scroll={slowestInput:F1} ms; synchronous selection={synchronousClickMs:F1} ms; content ready={click.Elapsed.TotalMilliseconds:F1} ms; ticks={ticks}; rows={window.ReviewSessionRows.Count}");
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
