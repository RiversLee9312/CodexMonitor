using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMonitor.Core;

/// <summary>Both file records and app-server history/lifecycle notifications use this projection.</summary>
public static class TranscriptItemAdapter
{
    public static IReadOnlyList<string> SupportedTypes { get; } = new[] { "userMessage", "hookPrompt", "agentMessage", "functionCallOutput", "plan", "reasoning", "commandExecution", "fileChange", "mcpToolCall", "dynamicToolCall", "collabAgentToolCall", "collabToolCall", "subAgentActivity", "webSearch", "imageView", "sleep", "imageGeneration", "enteredReviewMode", "exitedReviewMode", "contextCompaction" };
    public static TranscriptEntry Parse(JsonElement item, DateTimeOffset timestamp = default, ToolActivityState? lifecycle = null)
    {
        item = Normalize(item);
        var type = item.Str("type"); var id = item.Str("id", Guid.NewGuid().ToString());
        if (type is "error" or "warning") return TranscriptEvents.Parse(type, item, timestamp)! with { Id = id };
        if ((type.Contains('_') || type == "message") && ParseResponse(item, id, timestamp) is { } response) return response;
        if (type == "reasoning")
        {
            var reasoning = ReasoningContent.Parse(item);
            return new(id, EntryKind.Reasoning, reasoning.DisplayText, timestamp, type, Reasoning: reasoning);
        }
        var kind = type switch
        {
            "userMessage" => EntryKind.User, "agentMessage" => EntryKind.Assistant,
            "commandExecution" or "fileChange" or "mcpToolCall" or "dynamicToolCall" or "webSearch" or "collabToolCall" or "collabAgentToolCall" or "imageView" or "imageGeneration" or "functionCallOutput" or "sleep" => EntryKind.Tool,
            _ => EntryKind.Event
        };
        var text = type switch
        {
            "userMessage" => string.Join("\n", item.Field("content").Items().Select(x => x.Str("text", x.Str("type")))),
            "agentMessage" or "plan" => item.Str("text"),
            "commandExecution" => item.Str("command") + "\n" + item.Str("aggregatedOutput"),
            _ => TranscriptEvents.Sanitize(item)
        };
        var tool = kind == EntryKind.Tool || type is "hookPrompt" or "subAgentActivity" or "plan" or "contextCompaction" or "enteredReviewMode" or "exitedReviewMode" ? ToolActivity.FromItem(item) : null;
        if (tool is { State: ToolActivityState.Unknown } && lifecycle is { } state) tool = tool with { State = state };
        return new(id, kind, text, timestamp, type, RawJson: SupportedTypes.Contains(type) ? null : text, Tool: tool);
    }
    public static JsonElement Normalize(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return item;
        var node = (JsonObject)JsonNode.Parse(item.GetRawText())!;
        var type = item.Str("type", item.Str("item_type"));
        type = type is "Extension" or "extension" ? item.Str("kind") : type;
        type = type.Length == 0 ? type : char.ToLowerInvariant(type[0]) + type[1..];
        type = type switch { "agent_message" when item.Field("author").ValueKind == JsonValueKind.Undefined => "agentMessage", "assistant_message" => "agentMessage", "command_execution" => "commandExecution", "file_change" => "fileChange", "mcp_tool_call" => "mcpToolCall", "web_search" => "webSearch", "todo_list" or "plan_update" => "plan", _ => type };
        node["type"] = type;
        foreach (var (from, to) in new[] { ("summary_text","summary"), ("raw_content","content"), ("aggregated_output","aggregatedOutput"), ("exit_code","exitCode"), ("parsed_cmd","commandActions"), ("process_id","processId"), ("agent_thread_id","agentThreadId"), ("agent_path","agentPath"), ("reasoning_effort","reasoningEffort"), ("server_name","server"), ("tool_name","tool") })
            if (!node.ContainsKey(to) && node[from] is { } value) node[to] = value.DeepClone();
        if (node["status"] is JsonValue status && status.TryGetValue<string>(out var statusText) && statusText.Length > 0) node["status"] = char.ToLowerInvariant(statusText[0]) + statusText[1..];
        if (!node.ContainsKey("durationMs") && item.Field("duration").Field("secs").TryNumber(out var seconds))
            node["durationMs"] = seconds * 1000 + (item.Field("duration").Field("nanos").TryNumber(out var nanos) ? nanos / 1000000 : 0);
        if (node["commandActions"] is JsonArray actions)
            foreach (var action in actions.OfType<JsonObject>()) if (action["type"] is JsonValue kind && kind.TryGetValue<string>(out var actionType) && actionType.Length > 0) action["type"] = char.ToLowerInvariant(actionType[0]) + actionType[1..];
        if (type == "agentMessage" && !node.ContainsKey("text")) node["text"] = PlainText(item.Field("content"));
        if (type == "plan" && !node.ContainsKey("plan") && item.Field("items").ValueKind == JsonValueKind.Array)
            node["plan"] = new JsonArray(item.Field("items").Items().Select(step => (JsonNode)new JsonObject { ["step"] = step.Str("text", step.Str("step")), ["status"] = step.Field("completed").ValueKind == JsonValueKind.True ? "completed" : step.Str("status", "pending") }).ToArray());
        using var normalized = JsonDocument.Parse(node.ToJsonString()); return normalized.RootElement.Clone();
    }
    public static string PlainText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        return string.Join("\n\n", content.Items().Where(p => p.ValueKind == JsonValueKind.String || p.Str("type") is "" or "text" or "Text" or "input_text" or "output_text").Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.Str("text")));
    }
    public static TranscriptEntry? ParseResponse(JsonElement item, string id, DateTimeOffset time)
    {
        var type = item.Str("type");
        TranscriptEntry Tool(ToolActivity tool, string? body = null) => new(id, EntryKind.Tool, body ?? tool.Input, time, tool.Name, Tool: tool);
        switch (type)
        {
            case "message":
                var body = PlainText(item.Field("content"));
                if (item.Str("role") == "assistant" && item.Str("channel") == "analysis") return new(id, EntryKind.Reasoning, body, time, Reasoning: new("", body));
                return item.Str("role") is "user" or "assistant" ? new(id, item.Str("role") == "user" ? EntryKind.User : EntryKind.Assistant, body, time) : null;
            case "reasoning":
                var reasoning = ReasoningContent.Parse(item);
                return reasoning.DisplayText.Length == 0 ? null : new(id, EntryKind.Reasoning, reasoning.DisplayText, time, Reasoning: reasoning);
            case "function_call": case "custom_tool_call": case "multi_agent_call":
                var name = item.Str("name", item.Str("action", "Tool")); var scope = item.Str("namespace");
                if (scope.Length > 0 && !name.StartsWith(scope + ".", StringComparison.Ordinal)) name = scope + "." + name;
                var call = ToolActivity.FromCall(name, item.Field("arguments").TextValue() is { Length: > 0 } args ? args : item.Str("input"));
                if (type == "multi_agent_call") call = call with { CategoryOverride = "ToolCollaboration" };
                return Tool(call);
            case "agent_message" when item.Field("author").ValueKind == JsonValueKind.Undefined && item.Field("recipient").ValueKind == JsonValueKind.Undefined:
                return new(id, EntryKind.Assistant, item.Str("message", item.Str("text", PlainText(item.Field("content")))), time);
            case "agent_message":
                var message = PlainText(item.Field("content"));
                var input = new JsonObject { ["author"] = item.Str("author"), ["recipient"] = item.Str("recipient"), ["message"] = message }.ToJsonString();
                return new(id, EntryKind.Event, message, time, type, Tool: new(type, input, State: ToolActivityState.Completed, SummaryOverride: item.Str("author") + " → " + item.Str("recipient"), CategoryOverride: "AgentMessage"));
            case "function_call_output": case "custom_tool_call_output": case "multi_agent_call_output":
                return new(id, EntryKind.Tool, item.Field("output").ValueKind == JsonValueKind.Array ? PlainText(item.Field("output")) : item.Field("output").TextValue(), time, type);
            case "local_shell_call":
                var action = item.Field("action");
                return Tool(ToolActivity.FromCall("shell", string.Join(" ", action.Field("command").Items().Select(x => x.TextValue()))));
            case "tool_search_call":
                return Tool(new(type, item.Field("arguments").TextValue(), CategoryOverride: "ToolSearch"));
            case "tool_search_output": case "additional_tools":
                var names = string.Join("\n", item.Field("tools").Items().Select(x => x.Str("name", x.Field("function").Str("name"))).Where(x => x.Length > 0));
                return new(id, EntryKind.Event, names, time, type, Tool: new(type, names, State: ToolActivityState.Completed, SummaryOverride: "", CategoryOverride: type == "tool_search_output" ? "ToolSearch" : "ToolDynamic"));
            case "web_search_call": case "image_generation_call":
                return Tool(ToolActivity.FromItem(item));
            case "compaction": case "context_compaction": case "compaction_trigger":
                return new(id, EntryKind.Event, "", time, type, Tool: new(type, "", State: ToolActivityState.Completed, CategoryOverride: "ToolCompaction", SummaryOverride: ""));
            case "configuration_update":
                return new(id, EntryKind.Event, "", time, type, Tool: new(type, item.Field("reasoning").TextValue(), State: ToolActivityState.Completed, CategoryOverride: "Settings", SummaryOverride: ""));
            default:
                if (SupportedTypes.Contains(Normalize(item).Str("type"))) return Parse(item, time) with { Id = id };
                var original = TranscriptEvents.Sanitize(item);
                return new(id, EntryKind.Event, original, time, type, original);
        }
    }
}
