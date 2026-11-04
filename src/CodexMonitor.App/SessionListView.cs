using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

/// <summary>Keep native list semantics while making accessibility scrolling a navigation input.</summary>
public sealed partial class SessionListView : ListView
{
    internal event Action<bool>? NavigationRequested;
    protected override AutomationPeer OnCreateAutomationPeer() => new SessionListPeer(this);
    private sealed partial class SessionListPeer(SessionListView owner) : ListViewAutomationPeer(owner)
    {
        protected override object GetPatternCore(PatternInterface patternInterface)
        {
            var pattern = base.GetPatternCore(patternInterface);
            return patternInterface == PatternInterface.Scroll && pattern is IScrollProvider scroll ? new NavigationScroll(owner, scroll) : pattern;
        }
    }
    private sealed partial class NavigationScroll(SessionListView owner, IScrollProvider native) : IScrollProvider
    {
        public bool HorizontallyScrollable => native.HorizontallyScrollable;
        public bool VerticallyScrollable => native.VerticallyScrollable;
        public double HorizontalScrollPercent => native.HorizontalScrollPercent;
        public double VerticalScrollPercent => native.VerticalScrollPercent;
        public double HorizontalViewSize => native.HorizontalViewSize;
        public double VerticalViewSize => native.VerticalViewSize;
        public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
        { owner.NavigationRequested?.Invoke(verticalAmount is ScrollAmount.LargeIncrement or ScrollAmount.SmallIncrement); native.Scroll(horizontalAmount, verticalAmount); }
        public void SetScrollPercent(double horizontalPercent, double verticalPercent)
        { owner.NavigationRequested?.Invoke(verticalPercent >= 0 && verticalPercent >= native.VerticalScrollPercent); native.SetScrollPercent(horizontalPercent, verticalPercent); }
    }
}
