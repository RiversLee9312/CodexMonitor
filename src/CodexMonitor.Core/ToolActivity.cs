using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexMonitor.Core;

public enum ToolActivityState { Unknown, Running, Completed, Failed }
public sealed record ToolActivity(string Name, string Input, string? Output = null, ToolActivityState State = ToolActivityState.Running,
    long? DurationMs = null, string? SummaryOverride = null, string? CategoryOverride = null, int? AddedLines = null, int? RemovedLines = null)
{
    public long? WaitDurationMs
    {
        get
        {
            if (!Regex.IsMatch(Name, @"(?:^|[._:/])(?:sleep|wait)$", RegexOptions.IgnoreCase)) return null;
            try
            {
                using var document = JsonDocument.Parse(Input); var root = document.RootElement;
                var value = root.Field("duration_ms"); if (value.ValueKind == JsonValueKind.Undefined) value = root.Field("durationMs");
                return value.TryNumber(out var duration) && duration >= 0 ? duration : null;
            }
            catch (JsonException) { return null; }
        }
    }
    public string DecodedInput
    {
        get
        {
            var value = Input;
            try { using var document = JsonDocument.Parse(value); var root = document.RootElement; value = root.Str("cmd", root.Str("command", root.Str("code", root.Str("input", root.Str("query", root.Str("path", value)))))); }
            catch (JsonException) { }
            var patch = Regex.Match(value, "apply_patch\\(\\s*(\"(?:[^\"\\\\]|\\\\.)*\")");
            if (patch.Success) { try { return JsonSerializer.Deserialize(patch.Groups[1].Value, CoreJsonContext.Default.String) ?? value; } catch (JsonException) { } }
            return value;
        }
    }
    public string CategoryKey => CategoryOverride ?? InferCategory();
    private string InferCategory()
    {
        if (WaitDurationMs is not null) return "ToolWaiting";
        if (Input.Contains("*** Begin Patch", StringComparison.Ordinal) || Name is "apply_patch" or "fileChange") return "ToolFileChanges";
        var name = Name.Split([".", "/", ":"], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? Name;
        if (name == "write_stdin" || Input.Contains("write_stdin", StringComparison.Ordinal)) return "ToolReadOutput";
        if (name is "view_image" or "imageView") return "ToolImageView";
        if (name is "imagegen" or "imageGeneration" or "image_generation") return "ToolImageGeneration";
        if (name is "read_file" or "readFile") return "ToolReadFile";
        if (name is "list_dir" or "listFiles" or "list_directory") return "ToolListFiles";
        if (name is "spawn_agent" or "send_message" or "wait_agent" or "followup_task" or "collabToolCall") return "ToolCollaboration";
        if (name is "update_plan" or "plan") return "ToolPlan";
        if (Name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) return "ToolMcp";
        if (name.Contains("search", StringComparison.OrdinalIgnoreCase) || Input.Contains("\"search_query\"", StringComparison.Ordinal)) return "ToolSearch";
        if (Input.Contains("\"open\"", StringComparison.Ordinal) && name == "run") return "ToolOpenPage";
        if (Input.Contains("\"find\"", StringComparison.Ordinal) && name == "run") return "ToolFindInPage";
        if (name is "exec" or "shell" or "commandExecution" or "exec_command" || Input.Contains("exec_command", StringComparison.Ordinal)) return "ToolCommand";
        return "ToolDetails";
    }
    public string Summary
    {
        get
        {
            if (SummaryOverride is not null) return Compact(SummaryOverride);
            if (WaitDurationMs is not null) return "";
            var value = DecodedInput;
            var file = Regex.Match(value, @"\*\*\* (?:Update|Add|Delete) File:\s*([^\r\n]+)");
            if (file.Success) return Path.GetFileName(file.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar));
            var command = Regex.Match(value, "(?:cmd|command)\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")");
            if (command.Success)
            {
                try { value = JsonSerializer.Deserialize(command.Groups[1].Value, CoreJsonContext.Default.String) ?? value; } catch (JsonException) { }
            }
            if (CategoryKey == "ToolReadOutput") return "";
            return Compact(value);
        }
    }
    private static string Compact(string value)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value.Length > 180 ? value[..180] + "…" : value;
    }
    public IReadOnlyList<ToolActivity> DisplayParts()
    {
        if (CategoryKey != "ToolFileChanges") return [this];
        var value = DecodedInput; var edits = new List<(string Path, string Diff)>();
        var headers = Regex.Matches(value, @"^\*\*\* (?:Update|Add|Delete) File:\s*([^\r\n]+)", RegexOptions.Multiline);
        for (int i = 0; i < headers.Count; i++)
        {
            var end = i + 1 < headers.Count ? headers[i + 1].Index : value.Length;
            edits.Add((headers[i].Groups[1].Value.Trim(), value[headers[i].Index..end]));
        }
        if (edits.Count == 0)
        {
            try { using var doc = JsonDocument.Parse(Input); foreach (var change in doc.RootElement.Items()) edits.Add((change.Str("path"), change.Str("diff"))); }
            catch (JsonException) { }
        }
        if (edits.Count == 0) return [this];
        return edits.Select(edit =>
        {
            var added = edit.Diff.Split('\n').Count(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal));
            var removed = edit.Diff.Split('\n').Count(l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal));
            return this with { Input = edit.Diff, SummaryOverride = Path.GetFileName(edit.Path.Replace('/', Path.DirectorySeparatorChar)), CategoryOverride = "ToolFileChanges",
                AddedLines = added + removed > 0 ? added : null, RemovedLines = added + removed > 0 ? removed : null };
        }).ToArray();
    }
    public static ToolActivityState CompletedState(string output)
    {
        var code = Regex.Match(output, @"(?:Process exited with code|Exit code:)\s*(-?\d+)", RegexOptions.IgnoreCase);
        return code.Success && code.Groups[1].Value != "0" ? ToolActivityState.Failed : ToolActivityState.Completed;
    }
    public static ToolActivity FromItem(JsonElement item)
    {
        var type = item.Str("type");
        var state = item.Str("status") switch { "inProgress" => ToolActivityState.Running, "failed" or "declined" => ToolActivityState.Failed, "completed" => ToolActivityState.Completed, _ => ToolActivityState.Unknown };
        if (item.Field("exitCode").TryNumber(out var exit) && exit != 0 || item.Field("success").ValueKind == JsonValueKind.False || item.Field("error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) state = ToolActivityState.Failed;
        long? duration = item.Field("durationMs").TryNumber(out var milliseconds) && milliseconds >= 0 ? milliseconds : null;
        return type switch
        {
            "commandExecution" => Command(item, state, duration),
            "fileChange" => new(type, item.Field("changes").TextValue(), State: state, DurationMs: duration,
                SummaryOverride: string.Join(", ", item.Field("changes").Items().Select(c => Path.GetFileName(c.Str("path").Replace('/', Path.DirectorySeparatorChar)))), CategoryOverride: "ToolFileChanges"),
            "webSearch" or "web_search_call" => Web(item, state, duration),
            "mcpToolCall" => new(item.Str("tool", type), item.Field("arguments").TextValue(), item.Field("error").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? item.Field("error").TextValue() : item.Field("result").TextValue(), state, duration,
                string.Join(" · ", new[] { item.Str("server"), item.Str("tool") }.Where(s => s.Length > 0)), "ToolMcp"),
            "dynamicToolCall" => new(item.Str("tool", type), item.Field("arguments").TextValue(), item.Field("contentItems").TextValue(), state, duration, item.Str("tool"), "ToolDynamic"),
            "collabToolCall" => new(type, new JsonObject { ["tool"] = item.Str("tool"), ["receiverThreadId"] = item.Str("receiverThreadId", item.Str("newThreadId")), ["prompt"] = item.Str("prompt") }.ToJsonString(), item.Field("agentStatus").TextValue(), state, duration, item.Str("tool") + " " + item.Str("receiverThreadId", item.Str("newThreadId")), "ToolCollaboration"),
            "imageView" => new(type, item.Str("path"), State: state, DurationMs: duration, SummaryOverride: Path.GetFileName(item.Str("path").Replace('/', Path.DirectorySeparatorChar)), CategoryOverride: "ToolImageView"),
            "imageGeneration" or "image_generation_call" => new(type, item.GetRawText(), State: state, DurationMs: duration, SummaryOverride: item.Str("revised_prompt", item.Str("prompt")), CategoryOverride: "ToolImageGeneration"),
            "plan" => new(type, item.Str("text"), State: state, SummaryOverride: item.Str("text"), CategoryOverride: "ToolPlan"),
            "contextCompaction" => new(type, "", State: state, SummaryOverride: "", CategoryOverride: "ToolCompaction"),
            "enteredReviewMode" or "exitedReviewMode" => new(type, item.Str("review"), State: state, SummaryOverride: item.Str("review"), CategoryOverride: "ToolReview"),
            "functionCallOutput" => new(item.Str("name", type), "", item.Field("output").TextValue(), state, duration, item.Str("namespace") + " " + item.Str("name"), "ToolReadOutput"),
            _ => new(type, item.GetRawText(), State: state, DurationMs: duration)
        };
    }
    private static ToolActivity Web(JsonElement item, ToolActivityState state, long? duration)
    {
        var action = item.Field("action");
        var category = action.Str("type") switch { "openPage" or "open_page" => "ToolOpenPage", "findInPage" or "find_in_page" => "ToolFindInPage", _ => "ToolSearch" };
        var query = item.Str("query", action.Str("query", string.Join(" · ", action.Field("queries").Items().Select(x => x.TextValue()))));
        var summary = category == "ToolFindInPage" ? action.Str("pattern") + " · " + action.Str("url") : category == "ToolOpenPage" ? action.Str("url") : query;
        var input = action.ValueKind == JsonValueKind.Object ? action.GetRawText() : new JsonObject { ["query"] = query }.ToJsonString();
        return new(item.Str("type"), input, item.Field("results").TextValue(), state, duration, summary, category);
    }
    private static ToolActivity Command(JsonElement item, ToolActivityState state, long? duration)
    {
        var actions = item.Field("commandActions").Items().ToArray();
        var action = actions.Length == 1 ? actions[0] : default;
        var category = action.Str("type") switch { "read" => "ToolReadFile", "listFiles" => "ToolListFiles", "search" => "ToolSearch", _ => "ToolCommand" };
        var summary = category == "ToolCommand" ? null : action.Str("path", action.Str("query", action.Str("command", item.Str("command"))));
        return new("commandExecution", item.Str("command"), item.Str("aggregatedOutput"), state, duration, summary, category);
    }
}
