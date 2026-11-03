using System.Text.Json;

namespace CodexMonitor.Core;

/// <summary>Read-only presentation of documented app-server notifications. Stable IDs coalesce progress.</summary>
public static class TranscriptEvents
{
    public static TranscriptEntry? Parse(string method, JsonElement p, DateTimeOffset time = default)
    {
        var key = method switch
        {
            "turn/plan/updated" => "ToolPlan", "turn/diff/updated" => "ToolFileChanges",
            "warning" or "configWarning" => "EventWarning",
            "error" => p.Field("willRetry").ValueKind == JsonValueKind.True ? "EventRetrying" : "EventError",
            "turn/completed" when p.Field("turn").Str("status") == "failed" => "EventError",
            "turn/completed" when p.Field("turn").Str("status") == "interrupted" => "EventInterrupted",
            "hook/started" or "hook/completed" => "EventHook",
            "model/rerouted" => "EventModelChanged", "model/verification" => "EventVerification",
            "model/safetyBuffering/updated" when p.Field("showBufferingUi").ValueKind == JsonValueKind.True => "EventBuffering",
            "mcpServer/startupStatus/updated" => "EventMcpStatus", "thread/tokenUsage/updated" => "EventUsage",
            "account/rateLimits/updated" => "EventLimits", "thread/compacted" => "ToolCompaction", _ => null
        };
        if (key is null) return null;
        var turn = p.Field("turn");
        var summary = key switch
        {
            "ToolPlan" => p.Str("explanation"),
            "EventError" or "EventRetrying" => p.Field("error").Str("message", turn.Field("error").Str("message", p.Str("message"))),
            "EventWarning" => p.Str("message", p.Str("summary")),
            "EventModelChanged" => p.Str("fromModel") + " → " + p.Str("toModel"),
            "EventMcpStatus" => p.Str("name", p.Str("serverName")) + " · " + p.Str("status"),
            "EventHook" => p.Field("run").Str("name", p.Field("run").Str("eventName")), _ => ""
        };
        var state = key == "EventError" ? ToolActivityState.Failed : method == "hook/started" ? ToolActivityState.Running : ToolActivityState.Unknown;
        var coalesce = method is "turn/plan/updated" or "turn/diff/updated" or "thread/tokenUsage/updated" or "account/rateLimits/updated" or "mcpServer/startupStatus/updated" or "model/safetyBuffering/updated";
        var scope = p.Str("turnId", turn.Str("id", p.Str("threadId")));
        var id = method.StartsWith("hook/", StringComparison.Ordinal) ? "hook:" + p.Field("run").Str("id", Guid.NewGuid().ToString()) :
            coalesce ? scope + ":" + key + ":" + p.Str("serverName", p.Str("name")) : Guid.NewGuid().ToString();
        var input = method == "turn/diff/updated" ? p.Str("diff") : p.GetRawText();
        var tool = new ToolActivity(method, input, State: state, SummaryOverride: summary, CategoryOverride: key);
        return new(id, EntryKind.Event, input, time, key, Tool: tool);
    }
}
