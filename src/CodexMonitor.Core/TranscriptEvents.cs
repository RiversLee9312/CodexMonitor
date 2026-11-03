using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMonitor.Core;

/// <summary>Read-only presentation of documented app-server notifications. Stable IDs coalesce progress.</summary>
public static class TranscriptEvents
{
    public static bool IsGlobal(string method) => method is "warning" or "configWarning" or "deprecationNotice" or "account/rateLimits/updated" or "mcpServer/startupStatus/updated" or "windows/worldWritableWarning";
    public static TranscriptEntry? Parse(string method, JsonElement p, DateTimeOffset time = default)
    {
        var key = method switch
        {
            "turn/plan/updated" => "ToolPlan", "turn/diff/updated" => "ToolFileChanges",
            "warning" or "configWarning" or "guardianWarning" or "deprecationNotice" or "windows/worldWritableWarning" => "EventWarning",
            "error" => p.Field("willRetry").ValueKind == JsonValueKind.True ? "EventRetrying" : "EventError",
            "turn/completed" when p.Field("turn").Str("status") == "failed" => "EventError",
            "turn/completed" when p.Field("turn").Str("status") == "interrupted" => "EventInterrupted",
            "hook/started" or "hook/completed" => "EventHook",
            "model/rerouted" => "EventModelChanged", "model/verification" => "EventVerification",
            "modelProvider/authRecoveryStarted" or "modelProvider/authRecoveryCompleted" => "EventAuthRecovery",
            "item/autoApprovalReview/started" or "item/autoApprovalReview/completed" or "autoApprovalReview/strictReviewRequired" or "serverRequest/resolved" => "EventAutoApprovalReview",
            "model/safetyBuffering/updated" when p.Field("showBufferingUi").ValueKind == JsonValueKind.True => "EventBuffering",
            "mcpServer/startupStatus/updated" => "EventMcpStatus", "thread/tokenUsage/updated" => "EventUsage",
            "account/rateLimits/updated" => "EventLimits", "thread/compacted" => "ToolCompaction", _ => null
        };
        if (key is null)
        {
            if (!ProtocolEventCatalog.Notifications.TryGetValue(method, out var definition))
            {
                var original = Sanitize(p);
                return new(Guid.NewGuid().ToString(), EntryKind.Event, original, time, method, original, Tool: new(method, original, State: ToolActivityState.Unknown, SummaryOverride: "", CategoryOverride: "ToolDetails"));
            }
            if (definition.Route == ProtocolEventRoute.Stream) return null;
            var safe = Sanitize(p);
            var scopeId = p.Str("threadId", p.Field("thread").Str("id"));
            return new(scopeId + ":" + method, EntryKind.Event, safe, time, method, Tool: new(method, safe, State: ToolActivityState.Unknown, SummaryOverride: p.Str("message", p.Str("summary", p.Str("threadName", p.Str("name")))), CategoryOverride: definition.Category));
        }
        var turn = p.Field("turn");
        var summary = key switch
        {
            "ToolPlan" => p.Str("explanation"),
            "EventError" or "EventRetrying" => p.Field("error").Str("message", turn.Field("error").Str("message", p.Str("message"))),
            "EventWarning" => p.Str("message", p.Str("summary")),
            "EventModelChanged" => p.Str("fromModel") + " → " + p.Str("toModel"),
            "EventMcpStatus" => p.Str("name", p.Str("serverName")) + " · " + p.Str("status"),
            "EventHook" => p.Field("run").Str("name", p.Field("run").Str("eventName")),
            "EventAuthRecovery" => p.Str("provider") + " · " + p.Str("message"),
            "EventAutoApprovalReview" => p.Field("review").Str("summary", p.Field("review").Str("reason")), _ => ""
        };
        var state = key == "EventError" ? ToolActivityState.Failed : method.EndsWith("/started", StringComparison.Ordinal) || method == "modelProvider/authRecoveryStarted" ? ToolActivityState.Running : method.EndsWith("/completed", StringComparison.Ordinal) || method is "modelProvider/authRecoveryCompleted" or "serverRequest/resolved" ? ToolActivityState.Completed : ToolActivityState.Unknown;
        var coalesce = method is "turn/plan/updated" or "turn/diff/updated" or "thread/tokenUsage/updated" or "account/rateLimits/updated" or "mcpServer/startupStatus/updated" or "model/safetyBuffering/updated";
        var scope = p.Str("turnId", turn.Str("id", p.Str("threadId")));
        var id = key == "EventAutoApprovalReview" ? "approval-review:" + p.Str("reviewId", p.Field("requestId").TextValue()) : key == "EventAuthRecovery" ? scope + ":auth:" + p.Str("provider") : method.StartsWith("hook/", StringComparison.Ordinal) ? "hook:" + p.Field("run").Str("id", Guid.NewGuid().ToString()) :
            coalesce ? scope + ":" + key + ":" + p.Str("serverName", p.Str("name")) : Guid.NewGuid().ToString();
        var input = method == "turn/diff/updated" ? p.Str("diff") : Sanitize(p);
        var tool = new ToolActivity(method, input, State: state, SummaryOverride: summary, CategoryOverride: key);
        return new(id, EntryKind.Event, input, time, key, Tool: tool);
    }
    public static string Sanitize(JsonElement value)
    {
        JsonNode? Read(JsonElement node, int depth)
        {
            if (depth > 8) return null;
            if (node.ValueKind == JsonValueKind.Object)
            {
                var fields = new JsonObject();
                foreach (var property in node.EnumerateObject())
                {
                    var name = property.Name.ToLowerInvariant();
                    if (name.Contains("encrypted", StringComparison.Ordinal) || name.Contains("password", StringComparison.Ordinal) || name.Contains("secret", StringComparison.Ordinal) || name.Contains("authorization", StringComparison.Ordinal) || name.Contains("cookie", StringComparison.Ordinal) || name.EndsWith("base64", StringComparison.Ordinal) || name is "access_token" or "refresh_token" or "id_token" or "accesstoken" or "refreshtoken" or "idtoken" or "audio" or "sdp") continue;
                    fields[property.Name] = Read(property.Value, depth + 1);
                }
                return fields;
            }
            if (node.ValueKind == JsonValueKind.Array) return new JsonArray(node.EnumerateArray().Take(100).Select(child => Read(child, depth + 1)).ToArray());
            return node.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : JsonNode.Parse(node.GetRawText());
        }
        return Read(value, 0)?.ToJsonString() ?? "";
    }
}
