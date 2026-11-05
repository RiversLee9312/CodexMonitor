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
    internal long InputVersion { get; private set; }
    private long _restoreSequence, _activeRestore;
    internal bool IsRestoringPosition => _activeRestore != 0;
    internal long BeginRestoration() => _activeRestore = ++_restoreSequence;
    internal bool OwnsRestoration(long ticket) => _activeRestore == ticket;
    internal void EndRestoration(long ticket) { if (_activeRestore == ticket) _activeRestore = 0; }
    private void MarkInput() { InputVersion++; _activeRestore = 0; }
    internal void BeginNavigation(bool forward) { MarkInput(); NavigationRequested?.Invoke(forward); }
    private readonly HashSet<RepeaterListItem> _realized = [];
    private object? _source, _selected;
    private KeyedSource _view = new(Array.Empty<object>());
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
            _source = value; _view = new(Items); Repeater.ItemsSource = _view;
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
        TabFocusNavigation = KeyboardNavigationMode.Once;
        AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => MarkInput()), true);
        AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => MarkInput()), true);
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => MarkInput()), true);
        Repeater.ElementPrepared += (_, e) =>
        {
            var element = (RepeaterListItem)e.Element; element.Owner = this; element.Index = e.Index; _realized.Add(element);
            element.Style = ItemContainerStyle; element.ContentTemplate = ItemTemplate;
            element.ResetInteraction();
            element.Content = _view[e.Index]; element.IsSelected = ReferenceEquals(element.Content, _selected);
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
        if (e.Action == NotifyCollectionChangedAction.Reset && Items.Count == 0)
        { Repeater.ItemsSource = null; _view = new(Items); Repeater.ItemsSource = _view; }
        else _view.Changed(e);
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
    internal IDisposable DeferUpdates() => _view.Defer();
    internal string ItemKey(int index) => _view.KeyFromIndex(index);
    public UIElement? ContainerFromIndex(int index) => index >= 0 && index < _view.Count ? Repeater.TryGetElement(index) : null;
    public UIElement? ContainerFromItem(object item) => ContainerFromIndex(_view.IndexOf(item));
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
        var index = _view.IndexOf(item); if (index < 0 || !IsLoaded) return;
        var element = Repeater.GetOrCreateElement(index);
        // A freshly realized element has no arranged bounds until the virtual layout runs.
        // BringIntoView against stale bounds can silently leave a jump at the old end.
        Repeater.UpdateLayout();
        var bounds = element.TransformToVisual(Scroll).TransformBounds(new(0, 0, ((FrameworkElement)element).ActualWidth, ((FrameworkElement)element).ActualHeight));
        var target = Scroll.VerticalOffset + bounds.Top;
        if (alignment != ScrollIntoViewAlignment.Leading && bounds.Top >= 0)
        {
            if (bounds.Bottom <= Scroll.ViewportHeight) return;
            target -= Math.Max(0, Scroll.ViewportHeight - bounds.Height);
        }
        Scroll.ChangeView(null, Math.Clamp(target, 0, Scroll.ScrollableHeight), null, true);
    }
    // Publish contiguous pages as one range change. Stable identities retain existing
    // containers without hundreds of intermediate layouts or a reset to the origin.
    private sealed partial class KeyedSource(IList source) : IReadOnlyList<object>, INotifyCollectionChanged, IKeyIndexMapping
    {
        private sealed record Identity(long Value);
        private static long _identity;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Identity> Identities = new();
        private List<object>? _before;
        private Dictionary<string, int>? _indices;
        private bool _changed;
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        public int Count => _before?.Count ?? source.Count;
        public object this[int index] => _before is { } snapshot ? snapshot[index] : source[index]!;
        public IEnumerator<object> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int IndexOf(object item) => _before?.IndexOf(item) ?? source.IndexOf(item);
        public string KeyFromIndex(int index) => Identities.GetValue(this[index], static _ => new(Interlocked.Increment(ref _identity))).Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public int IndexFromKey(string uniqueId)
        {
            if (_indices is null) { _indices = new(StringComparer.Ordinal); for (int i = 0; i < Count; i++) _indices[KeyFromIndex(i)] = i; }
            return _indices.GetValueOrDefault(uniqueId, -1);
        }
        internal void Changed(NotifyCollectionChangedEventArgs args)
        { _indices = null; if (_before is not null) _changed = true; else CollectionChanged?.Invoke(this, args); }
        internal IDisposable Defer()
        {
            if (_before is not null) throw new InvalidOperationException("Nested collection transaction");
            _before = new(Count); for (int i = 0; i < source.Count; i++) _before.Add(source[i]!);
            return new Scope(() =>
            {
                var before = _before!; _before = null; _indices = null;
                if (!_changed) return;
                _changed = false;
                var added = source.Count - before.Count;
                if (added > 0 && Enumerable.Range(0, before.Count).All(i => ReferenceEquals(before[i], source[i + added])))
                    CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Add, source.Cast<object>().Take(added).ToList(), 0));
                else if (added > 0 && Enumerable.Range(0, before.Count).All(i => ReferenceEquals(before[i], source[i])))
                    CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Add, source.Cast<object>().Skip(before.Count).ToList(), before.Count));
                else CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
            });
        }
        private sealed partial class Scope(Action commit) : IDisposable { private Action? _commit = commit; public void Dispose() => Interlocked.Exchange(ref _commit, null)?.Invoke(); }
    }
    private void OnNavigation(object sender, KeyRoutedEventArgs e)
    {
        if (SelectionMode != ListViewSelectionMode.Single || Items.Count == 0) return;
        if (e.OriginalSource is not RepeaterListItem && e.OriginalSource is not ScrollViewer && !ReferenceEquals(e.OriginalSource, this)) return;
        var index = e.OriginalSource is RepeaterListItem focused ? focused.Index : Items.IndexOf(_selected);
        if (e.Key is VirtualKey.Enter or VirtualKey.Space && index >= 0)
        { SelectedItem = Items[index]; e.Handled = true; return; }
        var next = e.Key switch
        { VirtualKey.Up => Math.Max(0, index - 1), VirtualKey.Down => Math.Min(Items.Count - 1, index + 1), VirtualKey.Home => 0, VirtualKey.End => Items.Count - 1, _ => -1 };
        if (next < 0) return;
        NavigationRequested?.Invoke(next >= index); SelectedItem = Items[next]; ScrollIntoView(Items[next]!);
        ((Control)Repeater.GetOrCreateElement(next)).Focus(FocusState.Keyboard); e.Handled = true;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new CollectionPeer(this);
    private sealed partial class CollectionPeer(RepeaterList owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider, IScrollProvider
    {
        protected override string GetClassNameCore() => nameof(RepeaterList);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.Scroll || patternInterface == PatternInterface.Selection && owner.SelectionMode == ListViewSelectionMode.Single ? this : base.GetPatternCore(patternInterface);
        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => false;
        public IRawElementProviderSimple[] GetSelection() => owner._selected is not null && owner.ContainerFromItem(owner._selected) is FrameworkElement item ? [ProviderFromPeer(CreatePeerForElement(item))] : [];
        public bool HorizontallyScrollable => owner.Scroll.ScrollableWidth > 0;
        public bool VerticallyScrollable => owner.Scroll.ScrollableHeight > 0;
        public double HorizontalScrollPercent => HorizontallyScrollable ? owner.Scroll.HorizontalOffset / owner.Scroll.ScrollableWidth * 100 : -1;
        public double VerticalScrollPercent => VerticallyScrollable ? owner.Scroll.VerticalOffset / owner.Scroll.ScrollableHeight * 100 : -1;
        public double HorizontalViewSize => owner.Scroll.ExtentWidth > 0 ? Math.Min(100, owner.Scroll.ViewportWidth / owner.Scroll.ExtentWidth * 100) : 100;
        public double VerticalViewSize => owner.Scroll.ExtentHeight > 0 ? Math.Min(100, owner.Scroll.ViewportHeight / owner.Scroll.ExtentHeight * 100) : 100;
        public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            double Delta(ScrollAmount amount, double viewport) => amount switch { ScrollAmount.SmallIncrement => 32, ScrollAmount.SmallDecrement => -32, ScrollAmount.LargeIncrement => viewport * .9, ScrollAmount.LargeDecrement => -viewport * .9, _ => 0 };
            if (horizontalAmount != ScrollAmount.NoAmount && !HorizontallyScrollable || verticalAmount != ScrollAmount.NoAmount && !VerticallyScrollable) throw new InvalidOperationException("The requested axis is not scrollable");
            owner.BeginNavigation(verticalAmount is ScrollAmount.LargeIncrement or ScrollAmount.SmallIncrement);
            owner.Scroll.ChangeView(Math.Clamp(owner.Scroll.HorizontalOffset + Delta(horizontalAmount, owner.Scroll.ViewportWidth), 0, owner.Scroll.ScrollableWidth), Math.Clamp(owner.Scroll.VerticalOffset + Delta(verticalAmount, owner.Scroll.ViewportHeight), 0, owner.Scroll.ScrollableHeight), null, true);
        }
        public void SetScrollPercent(double horizontalPercent, double verticalPercent)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            if (horizontalPercent != -1 && (!double.IsFinite(horizontalPercent) || horizontalPercent < 0 || horizontalPercent > 100) || verticalPercent != -1 && (!double.IsFinite(verticalPercent) || verticalPercent < 0 || verticalPercent > 100)) throw new ArgumentOutOfRangeException();
            owner.BeginNavigation(verticalPercent >= 0 && verticalPercent >= VerticalScrollPercent);
            owner.Scroll.ChangeView(horizontalPercent == -1 ? null : owner.Scroll.ScrollableWidth * horizontalPercent / 100, verticalPercent == -1 ? null : owner.Scroll.ScrollableHeight * verticalPercent / 100, null, true);
        }
    }
}

