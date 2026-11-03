using System.Text.Json;
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
            if (patch.Success) { try { return JsonSerializer.Deserialize<string>(patch.Groups[1].Value) ?? value; } catch (JsonException) { } }
            return value;
        }
    }
    public string CategoryKey => CategoryOverride ?? (WaitDurationMs is not null ? "ToolWaiting" : Input.Contains("*** Begin Patch", StringComparison.Ordinal) ? "ToolFileChanges" :
        Input.Contains("write_stdin", StringComparison.Ordinal) ? "ToolReadOutput" : Name.Contains("search", StringComparison.OrdinalIgnoreCase) ? "ToolSearch" :
        Name is "exec" or "shell" or "commandExecution" or "exec_command" || Input.Contains("exec_command", StringComparison.Ordinal) ? "ToolCommand" : "ToolDetails");
    public string Summary
    {
        get
        {
            if (SummaryOverride is not null) return SummaryOverride;
            if (WaitDurationMs is not null) return "";
            var value = DecodedInput;
            var file = Regex.Match(value, @"\*\*\* (?:Update|Add|Delete) File:\s*([^\r\n]+)");
            if (file.Success) return Path.GetFileName(file.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar));
            var command = Regex.Match(value, "(?:cmd|command)\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")");
            if (command.Success)
            {
                try { value = JsonSerializer.Deserialize<string>(command.Groups[1].Value) ?? value; } catch (JsonException) { }
            }
            if (CategoryKey == "ToolReadOutput") return "";
            value = Regex.Replace(value, @"\s+", " ").Trim();
            return value.Length > 180 ? value[..180] + "…" : value;
        }
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
            "commandExecution" => new(type, item.Str("command"), item.Str("aggregatedOutput"), state, duration),
            "fileChange" => new(type, item.Field("changes").TextValue(), State: state, DurationMs: duration,
                SummaryOverride: string.Join(", ", item.Field("changes").Items().Select(c => Path.GetFileName(c.Str("path").Replace('/', Path.DirectorySeparatorChar)))), CategoryOverride: "ToolFileChanges"),
            "webSearch" => new(type, item.GetRawText(), item.Field("results").TextValue(), state, duration, item.Str("query", item.Field("action").Str("query", item.Field("action").Str("url"))), "ToolSearch"),
            "mcpToolCall" => new(item.Str("tool", type), item.Field("arguments").TextValue(), item.Field("error").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? item.Field("error").TextValue() : item.Field("result").TextValue(), state, duration),
            "dynamicToolCall" => new(item.Str("tool", type), item.Field("arguments").TextValue(), item.Field("contentItems").TextValue(), state, duration),
            _ => new(type, item.GetRawText(), State: state, DurationMs: duration)
        };
    }
}
