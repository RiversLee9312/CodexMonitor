using System.Text.Json;

namespace CodexMonitor.Core;

/// <summary>Responses stream compatibility. App-server item/started means execution, not argument generation.</summary>
public static class ResponseStream
{
    public static IReadOnlyList<TranscriptEntry> Apply(IReadOnlyList<TranscriptEntry> before, string method, JsonElement p, DateTimeOffset time, string? model, string? turnId)
    {
        var entries = before.ToList();
        var item = p.Field("item"); var id = p.Str("item_id", item.Str("id"));
        var index = entries.FindIndex(e => e.Id == id); var prior = index >= 0 ? entries[index] : null;
        void Put(TranscriptEntry entry)
        {
            entry = entry with { Model = entry.Model ?? prior?.Model ?? model, TurnId = entry.TurnId ?? prior?.TurnId ?? turnId, Timestamp = prior?.Timestamp ?? time };
            if (index < 0) entries.Add(entry); else entries[index] = entry;
        }
        void Pending(string name, string input, string kind)
        {
            if (id.Length == 0 || prior?.Tool is { State: not ToolActivityState.Generating }) return;
            Put(new(id, EntryKind.Tool, "", time, kind, Tool: new(name, input, State: ToolActivityState.Generating, SummaryOverride: "", CategoryOverride: "Working")));
        }
        if (method is "response.completed" or "response.failed" or "response.incomplete" or "response.cancelled" or "response.error")
        {
            entries.RemoveAll(e => e.Tool?.State == ToolActivityState.Generating && (turnId is null || e.TurnId == turnId));
            return entries.SequenceEqual(before) ? before : entries;
        }
        if (method == "response.output_item.added")
        {
            if (prior is not null) return before;
            if (item.Str("type") is "message" or "reasoning")
            {
                var textEntry = TranscriptItemAdapter.ParseResponse(item, id, time);
                if (textEntry is not null) Put(textEntry);
            }
            else Pending(item.Str("name", item.Str("type")), item.Field("arguments").TextValue(), item.Str("type"));
        }
        else if (method is "response.function_call_arguments.delta" or "response.custom_tool_call_input.delta" or "response.mcp_call_arguments.delta" or "response.code_interpreter_call_code.delta")
            Pending(prior?.Tool?.Name ?? "", (prior?.Tool?.Input ?? "") + p.Str("delta"), prior?.Label ?? "function_call");
        else if (method is "response.function_call_arguments.done" or "response.custom_tool_call_input.done")
        {
            if (prior?.Tool is not { State: ToolActivityState.Generating } pending) return before;
            var input = p.Str("arguments", p.Str("input", pending.Input));
            Put(prior with { Text = input, Tool = ToolActivity.FromCall(pending.Name, input, state: ToolActivityState.Unknown) });
        }
        else if (method == "response.output_item.done")
        {
            if (item.ValueKind != JsonValueKind.Object || id.Length == 0) return before;
            var parsed = TranscriptItemAdapter.ParseResponse(item, id, time);
            if (parsed is not null)
            {
                // A generated function call is ready, but the stream does not prove it was executed.
                if (item.Str("type") is "function_call" or "custom_tool_call" && parsed.Tool is { } tool) parsed = parsed with { Tool = tool with { State = ToolActivityState.Unknown } };
                Put(parsed);
            }
        }
        else if (method is "response.output_text.delta" or "response.output_text.done")
        {
            var value = method.EndsWith(".done", StringComparison.Ordinal) ? p.Str("text") : (prior?.Text ?? "") + p.Str("delta");
            Put(new(id, EntryKind.Assistant, value, time));
        }
        else if (method is "response.reasoning_summary_text.delta" or "response.reasoning_text.delta")
        {
            var reasoning = prior?.Reasoning ?? new ReasoningContent("", "");
            reasoning = method == "response.reasoning_summary_text.delta" ? reasoning with { Summary = reasoning.Summary + p.Str("delta") } : reasoning with { FullText = reasoning.FullText + p.Str("delta") };
            Put(new(id, EntryKind.Reasoning, reasoning.DisplayText, time, Reasoning: reasoning));
        }
        else if (id.Length > 0 && (method.EndsWith(".in_progress", StringComparison.Ordinal) || method.EndsWith(".generating", StringComparison.Ordinal) || method.EndsWith(".partial_image", StringComparison.Ordinal)))
            Pending(prior?.Tool?.Name ?? "", prior?.Tool?.Input ?? "", prior?.Label ?? method);
        return entries.SequenceEqual(before) ? before : entries;
    }
}
