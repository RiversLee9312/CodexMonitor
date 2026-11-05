using System.Diagnostics;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

public sealed partial class InjectedScrollUiTests
{
    [UITestMethod, DataRow(560), DataRow(760), DataRow(1180)]
    public async Task FastWheelReversalAcrossVariableHeightMessagesLeavesNoViewportHoles(int width)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(width, 700, "Dark", "zh-CN");
        var entries = Enumerable.Range(0, 400).Select(i => new TranscriptEntry("variable-" + i,
            i % 3 == 0 ? EntryKind.User : EntryKind.Assistant, "Message " + i + "\n\n" + new string('字', (i % 7) * 700 + 20), default)).ToArray();
        await window.ShowConversationPresentationReviewAsync(entries, false);
        var input = new InjectedInput(window); await input.ActivateAsync();
        for (int batch = 0; batch < 16; batch++)
        {
            await input.WheelAsync(window.ReviewTranscriptSurface, batch % 4 < 2 ? -960 : 960, 12);
            await WaitForScrollIdle(window.ReviewTranscriptSurface.Scroll);
            try { AssertCovered(window.ReviewTranscriptSurface, 20); } // Transcript rows intentionally have 16 DIP spacing.
            catch
            {
                var list = window.ReviewTranscriptSurface;
                TestContext.WriteLine($"Batch {batch}: offset={list.Scroll.VerticalOffset}, extent={list.Scroll.ExtentHeight}, height={list.Scroll.ViewportHeight}, items={list.Items.Count}; " + string.Join("; ", list.RealizedElements.Select(e => $"{e.Index}:y={e.TransformToVisual(list.Scroll).TransformPoint(new(0,0)).Y},h={e.ActualHeight}")));
                TestContext.AddResultFile(await window.CaptureReviewAsync("injected-variable-height-gap-" + width)); throw;
            }
        }
        TestContext.AddResultFile(await window.CaptureReviewAsync("injected-variable-height-no-gaps-" + width));
    }
    [UITestMethod]
    public async Task ContinuousFastSidebarWheelThenClickLoadsAPromptTitledConversationWithoutBlocking()
    {
        var path = await HistoryAsync(2400);
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var prompt = string.Concat(Enumerable.Repeat("## Task\n请检查实现、注释和测试。 Preserve state during navigation.\n", 4500));
        var sessions = Sessions(1000).Select((s, i) => s with { Title = s.Title + (i % 3 == 0 ? "\n" + prompt : ""), FilePath = path }).ToArray();
        var clock = Stopwatch.StartNew(); double previous = 0, maximumGap = 0;
        var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; maximumGap = Math.Max(maximumGap, now - previous); previous = now; };
        timer.Start();
        try
        {
            await window.ReviewSessionCatalogAsync(sessions); window.ReviewUsePaging();
            var input = new InjectedInput(window); await input.ActivateAsync();
            for (int pass = 0; pass < 4; pass++) await input.WheelAsync(window.ReviewSessionSurface, pass % 2 == 0 ? -2880 : 2880, 65, 6);
            var list = window.ReviewSessionSurface;
            await input.ClickAsync(list);
            await WaitFor(() => window.ReviewSelectedRow is SessionRow selected && selected.Session.Id.StartsWith("injected-", StringComparison.Ordinal) && !window.ReviewTranscriptLoading, "A physical click immediately after fast wheel reversal must finish loading", 12000);
            Assert.Contains("Message 2399", window.ReviewTranscriptText);
            Assert.AreEqual(300, window.ReviewLoadedCount, "Selection must read a bounded page, not the entire transcript");
            Assert.IsLessThan(600d, maximumGap, "Fast wheel input plus selection must not stall the UI dispatcher");
            TestContext.WriteLine($"Prompt title length={prompt.Length}; maximum dispatcher gap={maximumGap:F1} ms; visible rows={list.Items.Count}; initial records={window.ReviewLoadedCount}");
            TestContext.AddResultFile(await window.CaptureReviewAsync("injected-prompt-wheel-click"));
        }
        finally { timer.Stop(); File.Delete(path); }
    }
    [UITestMethod, DataRow(false, false), DataRow(true, false), DataRow(true, true)]
    public async Task ContinuousOneDirectionWheelLoadsAllLocalHistoryWithoutGapsOrDispatcherStalls(bool compressed, bool tools)
    {
        var path = await HistoryAsync(2400, tools);
        if (compressed)
        {
            await Task.Run(async () =>
            {
                await using (var output = File.Create(path + ".zst"))
                await using (var encoder = new ZstdSharp.CompressionStream(output))
                await using (var source = File.OpenRead(path)) await source.CopyToAsync(encoder);
                File.Delete(path);
            });
        }
        try
        {
            var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Dark", "zh-CN");
            window.ReviewTranscriptFile(path); window.ReviewUsePaging(); await window.ReviewReloadTranscript(true); await MainWindow.SettleAsync();
            var input = new InjectedInput(window); await input.ActivateAsync();
            var clock = Stopwatch.StartNew(); double previous = 0, maximumGap = 0; int beats = 0;
            var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; maximumGap = Math.Max(maximumGap, now - previous); previous = now; beats++; };
            timer.Start();
            try
            {
                for (int batch = 0; batch < 30 && window.ReviewHasEarlier; batch++)
                {
                    var surface = tools ? window.ReviewVisualElements<ActivityGroupView>().Single().List : window.ReviewTranscriptSurface;
                    await input.WheelAsync(surface, 2880, 24, 8);
                    await Task.Delay(80);
                }
                await WaitFor(() => window.ReviewLoadedCount == 2400 && !window.ReviewHasEarlier, "Sustained wheel input must reach the oldest available record", 12000);
                var rows = window.ReviewMessageRows.SelectMany(r => r.Activities).ToArray();
                Assert.AreEqual(2400, rows.Select(e => e.Id).Distinct().Count());
                Assert.IsTrue(beats > 10); Assert.IsLessThan(600d, maximumGap, "Paging must not monopolize the UI dispatcher");
                TestContext.WriteLine($"Real wheel: compressed={compressed}, work={tools}; records={window.ReviewLoadedCount}; maximum dispatcher gap={maximumGap:F1} ms; beats={beats}");
                TestContext.AddResultFile(await window.CaptureReviewAsync($"injected-local-history-{compressed}-{tools}"));
            }
            finally { timer.Stop(); }
        }
        finally { File.Delete(path); File.Delete(path + ".zst"); }
    }

    [UITestMethod, DataRow("sessions"), DataRow("transcript"), DataRow("work")]
    public async Task EachSurfaceRespondsToKeyboardPrecisionWheelTouchAndScrollbarDrag(string kind)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        RepeaterList surface;
        if (kind == "sessions")
        { await window.ReviewSessionCatalogAsync(Sessions(10000)); surface = window.ReviewSessionSurface; }
        else
        {
            await window.ShowConversationPresentationReviewAsync(Enumerable.Range(0, 800).Select(i => kind == "work"
                ? new TranscriptEntry("input-work-" + i, EntryKind.Tool, "", default, Tool: new("shell", "echo " + i, "done", ToolActivityState.Completed))
                : new TranscriptEntry("input-message-" + i, EntryKind.Assistant, "Message " + i + ". A readable message with enough text to occupy several lines in the scroll viewport.", default)).ToArray(), false);
            surface = kind == "work" ? window.ReviewVisualElements<ActivityGroupView>().Single().List : window.ReviewTranscriptSurface;
        }
        var input = new InjectedInput(window); await input.ActivateAsync();
        var presses = new List<string>(); Microsoft.UI.Xaml.Input.PointerEventHandler trace = (_, e) => presses.Add($"{e.OriginalSource?.GetType().Name}@{e.GetCurrentPoint(window.ReviewRoot).Position}");
        window.ReviewRoot.AddHandler(UIElement.PointerPressedEvent, trace, true);
        FocusForKeys(surface);
        await input.KeyAsync(0x22, 20); await MainWindow.SettleAsync();
        Assert.IsTrue(surface.Scroll.VerticalOffset > 0, "Injected PageDown must move " + kind);
        FocusForKeys(surface); await input.KeyAsync(0x21, 40); await MainWindow.SettleAsync();
        Assert.AreEqual(0, surface.Scroll.VerticalOffset, 2, "Injected PageUp must reach the beginning of " + kind);
        FocusForKeys(surface); await input.KeyAsync(0x22, 3); await MainWindow.SettleAsync();
        Assert.IsTrue(surface.Scroll.VerticalOffset > 20, "Injected PageDown must move " + kind);
        var before = surface.Scroll.VerticalOffset;
        await input.WheelAsync(surface, -15, 32); await WaitForScrollIdle(surface.Scroll);
        Assert.IsTrue(surface.Scroll.VerticalOffset > before + 2, "Small high-resolution wheel deltas must move " + kind);
        before = surface.Scroll.VerticalOffset;
        await input.TouchSwipeAsync(surface, false); await WaitForScrollIdle(surface.Scroll);
        Assert.IsTrue(surface.Scroll.VerticalOffset > before + 2, "A real injected touch pan must move " + kind);
        input.Move(input.ScreenPoint(surface, .995, .5)); await Task.Delay(180);
        var thumb = Descendants(surface.Scroll).OfType<Thumb>().First(t => t.ActualHeight > 0 && t.ActualWidth > 0 && t.Name.Contains("Vertical", StringComparison.Ordinal));
        before = surface.Scroll.VerticalOffset;
        var from = input.ScreenPoint(thumb); var to = input.ScreenPoint(surface, .995, before > surface.Scroll.ScrollableHeight * .5 ? .1 : .9);
        await input.DragAsync(from, to); await WaitForScrollIdle(surface.Scroll);
        window.ReviewRoot.RemoveHandler(UIElement.PointerPressedEvent, trace);
        TestContext.WriteLine($"{kind}: thumb={thumb.Name}, from={from}, to={to}, before={before}, after={surface.Scroll.VerticalOffset}, extent={surface.Scroll.ScrollableHeight}");
        TestContext.WriteLine("Press targets: " + string.Join("; ", presses));
        TestContext.AddResultFile(await window.CaptureReviewAsync("injected-all-inputs-" + kind));
        Assert.IsTrue(Math.Abs(surface.Scroll.VerticalOffset - before) > 10, "An injected scrollbar drag must move " + kind);
        if (kind == "sessions") AssertCovered(surface);
        else Assert.IsTrue(surface.RealizedElements.Any(e => e.ActualHeight > 0 && e.Content is not null));
    }
    private static void FocusForKeys(RepeaterList surface)
    {
        var visible = surface.RealizedElements.First(e => { var y = e.TransformToVisual(surface.Scroll).TransformPoint(new(0,0)).Y; return y + e.ActualHeight > 0 && y < surface.Scroll.ViewportHeight; });
        var control = surface.SelectionMode == ListViewSelectionMode.Single ? visible : Descendants(visible).OfType<Control>().First(c => c.IsTabStop && c.IsEnabled && c.ActualWidth > 0 && c.ActualHeight > 0);
        Assert.IsTrue(control.Focus(FocusState.Programmatic), "The test's keyboard target must be focusable");
    }
}
