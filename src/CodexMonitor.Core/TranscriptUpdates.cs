using System.Text.Json;

namespace CodexMonitor.Core;

/// <summary>One reducer for live events and recorded event envelopes. Final items replace streamed drafts.</summary>
public static class TranscriptUpdates
{
    public static IReadOnlyList<TranscriptEntry> Apply(IReadOnlyList<TranscriptEntry> before, string method, JsonElement p, DateTimeOffset time = default, string? model = null, string? turnId = null)
    {
        if (method.StartsWith("response.", StringComparison.Ordinal)) return ResponseStream.Apply(before, method, p, time, model, turnId);
        var entries = before.ToList(); var changed = false;
        void Upsert(TranscriptEntry entry)
        {
            var index = entries.FindIndex(e => e.Id == entry.Id);
            entry = entry with { TurnId = entry.TurnId ?? (index >= 0 ? entries[index].TurnId : null) ?? p.Str("turnId", p.Field("turn").Str("id", turnId ?? "")),
                Model = entry.Model ?? (p.Field("item").Str("model") is { Length: > 0 } actualModel ? actualModel : null) ?? (index >= 0 ? entries[index].Model : null) ?? (entry.Kind == EntryKind.Assistant && entry.Text.Length == 0 ? null : model) };
            if (index >= 0 && entry.Tool is { } incoming && entries[index].Tool?.StandardInput is { } stdin) entry = entry with { Tool = incoming with { StandardInput = stdin } };
            if (index < 0) entries.Add(entry); else entries[index] = entry with { Timestamp = entries[index].Timestamp };
            changed = true;
        }
        var id = p.Str("itemId", p.Str("item_id")); var prior = entries.FirstOrDefault(e => e.Id == id);
        void RealtimeText(JsonElement payload, bool complete, bool start = false)
        {
            var explicitId = payload.Str("itemId", payload.Str("id"));
            var previous = explicitId.Length > 0 ? entries.FirstOrDefault(e => e.Id == explicitId) : null;
            var kind = payload.Str("role") == "user" ? EntryKind.User : previous?.Kind ?? EntryKind.Assistant;
            previous ??= start ? null : entries.LastOrDefault(e => e.Label == "realtime:active" && e.Kind == kind);
            var realtimeId = explicitId.Length > 0 ? explicitId : previous?.Id ?? "realtime:" + Guid.NewGuid();
            var text = complete || start ? payload.Str("text") : (previous?.Text ?? "") + payload.Str("delta");
            Upsert(new(realtimeId, kind, text, previous?.Timestamp ?? time, complete ? "realtime:complete" : "realtime:active"));
        }
        switch (method)
        {
            case "legacy/agentMessage":
                Upsert(new(id, EntryKind.Assistant, p.Str("message", p.Str("text")), prior?.Timestamp ?? time)); break;
            case "legacy/reasoning":
                var summary = p.Str("text", p.Str("message")); Upsert(new(id, EntryKind.Reasoning, summary, prior?.Timestamp ?? time, Reasoning: new(summary, ""))); break;
            case "legacy/reasoningRaw":
                var full = p.Str("text", p.Str("message")); Upsert(new(id, EntryKind.Reasoning, full, prior?.Timestamp ?? time, Reasoning: new("", full))); break;
            case "item/started": case "item/completed":
                var entry = TranscriptItemAdapter.Parse(p.Field("item"), time, method == "item/started" ? ToolActivityState.Running : ToolActivityState.Completed);
                Upsert(entry); break;
            case "item/agentMessage/delta":
                Upsert(new(id, EntryKind.Assistant, (prior?.Text ?? "") + p.Str("delta"), prior?.Timestamp ?? time)); break;
            case "item/reasoning/summaryTextDelta": case "item/reasoning/textDelta":
                var reasoning = prior?.Reasoning ?? new ReasoningContent(prior?.Text ?? "", "");
                reasoning = method == "item/reasoning/summaryTextDelta" ? reasoning with { Summary = reasoning.Summary + p.Str("delta") } : reasoning with { FullText = reasoning.FullText + p.Str("delta") };
                Upsert(new(id, EntryKind.Reasoning, reasoning.DisplayText, prior?.Timestamp ?? time, Reasoning: reasoning)); break;
            case "item/reasoning/summaryPartAdded":
                if (prior?.Reasoning is { Summary.Length: > 0 } part && !part.Summary.EndsWith('\n'))
                { part = part with { Summary = part.Summary + "\n\n" }; Upsert(prior with { Text = part.DisplayText, Reasoning = part }); }
                break;
            case "item/plan/delta":
                var plan = (prior?.Tool?.Input ?? "") + p.Str("delta");
                Upsert(new(id, EntryKind.Event, plan, prior?.Timestamp ?? time, "plan", Tool: new("plan", plan, State: ToolActivityState.Running, SummaryOverride: plan, CategoryOverride: "ToolPlan"))); break;
            case "item/commandExecution/outputDelta": case "item/fileChange/outputDelta":
                var tool = prior?.Tool ?? new ToolActivity(method.Contains("fileChange", StringComparison.Ordinal) ? "fileChange" : "commandExecution", "");
                var output = (tool.Output ?? "") + p.Str("delta");
                Upsert(new(id, EntryKind.Tool, tool.Input + "\n" + output, prior?.Timestamp ?? time, Tool: tool with { Output = output })); break;
            case "item/fileChange/patchUpdated":
                var patch = prior?.Tool ?? new("fileChange", "");
                patch = patch with { Input = p.Field("changes").TextValue(), CategoryOverride = "ToolFileChanges" };
                Upsert(new(id, EntryKind.Tool, patch.Input, prior?.Timestamp ?? time, Tool: patch)); break;
            case "item/mcpToolCall/progress":
                var mcp = prior?.Tool ?? new("mcpToolCall", "", CategoryOverride: "ToolMcp");
                if (mcp.State is ToolActivityState.Completed or ToolActivityState.Failed or ToolActivityState.Interrupted) break;
                Upsert(new(id, EntryKind.Tool, prior?.Text ?? "", prior?.Timestamp ?? time, Tool: mcp with { Progress = p.Str("message") })); break;
            case "item/commandExecution/terminalInteraction":
                var command = prior?.Tool ?? new("commandExecution", "");
                Upsert(new(id, EntryKind.Tool, prior?.Text ?? "", prior?.Timestamp ?? time, Tool: command with { StandardInput = (command.StandardInput ?? "") + p.Str("stdin") })); break;
            case "model/safetyBuffering/updated" when p.Field("showBufferingUi").ValueKind == JsonValueKind.False:
                changed = entries.RemoveAll(e => e.Tool?.CategoryKey == "EventBuffering") > 0; break;
            case "thread/realtime/itemAdded": case "thread/realtime/item/started": case "thread/realtime/item/completed":
                var realtime = p.Field("item");
                if (realtime.Str("type") is "message" or "agentMessage" or "userMessage") Upsert(TranscriptItemAdapter.Parse(realtime, time));
                else if (realtime.Str("type") == "transcriptSegment") RealtimeText(realtime, method.EndsWith("/completed", StringComparison.Ordinal), method.EndsWith("/started", StringComparison.Ordinal));
                else
                {
                    var safe = TranscriptEvents.Sanitize(realtime);
                    var state = method.EndsWith("/started", StringComparison.Ordinal) ? ToolActivityState.Running : ToolActivityState.Completed;
                    Upsert(new("realtime-state:" + realtime.Str("type"), EntryKind.Event, "", time, method, Tool: new(method, safe, State: state, CategoryOverride: "EventRealtime", SummaryOverride: "")));
                }
                break;
            case "thread/realtime/transcript/delta": case "thread/realtime/transcript/done": case "thread/realtime/item/transcript/delta":
                RealtimeText(p, method.EndsWith("/done", StringComparison.Ordinal)); break;
        }
        if (!method.StartsWith("legacy/", StringComparison.Ordinal) && TranscriptEvents.Parse(method, p, time) is { } activity) Upsert(activity);
        if (method == "turn/completed") changed |= entries.RemoveAll(e => e.Tool?.State == ToolActivityState.Generating && (turnId is null || e.TurnId == turnId)) > 0;
        if (method == "turn/completed" && p.Field("turn").Str("status") is "interrupted" or "failed")
        {
            for (int i = 0; i < entries.Count; i++) if (entries[i].Tool is { State: ToolActivityState.Running } running)
            { entries[i] = entries[i] with { Tool = running with { State = p.Field("turn").Str("status") == "interrupted" ? ToolActivityState.Interrupted : ToolActivityState.Failed } }; changed = true; }
        }
        return changed ? entries : before;
    }
}
