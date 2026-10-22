using System.Windows;
using System.Windows.Controls;

namespace GetPdf.Controls;

/// <summary>
/// Browser-like document tab strip: tabs keep their natural width up to
/// <see cref="MaxTabWidth"/>, shrink evenly to <see cref="MinTabWidth"/> when space
/// runs out, and beyond that scroll so the selected tab always stays visible.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public double MinTabWidth { get; set; } = 120;
    public double MaxTabWidth { get; set; } = 260;
    private double[] _widths = [];
    private double _offset;

    public TabStripPanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = InternalChildren.Count;
        _widths = new double[count];
        double height = 0, natural = 0;
        for (int i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(MaxTabWidth, availableSize.Height));
            _widths[i] = Math.Clamp(child.DesiredSize.Width, MinTabWidth, MaxTabWidth);
            natural += _widths[i];
            height = Math.Max(height, child.DesiredSize.Height);
        }
        double limit = availableSize.Width;
        if (count > 0 && double.IsFinite(limit) && natural > limit)
        {
            double even = Math.Max(MinTabWidth, limit / count);
            for (int i = 0; i < count; i++)
            {
                _widths[i] = Math.Min(_widths[i], even);
                InternalChildren[i].Measure(new Size(_widths[i], availableSize.Height));
            }
            natural = _widths.Sum();
        }
        return new Size(double.IsFinite(limit) ? Math.Min(natural, limit) : natural, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double total = _widths.Sum();
        int selected = -1;
        for (int i = 0; i < InternalChildren.Count; i++)
            if (InternalChildren[i] is TabItem { IsSelected: true }) selected = i;
        if (total <= finalSize.Width) _offset = 0;
        else if (selected >= 0 && selected < _widths.Length)
        {
            double start = _widths.Take(selected).Sum(), end = start + _widths[selected];
            if (start < _offset) _offset = start;
            else if (end > _offset + finalSize.Width) _offset = end - finalSize.Width;
            _offset = Math.Clamp(_offset, 0, total - finalSize.Width);
        }
        double x = -_offset;
        for (int i = 0; i < InternalChildren.Count && i < _widths.Length; i++)
        {
            InternalChildren[i].Arrange(new Rect(x, 0, _widths[i], finalSize.Height));
            x += _widths[i];
        }
        return finalSize;
    }
}

/// <summary>Content slots for the ShellTabs header: after the last tab, and right-aligned.</summary>
public sealed class ShellHeaderSlots
{
    public object? Leading { get; init; }
    public object? Trailing { get; init; }
}
