using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CodexMonitor.App;

/// <summary>Virtualized collection surface with explicit single-selection and accessibility policies.</summary>
public partial class RepeaterList : UserControl
{
    internal ItemsRepeater Repeater { get; }
    internal ScrollViewer Scroll { get; }
    private readonly HashSet<RepeaterListItem> _realized = [];
    private object? _source, _selected;
    private INotifyCollectionChanged? _observable;
    private bool _positionsQueued;
    public event EventHandler? SelectionChanged;
    internal event Action<bool>? NavigationRequested;
    public DataTemplate? ItemTemplate { get; set; }
    public Style? ItemContainerStyle { get; set; }
    public ListViewSelectionMode SelectionMode { get; set; }
    public IList Items => _source as IList ?? Array.Empty<object>();
    public object? ItemsSource
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            if (_observable is not null) _observable.CollectionChanged -= SourceChanged;
            _source = value; Repeater.ItemsSource = value;
            _observable = value as INotifyCollectionChanged;
            if (_observable is not null) _observable.CollectionChanged += SourceChanged;
            if (_selected is not null && !Items.Contains(_selected)) SelectedItem = null;
        }
    }
    public object? SelectedItem
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            foreach (var element in _realized) element.IsSelected = ReferenceEquals(element.Content, value);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            if (FrameworkElementAutomationPeer.FromElement(this) is { } peer) peer.RaiseAutomationEvent(AutomationEvents.SelectionPatternOnInvalidated);
        }
    }
    public RepeaterList()
    {
        IsTabStop = false; HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        Repeater = new ItemsRepeater { Layout = new StackLayout(), VerticalCacheLength = 1.5, HorizontalAlignment = HorizontalAlignment.Stretch,
            // The factory's recycle pool must belong to this surface. A container from a
            // removed nested repeater must never be reparented into the sidebar or transcript.
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:CodexMonitor.App'><local:RepeaterListItem/></DataTemplate>") };
        Scroll = new ScrollViewer { Content = Repeater, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAnchorRatio = 0, IsTabStop = false, IsVerticalScrollChainingEnabled = true };
        Content = Scroll;
        Repeater.ElementPrepared += (_, e) =>
        {
            var element = (RepeaterListItem)e.Element; element.Owner = this; element.Index = e.Index; _realized.Add(element);
            element.Style = ItemContainerStyle; element.ContentTemplate = ItemTemplate;
            if (SelectionMode == ListViewSelectionMode.None) element.UseReadingStyle();
            else VisualStateManager.GoToState(element, "Normal", false);
            element.Content = Items[e.Index]; element.IsSelected = ReferenceEquals(element.Content, _selected);
            element.IsTabStop = SelectionMode == ListViewSelectionMode.Single;
            SetPosition(element);
        };
        Repeater.ElementClearing += (_, e) =>
        {
            var element = (RepeaterListItem)e.Element; _realized.Remove(element);
            element.Owner = null; element.Index = -1; element.ContentTemplate = null; element.Content = null; element.DataContext = null;
        };
        Repeater.ElementIndexChanged += (_, e) => { var element = (RepeaterListItem)e.Element; element.Index = e.NewIndex; SetPosition(element); };
        KeyDown += OnNavigation;
    }
    private void SourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_positionsQueued)
        {
            _positionsQueued = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            { _positionsQueued = false; foreach (var element in _realized) SetPosition(element); });
        }
        if (_selected is not null && !Items.Contains(_selected)) SelectedItem = null;
    }
    private void SetPosition(RepeaterListItem element)
    {
        AutomationProperties.SetPositionInSet(element, element.Index + 1); AutomationProperties.SetSizeOfSet(element, Items.Count);
    }
    public UIElement? ContainerFromIndex(int index) => index >= 0 && index < Items.Count ? Repeater.TryGetElement(index) : null;
    public UIElement? ContainerFromItem(object item) => ContainerFromIndex(Items.IndexOf(item));
    internal IEnumerable<RepeaterListItem> RealizedElements => _realized.OrderBy(e => e.Index);
    internal int FirstVisibleIndex => VisibleIndices().DefaultIfEmpty(-1).First();
    internal int LastVisibleIndex => VisibleIndices().DefaultIfEmpty(-1).Last();
    private IEnumerable<int> VisibleIndices()
    {
        foreach (var element in RealizedElements)
        {
            if (!element.IsLoaded || element.Visibility != Visibility.Visible) continue;
            var y = element.TransformToVisual(Scroll).TransformPoint(new(0, 0)).Y;
            if (y + element.ActualHeight > 0 && y < Scroll.ViewportHeight) yield return element.Index;
        }
    }
    public void ScrollIntoView(object item, ScrollIntoViewAlignment alignment = ScrollIntoViewAlignment.Default)
    {
        var index = Items.IndexOf(item); if (index < 0 || !IsLoaded) return;
        var element = Repeater.GetOrCreateElement(index);
        // A freshly realized element has no arranged bounds until the virtual layout runs.
        // BringIntoView against stale bounds can silently leave a jump at the old end.
        Repeater.UpdateLayout();
        var bounds = element.TransformToVisual(Scroll).TransformBounds(new(0, 0, ((FrameworkElement)element).ActualWidth, ((FrameworkElement)element).ActualHeight));
        var target = Scroll.VerticalOffset + bounds.Top;
        if (alignment != ScrollIntoViewAlignment.Leading && bounds.Top >= 0)
        {
            if (bounds.Bottom <= Scroll.ViewportHeight) return;
            target = Scroll.VerticalOffset + bounds.Bottom - Scroll.ViewportHeight;
        }
        // Keep this request local and synchronous. A queued BringIntoView can outlive
        // the follow generation and pull a reader back after their next wheel gesture.
        Scroll.ChangeView(null, Math.Clamp(target, 0, Scroll.ScrollableHeight), null, true);
    }
    private void OnNavigation(object sender, KeyRoutedEventArgs e)
    {
        if (SelectionMode != ListViewSelectionMode.Single || Items.Count == 0) return;
        if (e.OriginalSource is not RepeaterListItem && e.OriginalSource is not ScrollViewer && !ReferenceEquals(e.OriginalSource, this)) return;
        var index = Items.IndexOf(_selected); var next = e.Key switch
        { VirtualKey.Up => Math.Max(0, index - 1), VirtualKey.Down => Math.Min(Items.Count - 1, index + 1), VirtualKey.Home => 0, VirtualKey.End => Items.Count - 1, _ => -1 };
        if (next < 0) return;
        NavigationRequested?.Invoke(next >= index); SelectedItem = Items[next]; ScrollIntoView(Items[next]!);
        ((Control)Repeater.GetOrCreateElement(next)).Focus(FocusState.Keyboard); e.Handled = true;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new CollectionPeer(this);
    private sealed partial class CollectionPeer(RepeaterList owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider, IScrollProvider
    {
        private IScrollProvider Native => (IScrollProvider)FrameworkElementAutomationPeer.CreatePeerForElement(owner.Scroll).GetPattern(PatternInterface.Scroll);
        protected override string GetClassNameCore() => nameof(RepeaterList);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.Scroll || patternInterface == PatternInterface.Selection && owner.SelectionMode == ListViewSelectionMode.Single ? this : base.GetPatternCore(patternInterface);
        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => false;
        public IRawElementProviderSimple[] GetSelection() => owner._selected is not null && owner.ContainerFromItem(owner._selected) is FrameworkElement item ? [ProviderFromPeer(CreatePeerForElement(item))] : [];
        public bool HorizontallyScrollable => Native.HorizontallyScrollable;
        public bool VerticallyScrollable => Native.VerticallyScrollable;
        public double HorizontalScrollPercent => Native.HorizontalScrollPercent;
        public double VerticalScrollPercent => Native.VerticalScrollPercent;
        public double HorizontalViewSize => Native.HorizontalViewSize;
        public double VerticalViewSize => Native.VerticalViewSize;
        public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
        { owner.NavigationRequested?.Invoke(verticalAmount is ScrollAmount.LargeIncrement or ScrollAmount.SmallIncrement); Native.Scroll(horizontalAmount, verticalAmount); }
        public void SetScrollPercent(double horizontalPercent, double verticalPercent)
        { owner.NavigationRequested?.Invoke(verticalPercent >= 0 && verticalPercent >= Native.VerticalScrollPercent); Native.SetScrollPercent(horizontalPercent, verticalPercent); }
    }
}

public sealed partial class RepeaterListItem : ListViewItem
{
    private bool _readingStyle;
    internal void UseReadingStyle()
    {
        if (_readingStyle) return;
        _readingStyle = true;
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundPressed", "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed", "ListViewItemBorderBrushPointerOver", "ListViewItemBorderBrushPressed" }) Resources[key] = transparent;
    }
    internal RepeaterList? Owner { get; set; }
    internal int Index { get; set; } = -1;
    public RepeaterListItem()
    {
        DefaultStyleKey = typeof(ListViewItem);
        Tapped += (_, _) => { if (Owner?.SelectionMode == ListViewSelectionMode.Single) { Owner.SelectedItem = Content; Focus(FocusState.Pointer); } };
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ItemPeer(this);
    private sealed partial class ItemPeer(RepeaterListItem owner) : ListViewItemAutomationPeer(owner), ISelectionItemProvider
    {
        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.Content?.ToString() ?? "";
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.SelectionItem ? owner.Owner?.SelectionMode == ListViewSelectionMode.Single ? this : null! : base.GetPatternCore(patternInterface);
        public bool IsSelected => owner.IsSelected;
        public IRawElementProviderSimple SelectionContainer => owner.Owner is { } list ? ProviderFromPeer(CreatePeerForElement(list)) : null!;
        public void Select() { if (!owner.IsEnabled) throw new ElementNotEnabledException(); if (owner.Owner is { } list) list.SelectedItem = owner.Content; }
        public void AddToSelection() => Select();
        public void RemoveFromSelection() { if (owner.Owner is { } list && owner.IsSelected) list.SelectedItem = null; }
    }
}
