using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodexMonitor.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class CompressedPerformanceUiTests
{
    public TestContext TestContext { get; set; } = null!;
    private readonly List<string> _files=[];
    [TestCleanup] public void Cleanup() { foreach(var path in _files)File.Delete(path); }
    private async Task<string> Fixture(int count)
    {
        var path=Path.Combine(Path.GetTempPath(),"CodexMonitor-ui-perf-"+Guid.NewGuid()+".jsonl.zst");_files.Add(path);
        await Task.Run(async ()=>
        {
            await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous);
            await using var encoder=new ZstdSharp.CompressionStream(file); await using var writer=new StreamWriter(encoder);
            var text=new string('x',800);
            for(int i=0;i<count;i++)await writer.WriteLineAsync("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"text\":\"History "+i+" "+text+"\"}]}}");
        }); return path;
    }
    private static MeterListener Listen(Action<string,long> callback)
    { var listener=new MeterListener{InstrumentPublished=(i,l)=>{if(i.Meter.Name=="CodexMonitor.History")l.EnableMeasurementEvents(i);}}; listener.SetMeasurementEventCallback<long>((i,n,_,_)=>callback(i.Name,n));listener.Start();return listener; }
    [UITestMethod]
    public async Task ColdDecodeAndRepeatedPagesLeaveTheDispatcherResponsiveAndKeepUniqueRows()
    {
        var path=await Fixture(100000);var window=TestApp.ReviewWindow;await window.SetReviewAsync(760,700,"Dark","zh-CN");window.ReviewTranscriptFile(path);window.ReviewUsePaging();
        int ticks=0;long builds=0;var workerOnly=true;var uiThread=Environment.CurrentManagedThreadId;var probe=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener=Listen((name,n)=>{if(name=="rollout.decode.completed")Interlocked.Add(ref builds,n);if(name=="rollout.decode.bytes"){workerOnly &= Environment.CurrentManagedThreadId!=uiThread;window.DispatcherQueue.TryEnqueue(()=>probe.TrySetResult());}if(name=="transcript.parse.started")workerOnly &= Environment.CurrentManagedThreadId!=uiThread;});
        var timer=window.DispatcherQueue.CreateTimer();timer.Interval=TimeSpan.FromMilliseconds(16);timer.Tick+=(_,_)=>ticks++;timer.Start();var watch=Stopwatch.StartNew();
        try
        {
            await window.ReviewReloadTranscript();Assert.IsTrue(probe.Task.IsCompletedSuccessfully);Assert.IsGreaterThan(0,ticks);Assert.AreEqual(300,window.ReviewLoadedCount);
            for(int i=0;i<3;i++) {await window.ReviewLoadEarlierAsync();await window.ReviewReloadTranscript();}
            Assert.AreEqual(1200,window.ReviewLoadedCount);Assert.AreEqual(1L,builds);Assert.IsTrue(workerOnly);
            Assert.AreEqual(window.ReviewMessageRows.Count,window.ReviewMessageRows.Select(r=>r.Entry.Id).Distinct().Count());
            TestContext.WriteLine($"100000 compressed records: {watch.Elapsed.TotalMilliseconds:F1} ms for cold load, three prepends and three same-window refreshes; dispatcher ticks {ticks}; one decode; all decode/parse callbacks off UI thread.");
        }
        finally {timer.Stop();}
        TestContext.AddResultFile(await window.CaptureReviewAsync("compressed-performance-dark"));
    }
    [UITestMethod]
    public async Task SwitchingWhileAnOlderPageParsesCancelsTheOldOwnerAndCannotOverwriteTheNewView()
    {
        var path=await Fixture(1200);var next=Path.Combine(Path.GetTempPath(),"CodexMonitor-next-"+Guid.NewGuid()+".jsonl");_files.Add(next);
        await File.WriteAllTextAsync(next,"{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"text\":\"New selection only\"}]}}\n");
        var window=TestApp.ReviewWindow;await window.SetReviewAsync(760,700,"Light","zh-CN");window.ReviewTranscriptFile(path);window.ReviewUsePaging();await window.ReviewReloadTranscript();
        var switched=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int scheduled=0;
        using var listener=Listen((name,_)=>{if(name=="transcript.parse.started"&&Interlocked.Exchange(ref scheduled,1)==0)window.DispatcherQueue.TryEnqueue(async()=>{try{window.ReviewTranscriptFile(next);await window.ReviewReloadTranscript();switched.TrySetResult();}catch(Exception e){switched.TrySetException(e);}});});
        await window.ReviewLoadEarlierAsync();await switched.Task.WaitAsync(TimeSpan.FromSeconds(10));await MainWindow.SettleAsync();
        Assert.AreEqual("New selection only",window.ReviewTranscriptText);Assert.IsFalse(window.ReviewLoadingEarlier);Assert.AreEqual(1,window.ReviewLoadedCount);
    }
}
