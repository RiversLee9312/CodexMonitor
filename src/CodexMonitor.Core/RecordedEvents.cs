using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMonitor.Core;

/// <summary>Legacy rollout event names mapped into the same presentation used by app-server.</summary>
public static class RecordedEvents
{
    public static RpcMessage Normalize(RpcMessage message)
    {
        var original = message.Parameters;
        if (original.ValueKind == JsonValueKind.Object && original.Field("threadId").ValueKind == JsonValueKind.String && !message.Method.StartsWith("codex/event/", StringComparison.Ordinal) && !message.Method.StartsWith("item.", StringComparison.Ordinal)) return message;
        var payload = original.Field("msg").ValueKind == JsonValueKind.Object ? original.Field("msg") : original;
        var method = message.Method.Replace("item.started", "item/started", StringComparison.Ordinal).Replace("item.updated", "item/started", StringComparison.Ordinal).Replace("item.completed", "item/completed", StringComparison.Ordinal);
        var type = payload.Str("type", method.Split('/').Last());
        if (method.StartsWith("codex/event/", StringComparison.Ordinal)) method = type switch
        {
            "agent_message" => "legacy/agentMessage", "agent_message_delta" => "item/agentMessage/delta",
            "agent_reasoning" => "legacy/reasoning", "agent_reasoning_raw_content" => "legacy/reasoningRaw", "agent_reasoning_delta" => "item/reasoning/summaryTextDelta", "agent_reasoning_raw_content_delta" => "item/reasoning/textDelta",
            "item_started" => "item/started", "item_completed" => "item/completed", "task_started" => "turn/started", "task_complete" or "turn_aborted" => "turn/completed",
            "plan_update" => "turn/plan/updated", "turn_diff" => "turn/diff/updated", "warning" => "warning", "error" => "error", _ => method
        };
        var p = payload.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(payload.GetRawText())! : new JsonObject();
        p["threadId"] = payload.Str("threadId", payload.Str("thread_id", original.Str("threadId", original.Str("conversationId", original.Str("thread_id", payload.Field("thread").Str("id"))))));
        if (!p.ContainsKey("itemId") && message.Method.StartsWith("codex/event/", StringComparison.Ordinal)) p["itemId"] = payload.Str("item_id", payload.Str("message_id", payload.Str("id", "legacy:" + payload.Str("turn_id"))));
        if (!p.ContainsKey("turnId") && payload.Field("turn_id").ValueKind != JsonValueKind.Undefined) p["turnId"] = payload.Str("turn_id");
        if (method is "turn/started" or "turn/completed" && !p.ContainsKey("turn")) p["turn"] = new JsonObject { ["id"] = payload.Str("turn_id"), ["status"] = type == "turn_aborted" ? "interrupted" : method == "turn/started" ? "inProgress" : "completed" };
        using var document = JsonDocument.Parse(p.ToJsonString());
        return message with { Method = method, Parameters = document.RootElement.Clone() };
    }
    public static TranscriptEntry? Parse(JsonElement p, string id, DateTimeOffset time)
    {
        var type = p.Str("type"); var message = p.Str("message", p.Str("text"));
        if (type is "agent_reasoning" or "agent_reasoning_raw_content")
        {
            var reasoning = type == "agent_reasoning" ? new ReasoningContent(message, "") : new ReasoningContent("", message);
            return new(id, EntryKind.Reasoning, reasoning.DisplayText, time, Reasoning: reasoning);
        }
        var method = type switch
        {
            "warning" => "warning", "error" or "stream_error" => "error", "deprecation_notice" => "deprecationNotice", "guardian_warning" => "guardianWarning",
            "plan_update" => "turn/plan/updated", "turn_diff" => "turn/diff/updated", "context_compacted" => "thread/compacted",
            "mcp_startup_update" or "mcp_startup_complete" => "mcpServer/startupStatus/updated", "hook_started" => "hook/started", "hook_completed" => "hook/completed",
            _ => null
        };
        if (method is not null) return TranscriptEvents.Parse(method, p, time) is { } entry ? entry with { Id = id } : null;
        if (type is "exec_command_begin" or "exec_command_end" or "mcp_tool_call_begin" or "mcp_tool_call_end" or "patch_apply_begin" or "patch_apply_end")
        {
            var name = type.StartsWith("exec_", StringComparison.Ordinal) ? "shell" : type.StartsWith("patch_", StringComparison.Ordinal) ? "apply_patch" : "mcp__" + p.Field("invocation").Str("server") + "__" + p.Field("invocation").Str("tool");
            var input = p.Field("command").ValueKind == JsonValueKind.Array ? string.Join(" ", p.Field("command").Items().Select(x => x.TextValue())) : p.Str("command", p.Field("invocation").Field("arguments").TextValue());
            var output = p.Str("aggregated_output", p.Str("stdout", p.Field("result").TextValue()));
            var tool = ToolActivity.FromCall(name, input, output, type.EndsWith("_end", StringComparison.Ordinal) ? ToolActivity.CompletedState(output) : ToolActivityState.Running);
            return new(id, EntryKind.Tool, input + "\n" + output, time, name, Tool: tool);
        }
        if (type.EndsWith("_delta", StringComparison.Ordinal) || type is "item_started" or "agent_reasoning_section_break" or "session_configured") return null;
        var original = TranscriptEvents.Sanitize(p);
        return new(id, EntryKind.Event, original, time, type, original, Tool: new(type, original, CategoryOverride: "ToolDetails"));
    }
}
