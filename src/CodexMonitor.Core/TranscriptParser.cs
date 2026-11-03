using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexMonitor.Core;

/// <summary>Reads append-only rollout logs. Never writes session data or exposes encrypted reasoning.</summary>
public sealed class TranscriptParser
{
    public async Task<TranscriptSnapshot> ReadAsync(SessionInfo session, CancellationToken ct = default)
    {
        await using var stream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var entries = new List<TranscriptEntry>();
        var fallbackMessages = new List<TranscriptEntry>();
        var nativeItems = new List<TranscriptEntry>();
        var wireIds = new Dictionary<string, string>();
        var calls = new Dictionary<string, int>();
        var drafts = new Dictionary<string, TranscriptEntry>();
        var legacyTools = new Dictionary<string, int>();
        int invalid = 0, unknown = 0, ordinal = 0;
        long tokens = 0;
        string? turnId = null;
        var state = SessionState.Unknown;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            ordinal++;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var payload = root.Field("payload");
                var time = root.Date("timestamp");
                var id = $"{session.Id}:{ordinal}";
                switch (root.Str("type"))
                {
                    case "session_meta":
                        session = Metadata(session, payload);
                        break;
                    case "turn_context":
                        session = session with { Model = payload.Str("model", session.Model) };
                        break;
                    case "response_item":
                        var entry = ParseResponseItem(payload, id, time);
                        if (entry is not null)
                        {
                            var callId = payload.Str("call_id");
                            if (payload.Str("type") is "function_call_output" or "custom_tool_call_output" or "multi_agent_call_output" && calls.TryGetValue(callId, out var callIndex))
                            {
                                var call = entries[callIndex];
                                entries[callIndex] = call with { Text = call.Text + "\n\n" + entry.Text,
                                    Tool = call.Tool is { } tool ? tool with { Output = entry.Text, State = ToolActivity.CompletedState(entry.Text) } : null };
                            }
                            else
                            {
                                if (callId.Length > 0 && payload.Str("type") is "function_call" or "custom_tool_call" or "multi_agent_call") calls[callId] = entries.Count;
                                wireIds[entry.Id] = callId.Length > 0 ? callId : payload.Str("id");
                                entries.Add(entry);
                            }
                        }
                        break;
                    case "event_msg":
                        switch (payload.Str("type"))
                        {
                            case "task_started": case "turn_started":
                                state = SessionState.RecordedActive;
                                turnId = payload.Str("turn_id");
                                break;
                            case "task_complete": case "turn_complete":
                                state = SessionState.Completed; turnId = null; break;
                            case "turn_aborted": case "task_interrupted":
                                state = SessionState.Interrupted; turnId = null; break;
                            case "error":
                                state = SessionState.Error;
                                entries.Add(TranscriptEvents.Parse("error", payload, time)! with { Id = id });
                                break;
                            case "warning":
                                entries.Add(TranscriptEvents.Parse("warning", payload, time)! with { Id = id });
                                break;
                            case "token_count":
                                var usage = payload.Field("info").Field("total_token_usage").Field("total_tokens");
                                if (usage.TryNumber(out var count)) tokens = count;
                                break;
                            case "item_completed":
                                var native = TranscriptItemAdapter.Parse(payload.Field("item"), time, ToolActivityState.Completed);
                                nativeItems.Add(native); break;
                            case "user_message": case "agent_message":
                                var draftKey = (turnId ?? "") + ":assistant";
                                if (payload.Str("type") == "agent_message") drafts.Remove(draftKey);
                                fallbackMessages.Add(new(id, payload.Str("type") == "user_message" ? EntryKind.User : EntryKind.Assistant,
                                    payload.Str("message"), time));
                                break;
                            case "agent_message_delta": case "agent_reasoning_delta": case "agent_reasoning_raw_content_delta":
                                var draftKind = payload.Str("type") == "agent_message_delta" ? "assistant" : payload.Str("type") == "agent_reasoning_delta" ? "summary" : "thinking";
                                var key = (turnId ?? "") + ":" + draftKind;
                                var draftText = (drafts.TryGetValue(key, out var draft) ? draft.Text : "") + payload.Str("delta");
                                drafts[key] = new(draft?.Id ?? id, draftKind == "assistant" ? EntryKind.Assistant : EntryKind.Reasoning, draftText, draft?.Timestamp ?? time,
                                    Reasoning: draftKind == "assistant" ? null : draftKind == "summary" ? new(draftText, "") : new("", draftText));
                                break;
                            default:
                                if (payload.Str("type") is "agent_reasoning" or "agent_reasoning_raw_content") drafts.Remove((turnId ?? "") + (payload.Str("type") == "agent_reasoning" ? ":summary" : ":thinking"));
                                if (RecordedEvents.Parse(payload, id, time) is { } recorded)
                                {
                                    var legacyCall = payload.Str("call_id");
                                    if (recorded.Tool is not null && legacyCall.Length > 0 && legacyTools.TryGetValue(legacyCall, out var index))
                                    {
                                        var prior = fallbackMessages[index];
                                        var tool = recorded.Tool.Input.Length == 0 ? recorded.Tool with { Input = prior.Tool!.Input } : recorded.Tool;
                                        fallbackMessages[index] = recorded with { Id = prior.Id, Timestamp = prior.Timestamp, Tool = tool };
                                    }
                                    else { if (recorded.Tool is not null && legacyCall.Length > 0) legacyTools[legacyCall] = fallbackMessages.Count; fallbackMessages.Add(recorded); }
                                }
                                break;
                        }
                        break;
                    case "token_usage_record":
                        if (payload.Field("total_tokens").TryNumber(out var total)) tokens = total;
                        break;
                    case "compacted":
                        entries.Add(new(id, EntryKind.Event, payload.Str("message", payload.GetRawText()), time, "compacted",
                            Tool: new("contextCompaction", payload.Str("message"), State: ToolActivityState.Completed, SummaryOverride: "", CategoryOverride: "ToolCompaction")));
                        break;
                    case "item.started": case "item.updated": case "item.completed":
                        nativeItems.Add(TranscriptItemAdapter.Parse(root.Field("item"), time, root.Str("type") == "item.completed" ? ToolActivityState.Completed : ToolActivityState.Running)); break;
                    case "turn.started": state = SessionState.RecordedActive; break;
                    case "turn.completed": state = SessionState.Completed; turnId = null; break;
                    case "turn.failed": state = SessionState.Error; turnId = null; break;
                    case "thread.started": session = session with { Id = root.Str("thread_id", session.Id) }; break;
                    case "error": state = SessionState.Error; entries.Add(TranscriptEvents.Parse("error", root, time)! with { Id = id }); break;
                    case "world_state": case "thread_settings": case "context_window": case "inter_agent_communication_metadata":
                        break;
                    default:
                        unknown++;
                        var original = TranscriptEvents.Sanitize(root);
                        entries.Add(new(id, EntryKind.Event, original, time, root.Str("type"), original));
                        break;
                }
            }
            catch (JsonException) { invalid++; } // An incomplete final line is retried on the next refresh.
        }
        // Native completed items carry richer lifecycle data. Match mirrors by wire ID first, then consume
        // textual mirrors one-for-one so repeated identical messages in a conversation remain distinct.
        var nativeById = nativeItems.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last());
        var usedNative = new HashSet<string>();
        var matchedEntries = new HashSet<int>();
        for (int i = 0; i < entries.Count; i++)
            if (wireIds.TryGetValue(entries[i].Id, out var wireId) && nativeById.TryGetValue(wireId, out var mirror))
            { usedNative.Add(mirror.Id); matchedEntries.Add(i); entries[i] = mirror with { Id = entries[i].Id, Timestamp = entries[i].Timestamp }; }
        var mirrorTargets = entries.Select((entry, index) => (entry, index)).Where(pair => !matchedEntries.Contains(pair.index)).GroupBy(pair => MirrorKey(pair.entry)).ToDictionary(group => group.Key, group => new Queue<int>(group.Select(pair => pair.index)));
        foreach (var native in nativeById.Values.Where(entry => !usedNative.Contains(entry.Id)))
        {
            if (!mirrorTargets.TryGetValue(MirrorKey(native), out var targets) || !targets.TryDequeue(out var index)) continue;
            usedNative.Add(native.Id);
            if (entries[index].Tool is { } original && native.Tool is { } final)
                entries[index] = entries[index] with { Tool = original with { DurationMs = final.DurationMs ?? original.DurationMs, Output = original.Output ?? final.Output,
                    State = final.State == ToolActivityState.Unknown || original.State == ToolActivityState.Failed ? original.State : final.State,
                    CategoryOverride = final.CategoryOverride ?? original.CategoryOverride, SummaryOverride = final.SummaryOverride ?? original.SummaryOverride } };
        }
        entries.AddRange(nativeById.Values.Where(entry => !usedNative.Contains(entry.Id)));
        var copies = entries.GroupBy(MirrorKey).ToDictionary(g => g.Key, g => g.Count());
        foreach (var candidate in fallbackMessages.Concat(drafts.Values))
        {
            var key = MirrorKey(candidate);
            if (copies.TryGetValue(key, out var remaining) && remaining > 0) { copies[key]--; continue; }
            entries.Add(candidate);
        }
        session = session with { State = state, ActiveTurnId = turnId };
        return new(session, entries.OrderBy(e => e.Timestamp).ToArray(), invalid, unknown, tokens);
    }
    private static string MirrorKey(TranscriptEntry entry)
    {
        if (entry.Tool is not { } tool) return entry.Kind + ":" + entry.Text;
        if (tool.CategoryKey == "ToolFileChanges")
        {
            var edits = new List<string>();
            var patch = tool.DecodedInput;
            var headers = Regex.Matches(patch, @"^\*\*\* (?:Update|Add|Delete) File:\s*([^\r\n]+)", RegexOptions.Multiline);
            for (int i = 0; i < headers.Count; i++) Add(headers[i].Groups[1].Value, patch[headers[i].Index..(i + 1 < headers.Count ? headers[i + 1].Index : patch.Length)]);
            if (edits.Count == 0)
            {
                try { using var changes = JsonDocument.Parse(tool.Input); foreach (var change in changes.RootElement.Items()) Add(change.Str("path"), change.Str("diff")); }
                catch (JsonException) { }
            }
            if (edits.Count > 0) return "files:" + string.Join("\n", edits.Order(StringComparer.Ordinal));
            void Add(string path, string diff) => edits.Add(path.Trim().Replace('\\', '/') + ":" + string.Join("\n", diff.Replace("\r", "").Split('\n').Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))));
        }
        var category = tool.Name.Split('.').Last() is "exec" or "exec_command" or "shell" or "commandExecution" ? "command" : tool.CategoryKey;
        return "tool:" + category + ":" + tool.DecodedInput;
    }

    public static SessionInfo Metadata(SessionInfo session, JsonElement p)
    {
        var source = p.Field("source");
        var spawn = source.Field("subagent").Field("thread_spawn");
        var parent = spawn.Str("parent_thread_id", p.Str("parent_thread_id"));
        return session with
        {
            Id = p.Str("id", p.Str("session_id", session.Id)), Cwd = p.Str("cwd", session.Cwd),
            Source = source.ValueKind == JsonValueKind.String ? source.GetString()! : source.ValueKind == JsonValueKind.Object ? "subAgent" : "unknown",
            ParentId = string.IsNullOrEmpty(parent) ? session.ParentId : parent,
            CreatedAt = p.Date("timestamp", session.CreatedAt), Model = p.Str("model", session.Model)
        };
    }

    public static TranscriptEntry? ParseResponseItem(JsonElement p, string id, DateTimeOffset timestamp)
        => TranscriptItemAdapter.ParseResponse(p, id, timestamp);
}