public sealed partial class RepeaterListItem : ContentControl
{
    private bool _selected, _hover;
    private Microsoft.UI.Xaml.Controls.Border? _chrome;
    internal bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Paint();
            if (FrameworkElementAutomationPeer.FromElement(this) is { } peer) peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !value, value); }
    }
    internal void ResetInteraction() { _hover = false; Paint(); }
    protected override void OnApplyTemplate() { base.OnApplyTemplate(); _chrome = GetTemplateChild("Chrome") as Microsoft.UI.Xaml.Controls.Border; Paint(); }
    private void Paint() => _chrome?.SetResourceReference(Microsoft.UI.Xaml.Controls.Border.BackgroundProperty,
        Owner?.SelectionMode != ListViewSelectionMode.Single ? "ControlFillColorTransparentBrush" : _selected ? "ListViewItemBackgroundSelected" : _hover ? "ListViewItemBackgroundPointerOver" : "ControlFillColorTransparentBrush");
    internal RepeaterList? Owner { get; set; }
    internal int Index { get; set; } = -1;
    public RepeaterListItem()
    {
        UseSystemFocusVisuals = true;
        PointerEntered += (_, _) => { _hover = true; Paint(); };
        PointerExited += (_, _) => { _hover = false; Paint(); };
        Tapped += (_, _) => { if (Owner?.SelectionMode == ListViewSelectionMode.Single) { Owner.SelectedItem = Content; Focus(FocusState.Pointer); } };
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ItemPeer(this);
    private sealed partial class ItemPeer(RepeaterListItem owner) : FrameworkElementAutomationPeer(owner), ISelectionItemProvider, IScrollItemProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetClassNameCore() => nameof(RepeaterListItem);
        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.Content?.ToString() ?? "";
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.ScrollItem ? this : patternInterface == PatternInterface.SelectionItem ? owner.Owner?.SelectionMode == ListViewSelectionMode.Single ? this : null! : base.GetPatternCore(patternInterface);
        public void ScrollIntoView() { if (owner.Owner is { } list && owner.Content is { } item) list.ScrollIntoView(item); }
        public bool IsSelected => owner.IsSelected;
        public IRawElementProviderSimple SelectionContainer => owner.Owner is { } list ? ProviderFromPeer(CreatePeerForElement(list)) : null!;
        public void Select() { if (!owner.IsEnabled) throw new ElementNotEnabledException(); if (owner.Owner is { } list) list.SelectedItem = owner.Content; }
        public void AddToSelection() => Select();
        public void RemoveFromSelection() { if (owner.Owner is { } list && owner.IsSelected) list.SelectedItem = null; }
    }
}
