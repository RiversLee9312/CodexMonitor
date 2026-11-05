using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CodexMonitor.App;

/// <summary>A bounded, variable-height virtual layout in the inner scroller's coordinate space.</summary>
internal sealed partial class ViewportStackLayout(RepeaterList owner, double estimatedHeight = 40) : VirtualizingLayout
{
    private readonly Dictionary<string, double> _measured = new(StringComparer.Ordinal);
    private readonly Dictionary<int, UIElement> _elements = [];
    private readonly HeightIndex _heights = new();
    private string[] _keys = [];
    private bool _dirty = true;
    private double _width = -1;
    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    { _dirty = true; InvalidateMeasure(); }
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : Math.Max(1, owner.ActualWidth);
        if (Math.Abs(_width - width) > .5) { _width = width; _dirty = true; _measured.Clear(); }
        if (_dirty || _keys.Length != context.ItemCount)
        {
            _keys = new string[context.ItemCount]; var values = new double[context.ItemCount];
            for (int i = 0; i < context.ItemCount; i++) { _keys[i] = owner.ItemKey(i); values[i] = _measured.GetValueOrDefault(_keys[i], estimatedHeight); }
            _heights.Reset(values); _dirty = false;
            if (_measured.Count > context.ItemCount * 2 + 100) { var current = _keys.ToHashSet(StringComparer.Ordinal); foreach (var key in _measured.Keys.Where(k => !current.Contains(k)).ToArray()) _measured.Remove(key); }
        }
        context.LayoutOrigin = new(0, 0); _elements.Clear();
        if (context.ItemCount == 0) return new(width, 0);
        var viewport = owner.Scroll.ViewportHeight > 0 ? owner.Scroll.ViewportHeight : 480;
        for (int pass = 0; pass < 2; pass++)
        {
            var offset = Math.Clamp(owner.Scroll.VerticalOffset, 0, Math.Max(0, _heights.Total - viewport));
            var start = _heights.At(Math.Max(0, offset - viewport * .5));
            var end = _heights.At(Math.Min(_heights.Total, offset + viewport * 1.5));
            for (int i = start; i <= end; i++) Measure(i);
        }
        if (context.RecommendedAnchorIndex is var anchor && anchor >= 0 && anchor < context.ItemCount) Measure(anchor);
        return new(width, _heights.Total);

        void Measure(int index)
        {
            if (_elements.ContainsKey(index)) return;
            var element = context.GetOrCreateElementAt(index); element.Measure(new(width, double.PositiveInfinity));
            var height = Math.Max(1, element.DesiredSize.Height);
            _measured[_keys[index]] = height; _heights.Set(index, height); _elements[index] = element;
        }
    }
    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        foreach (var (index, element) in _elements) element.Arrange(new Rect(0, _heights.Before(index), finalSize.Width, _heights[index]));
        return new(finalSize.Width, _heights.Total);
    }
    // Prefix sums and viewport lookup stay logarithmic as deliberately loaded history grows.
    internal sealed class HeightIndex
    {
        private double[] _values = [], _tree = new double[1];
        internal double this[int index] => _values[index];
        internal double Total => Before(_values.Length);
        internal void Reset(double[] values)
        {
            _values = values; _tree = new double[values.Length + 1];
            for (int i = 1; i < _tree.Length; i++) { _tree[i] += values[i - 1]; var parent = i + (i & -i); if (parent < _tree.Length) _tree[parent] += _tree[i]; }
        }
        internal double Before(int index) { double sum = 0; for (int i = index; i > 0; i -= i & -i) sum += _tree[i]; return sum; }
        internal void Set(int index, double height) { var delta = height - _values[index]; _values[index] = height; for (int i = index + 1; i < _tree.Length; i += i & -i) _tree[i] += delta; }
        internal int At(double offset)
        {
            int index = 0, step = 1; double sum = 0;
            while (step < _values.Length) step <<= 1;
            for (; step > 0; step >>= 1) { var next = index + step; if (next < _tree.Length && sum + _tree[next] <= offset) { index = next; sum += _tree[next]; } }
            return Math.Min(index, Math.Max(0, _values.Length - 1));
        }
    }
}
