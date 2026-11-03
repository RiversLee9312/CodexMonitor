using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CodexMonitor.App;

internal static class AccessibilitySupport
{
    internal static void KeepIconsUnscaled(DependencyObject root)
    {
        if (root is FontIcon icon) icon.IsTextScaleFactorEnabled = false;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) KeepIconsUnscaled(VisualTreeHelper.GetChild(root, i));
    }
    internal static void NormalizeIconsAfterLayout(FrameworkElement root) => root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => { if (root.IsLoaded) KeepIconsUnscaled(root); });
    internal static void Announce(FrameworkElement source, string message, string activity)
    {
        if (!source.IsLoaded || string.IsNullOrWhiteSpace(message)) return;
        var peer = FrameworkElementAutomationPeer.FromElement(source) ?? FrameworkElementAutomationPeer.CreatePeerForElement(source);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, message, activity);
    }
    internal static bool ContainsFocus(FrameworkElement element)
    {
        if (element.XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(element.XamlRoot) as DependencyObject;
        while (focused is not null) { if (ReferenceEquals(element, focused)) return true; focused = VisualTreeHelper.GetParent(focused); }
        return false;
    }
    internal static void RestoreFocus(Control? original, Control fallback)
    {
        var target = original is { IsEnabled: true, IsLoaded: true, Visibility: Visibility.Visible } ? original : fallback;
        if (target.IsEnabled && target.IsLoaded && target.Visibility == Visibility.Visible) target.Focus(FocusState.Programmatic);
    }
}
