using System.Diagnostics.Metrics;

namespace CodexMonitor.Core;

internal static class HistoryMetrics
{
    private static readonly Meter Meter = new("CodexMonitor.History", "1.0");
    internal static readonly Counter<long> Decoded = Meter.CreateCounter<long>("rollout.decode.completed");
    internal static readonly Counter<long> DecodeBytes = Meter.CreateCounter<long>("rollout.decode.bytes");
    internal static readonly Counter<long> Parsing = Meter.CreateCounter<long>("transcript.parse.started");
    internal static readonly Counter<long> ParseBytes = Meter.CreateCounter<long>("transcript.parse.bytes");
}
