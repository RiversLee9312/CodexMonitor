using System.Collections.ObjectModel;

namespace CodexMonitor.App;

internal static class EntryRows
{
    internal static void Synchronize(ObservableCollection<EntryRow> target, IReadOnlyList<EntryRow> desired)
    {
        var ids = desired.Select(r => r.Entry.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = target.Count - 1; i >= 0; i--) if (!ids.Contains(target[i].Entry.Id)) target.RemoveAt(i);
        var present = target.ToDictionary(r => r.Entry.Id);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && target[i].Entry.Id == desired[i].Entry.Id) { if (!target[i].Matches(desired[i])) target[i].Update(desired[i]); }
            else if (present.TryGetValue(desired[i].Entry.Id, out var row)) { target.Move(target.IndexOf(row), i); if (!row.Matches(desired[i])) row.Update(desired[i]); }
            else target.Insert(i, desired[i]);
        }
    }
}
