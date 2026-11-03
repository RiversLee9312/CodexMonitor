using System.Text.Json;

namespace CodexMonitor.Core;

/// <summary>Protocol labels distinguish summaries from plain-text reasoning. Encrypted blocks are never read.</summary>
public sealed record ReasoningContent(string Summary, string FullText)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Summary) ? FullText : Summary;
    public static ReasoningContent Parse(JsonElement item)
    {
        var summary = Read(item.Field("summary"), summary: true);
        var full = Read(item.Field("content"), summary: false);
        // Some older providers emit a plain text field instead of typed content. It is not a confirmed summary.
        if (full.Length == 0) full = item.Str("text");
        return new(summary, full);
    }
    private static string Read(JsonElement value, bool summary)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        return string.Join("\n\n", value.Items().Select(part => part.ValueKind == JsonValueKind.String ? part.GetString() ?? "" :
            part.Str("type") is "" or "text" || (summary ? part.Str("type") is "summary_text" or "summaryText" : part.Str("type") is "reasoning_text" or "reasoningText") ? part.Str("text") : "")
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    }
}
