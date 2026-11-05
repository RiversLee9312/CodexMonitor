using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexMonitor.App;

internal sealed record ScrollAnchor(RepeaterList List, object Item, double Y, long InputVersion)
{
    internal static Action<string>? Diagnostics { get; set; }
    internal static ScrollAnchor? Capture(RepeaterList list)
    {
        foreach (var element in list.RealizedElements)
            if (element.Content is { } item)
            {
                var y = element.TransformToVisual(list).TransformPoint(new(0, 0)).Y;
                if (y + element.ActualHeight > 0 && y < list.ActualHeight) return new(list, item, y, list.InputVersion);
            }
        return null;
    }
    internal void Restore(Func<bool>? stillValid = null)
    {
        bool Valid() => List.InputVersion == InputVersion && stillValid?.Invoke() != false && List.Items.Contains(Item) && List.IsLoaded;
        if (!Valid()) return;
        var ticket = List.BeginRestoration();
        List.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                for (int attempt = 0; attempt < 6 && Valid() && List.OwnsRestoration(ticket); attempt++)
                {
                    List.UpdateLayout();
                    if (List.ContainerFromItem(Item) is not FrameworkElement) { List.ScrollIntoView(Item, ScrollIntoViewAlignment.Leading); List.UpdateLayout(); }
                    if (Adjust() && attempt >= 2) return;
                    await Task.Delay(16);
                }
                if (!Valid()) Diagnostics?.Invoke($"anchor cancelled: loaded={List.IsLoaded}, input={InputVersion}/{List.InputVersion}, contains={List.Items.Contains(Item)}");
            }
            finally { Diagnostics?.Invoke($"anchor finished ticket={ticket} owns={List.OwnsRestoration(ticket)} valid={Valid()}"); List.EndRestoration(ticket); }
        });
    }
    private bool Adjust()
    {
        if (!List.IsLoaded || List.ContainerFromItem(Item) is not FrameworkElement element || FindScroll(List) is not { } scroll) return false;
        var delta = element.TransformToVisual(List).TransformPoint(new(0, 0)).Y - Y;
        Diagnostics?.Invoke($"anchor count={List.Items.Count} desired={Y:F1} actual={Y+delta:F1} offset={scroll.VerticalOffset:F1} extent={scroll.ExtentHeight:F1} delta={delta:F1}");
        if (Math.Abs(delta) > .5) scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + delta, 0, scroll.ScrollableHeight), null, true);
        return Math.Abs(delta) <= .5;
    }
    internal static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is ScrollViewer scroll) return scroll; if (FindScroll(child) is { } nested) return nested; }
        return null;
    }
}
