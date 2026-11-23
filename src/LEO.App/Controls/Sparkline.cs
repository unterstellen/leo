using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Leo.App.Controls;

/// <summary>
/// A small two-line graph (e.g. download and upload over the last 5 minutes). Values are plotted left (oldest) to
/// right (newest) against a shared maximum; NaN points are skipped. Colors follow the Windows theme/accent.
/// </summary>
public sealed partial class Sparkline : Grid
{
    private readonly Polyline _primary = new() { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polyline _secondary = new() { StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round, Opacity = 0.8 };
    private readonly Line _baseline = new() { StrokeThickness = 1, Opacity = 0.25 };

    public Sparkline()
    {
        Children.Add(_baseline);
        Children.Add(_secondary);
        Children.Add(_primary);
        _primary.Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _secondary.Stroke = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _baseline.Stroke = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        SizeChanged += (_, _) => Redraw();
    }

    public static readonly DependencyProperty PrimaryProperty =
        DependencyProperty.Register(nameof(Primary), typeof(IReadOnlyList<double>), typeof(Sparkline), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SecondaryProperty =
        DependencyProperty.Register(nameof(Secondary), typeof(IReadOnlyList<double>), typeof(Sparkline), new PropertyMetadata(null, OnChanged));

    public IReadOnlyList<double>? Primary
    {
        get => (IReadOnlyList<double>?)GetValue(PrimaryProperty);
        set => SetValue(PrimaryProperty, value);
    }

    public IReadOnlyList<double>? Secondary
    {
        get => (IReadOnlyList<double>?)GetValue(SecondaryProperty);
        set => SetValue(SecondaryProperty, value);
    }

    /// <summary>Points the graph spans (fixed, so it scrolls right-to-left as samples arrive).</summary>
    public int Capacity { get; set; } = 150;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Sparkline)d).Redraw();

    private void Redraw()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var a = Primary ?? [];
        var b = Secondary ?? [];
        var max = a.Concat(b).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
        if (max <= 0) max = 1;

        _baseline.X1 = 0; _baseline.X2 = w; _baseline.Y1 = _baseline.Y2 = h - 0.5;
        Fill(_primary, a, w, h, max);
        Fill(_secondary, b, w, h, max);
    }

    private void Fill(Polyline line, IReadOnlyList<double> values, double w, double h, double max)
    {
        var points = new PointCollection();
        var cap = Math.Max(Capacity, values.Count);
        var step = cap > 1 ? w / (cap - 1) : w;
        var offset = cap - values.Count; // newest point sits at the right edge
        for (var i = 0; i < values.Count; i++)
        {
            if (double.IsNaN(values[i])) continue;
            var y = h - 1 - values[i] / max * (h - 3);
            points.Add(new Point((offset + i) * step, y));
        }
        line.Points = points;
    }
}
