using System.Text.Json;

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
        var calls = new Dictionary<string, int>();
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
                            if (payload.Str("type") is "function_call_output" or "custom_tool_call_output" && calls.TryGetValue(callId, out var callIndex))
                                entries[callIndex] = entries[callIndex] with { Text = entries[callIndex].Text + "\n\n" + entry.Text };
                            else
                            {
                                if (callId.Length > 0 && payload.Str("type") is "function_call" or "custom_tool_call") calls[callId] = entries.Count;
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
                                entries.Add(new(id, EntryKind.Event, payload.Str("message", payload.GetRawText()), time, "Error"));
                                break;
                            case "token_count":
                                var usage = payload.Field("info").Field("total_token_usage").Field("total_tokens");
                                if (usage.TryNumber(out var count)) tokens = count;
                                break;
                            case "user_message": case "agent_message":
                                fallbackMessages.Add(new(id, payload.Str("type") == "user_message" ? EntryKind.User : EntryKind.Assistant,
                                    payload.Str("message"), time));
                                break;
                        }
                        break;
                    case "token_usage_record":
                        if (payload.Field("total_tokens").TryNumber(out var total)) tokens = total;
                        break;
                    case "compacted":
                        entries.Add(new(id, EntryKind.Event, payload.Str("message", payload.GetRawText()), time, "compacted"));
                        break;
                    case "world_state": case "thread_settings": case "context_window":
                        break;
                    default:
                        unknown++;
                        entries.Add(new(id, EntryKind.Event, line, time, root.Str("type"), line));
                        break;
                }
            }
            catch (JsonException) { invalid++; } // An incomplete final line is retried on the next refresh.
        }
        // Older CLI builds recorded only event messages; response items are authoritative when present.
        if (!entries.Any(e => e.Kind is EntryKind.User or EntryKind.Assistant)) entries.AddRange(fallbackMessages);
        session = session with { State = state, ActiveTurnId = turnId };
        return new(session, entries.OrderBy(e => e.Timestamp).ToArray(), invalid, unknown, tokens);
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
    {
        var type = p.Str("type");
        string Content(JsonElement content) => string.Join("\n", content.Items().Select(x => x.Str("text", x.Str("type") == "input_image" ? "[input_image]" : "")).Where(x => x.Length > 0));
        return type switch
        {
            "message" when p.Str("role") is "user" or "assistant" => new(id, p.Str("role") == "user" ? EntryKind.User : EntryKind.Assistant, Content(p.Field("content")), timestamp),
            "function_call" or "custom_tool_call" => new(id, EntryKind.Tool, p.Str("arguments", p.Str("input")), timestamp, p.Str("name", "Tool")),
            "function_call_output" or "custom_tool_call_output" => new(id, EntryKind.Tool, p.Field("output").TextValue(), timestamp, "Tool"),
            "reasoning" when p.Field("summary").Items().Any() => new(id, EntryKind.Reasoning, Content(p.Field("summary")), timestamp),
            "message" or "reasoning" => null,
            _ => new(id, EntryKind.Event, p.GetRawText(), timestamp, type, p.GetRawText())
        };
    }
}
