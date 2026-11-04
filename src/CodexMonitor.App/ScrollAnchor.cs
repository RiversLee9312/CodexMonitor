using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexMonitor.App;

internal sealed record ScrollAnchor(ListView List, object Item, double Y)
{
    internal static ScrollAnchor? Capture(ListView list)
    {
        foreach (var item in list.Items)
            if (list.ContainerFromItem(item) is FrameworkElement element)
            {
                var y = element.TransformToVisual(list).TransformPoint(new(0, 0)).Y;
                if (y + element.ActualHeight > 0 && y < list.ActualHeight) return new(list, item, y);
            }
        return null;
    }
    internal void Restore(Func<bool>? stillValid = null)
    {
        if (stillValid?.Invoke() == false || !List.Items.Contains(Item) || !List.IsLoaded) return;
        List.UpdateLayout();
        if (List.ContainerFromItem(Item) is not FrameworkElement) { List.ScrollIntoView(Item, ScrollIntoViewAlignment.Leading); List.UpdateLayout(); }
        AdjustIfValid();
        List.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, AdjustIfValid);
        void AdjustIfValid() { if (stillValid?.Invoke() != false) Adjust(); }
    }
    private void Adjust()
    {
        if (!List.IsLoaded || List.ContainerFromItem(Item) is not FrameworkElement element || FindScroll(List) is not { } scroll) return;
        var delta = element.TransformToVisual(List).TransformPoint(new(0, 0)).Y - Y;
        if (Math.Abs(delta) > .5) scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + delta, 0, scroll.ScrollableHeight), null, true);
    }
    internal static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is ScrollViewer scroll) return scroll; if (FindScroll(child) is { } nested) return nested; }
        return null;
    }
}
