using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Leo.App.Controls;

/// <summary>
/// Lays children out left-to-right and wraps to a new line when the width runs out (WinUI has no built-in
/// WrapPanel). Used for card action buttons so they never get clipped in narrow windows.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 6;
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (var child in Children)
        {
            // Buttons default to VerticalAlignment=Center; stretch so ArrangeOverride can equalize line heights.
            if (child is FrameworkElement { VerticalAlignment: not VerticalAlignment.Stretch } fe)
                fe.VerticalAlignment = VerticalAlignment.Stretch;
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) continue; // collapsed

            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
            width = Math.Max(width, x - HorizontalSpacing);
        }
        return new Size(width, y + lineHeight);
    }

    /// <summary>
    /// Every child on a line gets that line's full height, so an icon-only button (↑ ↓) is exactly as tall as
    /// its text-labelled neighbours.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var line = new List<UIElement>();
        double x = 0, y = 0, lineHeight = 0;

        void Flush()
        {
            double lx = 0;
            foreach (var c in line)
            {
                c.Arrange(new Rect(lx, y, c.DesiredSize.Width, lineHeight));
                lx += c.DesiredSize.Width + HorizontalSpacing;
            }
            line.Clear();
        }

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                Flush();
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            line.Add(child);
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        Flush();
        return finalSize;
    }
}
