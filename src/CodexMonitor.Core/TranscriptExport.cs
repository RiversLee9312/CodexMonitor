using System.Text;

namespace CodexMonitor.Core;

public static class TranscriptExport
{
    public static string Markdown(TranscriptSnapshot transcript, LocalizationService text)
    {
        var b = new StringBuilder().Append("# ").AppendLine(transcript.Session.Title.Replace('\n', ' ')).AppendLine();
        b.Append(text["SessionId"]).Append(": ").AppendLine(transcript.Session.Id);
        b.Append(text["Folder"]).Append(": ").AppendLine(transcript.Session.Cwd).AppendLine();
        foreach (var entry in transcript.Entries)
        {
            var role = text[entry.Kind switch { EntryKind.User => "User", EntryKind.Assistant => "Assistant", EntryKind.Tool => "Tool", EntryKind.Reasoning => "Reasoning", _ => "SystemMessage" }];
            b.Append("## ").Append(role).Append(" · ").AppendLine(text.DateTime(entry.Timestamp)).AppendLine();
            if (entry.Kind is EntryKind.Tool or EntryKind.Event)
            {
                var fence = new string('`', Math.Max(3, LongestBackticks(entry.Text) + 1));
                b.AppendLine(fence).AppendLine(entry.Text).AppendLine(fence);
            }
            else b.AppendLine(entry.Text);
            if (entry.Reasoning is { Summary.Length: > 0, FullText.Length: > 0 } reasoning)
            {
                b.AppendLine().Append("### ").AppendLine(text["Thinking"]).AppendLine().AppendLine(reasoning.FullText);
            }
            b.AppendLine();
        }
        return b.ToString();
    }
    private static int LongestBackticks(string s)
    {
        int best = 0, run = 0;
        foreach (var c in s) { run = c == '`' ? run + 1 : 0; best = Math.Max(best, run); }
        return best;
    }
}
