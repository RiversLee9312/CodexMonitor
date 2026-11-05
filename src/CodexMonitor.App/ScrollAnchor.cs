using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace CodexMonitor.App;

internal sealed record ScrollAnchor(RepeaterList List, object Item, double Y, long InputVersion)
{
    internal static Action<string>? Diagnostics { get; set; }
    // Native ScrollViewer chaining excludes inertial mouse-wheel input. Forward only
    // gestures that begin at an exhausted boundary; touch keeps its native policy.
    internal static void EnableWheelChaining(ScrollViewer scroll)
    {
        scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            var properties = args.GetCurrentPoint(scroll).Properties;
            var delta = properties.MouseWheelDelta;
            if (properties.IsHorizontalMouseWheel || delta == 0 || args.KeyModifiers != Windows.System.VirtualKeyModifiers.None || !scroll.IsVerticalScrollChainingEnabled) return;
            for (var node = args.OriginalSource as DependencyObject; node is not null && node != scroll; node = VisualTreeHelper.GetParent(node))
                if (node is ScrollViewer) return; // The deepest surface owns the gesture.
            if (delta > 0 ? scroll.VerticalOffset > .5 : scroll.ScrollableHeight - scroll.VerticalOffset > .5) return;
            for (var node = VisualTreeHelper.GetParent(scroll); node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is not ScrollViewer parent || parent.VerticalScrollMode == ScrollMode.Disabled ||
                    (delta > 0 ? parent.VerticalOffset <= .5 : parent.ScrollableHeight - parent.VerticalOffset <= .5)) continue;
                if (!GetWheelLines(0x0068, 0, out var lines, 0)) lines = 3;
                var distance = delta / 120d * (lines == uint.MaxValue ? parent.ViewportHeight : lines * 16d);
                parent.ChangeView(null, Math.Clamp(parent.VerticalOffset - distance, 0, parent.ScrollableHeight), null, true);
                args.Handled = true; return;
            }
        }), true);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWheelLines(uint action, uint parameter, out uint value, uint flags);
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
