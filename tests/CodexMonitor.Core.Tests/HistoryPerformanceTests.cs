using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZstdSharp;

namespace CodexMonitor.Core.Tests;

[TestClass, DoNotParallelize]
public sealed class HistoryPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private readonly List<string> _files = [];
    [TestCleanup] public void Cleanup() { foreach (var path in _files) File.Delete(path); }
    private async Task<SessionInfo> Fixture(int records = 100000)
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexMonitor-perf-" + Guid.NewGuid() + ".jsonl.zst"); _files.Add(path);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await using var compressed = new CompressionStream(file);
        await using var writer = new StreamWriter(compressed, Encoding.UTF8, 65536);
        for (int i = 0; i < records; i++) await writer.WriteLineAsync("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"text\":\"Message " + i + " " + new string('x', 800) + "\"}]}}");
        return new() { Id = Guid.NewGuid().ToString(), FilePath = path };
    }
    private static MeterListener Listen(Action<string,long> observe)
    {
        var listener = new MeterListener { InstrumentPublished = (i,l) => { if(i.Meter.Name == "CodexMonitor.History") l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<long>((i,n,_,_) => observe(i.Name,n)); listener.Start(); return listener;
    }
    [TestMethod]
    public async Task ActiveSnapshotSurvivesCacheEvictionAndRepeatedRequestsReuseTheParsedWindow()
    {
        var session = await Fixture(); long decodes = 0, parses = 0; var worker = true;
        using var listener = Listen((name,n) => { if(name == "rollout.decode.completed") Interlocked.Add(ref decodes,n); if(name == "transcript.parse.started") { Interlocked.Add(ref parses,n); worker &= Thread.CurrentThread.IsThreadPoolThread; } });
        await using var pager = new LocalTranscriptPager(retainSnapshot:true); var clock=Stopwatch.StartNew();
        var first = await pager.ReadAsync(session); var cold=clock.Elapsed; Assert.HasCount(300,first.Snapshot.Entries);
        var requests=Enumerable.Range(0,10).Select(_=>pager.ReadAsync(session)).ToArray(); await Task.WhenAll(requests);
        Assert.AreEqual(1L, decodes); Assert.AreEqual(1L,parses); Assert.IsTrue(requests.All(t=>ReferenceEquals(first,t.Result)));
        foreach(var other in new[]{await Fixture(600),await Fixture(600)}) _ = await new LocalTranscriptPager().ReadAsync(other);
        var builds=decodes; var start=first.StartOffset;
        for(int i=0;i<10;i++) { var next=await pager.ReadAsync(session,start,true); Assert.IsLessThan(start,next.StartOffset); Assert.AreEqual(next.Snapshot.Entries.Count,next.Snapshot.Entries.Select(e=>e.Id).Distinct().Count()); start=next.StartOffset; }
        Assert.AreEqual(builds,decodes,"An active conversation must not be decoded again after global cache eviction"); Assert.IsTrue(worker);
        TestContext.WriteLine($"100000 records (~90 MB JSONL): first page {cold.TotalMilliseconds:F1} ms; ten older windows completed in {clock.Elapsed.TotalMilliseconds:F1} ms total; decoder builds for active conversation: 1.");
    }
    [TestMethod]
    public async Task CancellingMidDecodeDoesNotCacheAPartialSnapshotAndCanRetry()
    {
        var session=await Fixture(); using var cancel=new CancellationTokenSource(); long decoded=0;
        using(var listener=Listen((name,n)=> {if(name=="rollout.decode.bytes" && Interlocked.Add(ref decoded,n)>=65536)cancel.Cancel();}))
            await Assert.ThrowsAsync<OperationCanceledException>(()=>new LocalTranscriptPager().ReadAsync(session,ct:cancel.Token));
        Assert.IsGreaterThan(0L,decoded);
        var result=await new LocalTranscriptPager().ReadAsync(session); Assert.HasCount(300,result.Snapshot.Entries); StringAssert.StartsWith(result.Snapshot.Entries[^1].Text,"Message 99999 ");
    }
    [TestMethod]
    public async Task DisposalCancelsAnInFlightReadAndRejectsFurtherPages()
    {
        var session=await Fixture(); var pager=new LocalTranscriptPager(true); Task? disposal=null;
        using(var listener=Listen((name,_)=> { if(name=="rollout.decode.bytes") Interlocked.CompareExchange(ref disposal,pager.DisposeAsync().AsTask(),null); }))
            await Assert.ThrowsAsync<OperationCanceledException>(()=>pager.ReadAsync(session));
        if(disposal is not null)await disposal;
        await Assert.ThrowsAsync<OperationCanceledException>(()=>pager.ReadAsync(session));
    }
}
