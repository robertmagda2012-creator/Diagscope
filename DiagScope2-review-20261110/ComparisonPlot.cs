using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DiagScope2;

public sealed class ComparisonPlot : FrameworkElement
{
    readonly Channel first;
    readonly Channel second;
    readonly double offset;
    readonly double? selectedStart;
    readonly double? selectedEnd;

    public ComparisonPlot(Channel first, Channel second, double offset, double? start, double? end)
    {
        this.first = first;
        this.second = second;
        this.offset = offset;
        selectedStart = start;
        selectedEnd = end;
        Height = 340;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        if (width < 240) return;

        const double left = 95, top = 35, bottom = 286;
        double right = width - 95;
        var area = new Rect(left, top, right - left, bottom - top);
        var a = Samples(first, 0);
        var b = Samples(second, offset);
        dc.DrawRoundedRectangle(MainWindow.B("#0B1727"), null, new Rect(0, 0, width, Height), 8, 8);
        if (a.Length == 0 || b.Length == 0)
        {
            Text(dc, "Porównanie wymaga dwóch przebiegów liczbowych.", 20, 20, Brushes.White);
            return;
        }

        double start = Math.Min(a.Min(x => x.Time), b.Min(x => x.Time));
        double end = Math.Max(a.Max(x => x.Time), b.Max(x => x.Time));
        if (end <= start) end = start + 1;
        double X(double time) => left + (time - start) / (end - start) * area.Width;
        var scaleA = Scale(a);
        var scaleB = Scale(b);
        if (first.Unit.Length > 0 && first.Unit == second.Unit)
            scaleA = scaleB = (Math.Min(scaleA.Min, scaleB.Min), Math.Max(scaleA.Max, scaleB.Max));

        var grid = new Pen(MainWindow.B("#314962"), 1);
        for (int i = 0; i <= 5; i++)
        {
            double x = left + i * area.Width / 5;
            dc.DrawLine(grid, new(x, top), new(x, bottom));
            Text(dc, $"{start + (end - start) * i / 5:F2} s", x - 23, bottom + 10, Brushes.White);
        }
        for (int i = 0; i <= 4; i++)
        {
            double y = bottom - i * area.Height / 4;
            dc.DrawLine(grid, new(left, y), new(right, y));
            Text(dc, $"{scaleA.Min + (scaleA.Max - scaleA.Min) * i / 4:G4}", 12, y - 9, Plot.Palette[0]);
            Text(dc, $"{scaleB.Min + (scaleB.Max - scaleB.Min) * i / 4:G4}", right + 15, y - 9, Plot.Palette[1]);
        }
        Text(dc, "A · " + first.Unit, 12, 10, Plot.Palette[0]);
        Text(dc, "B · " + second.Unit, right + 15, 10, Plot.Palette[1]);
        dc.PushClip(new RectangleGeometry(area));
        if (selectedStart is double from && selectedEnd is double to && to >= from)
            dc.DrawRectangle(MainWindow.B("#254666"), null, new Rect(X(from), top, Math.Max(2, X(to) - X(from)), area.Height));
        Draw(a, scaleA, Analyzer.Analyze(first).Median, Plot.Palette[0]);
        Draw(b, scaleB, Analyzer.Analyze(second).Median, Plot.Palette[1]);
        dc.Pop();

        void Draw((double Time, double Value, int Index)[] points, (double Min, double Max) scale, double median, Brush color)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                (double Time, double Value, int Index)? previous = null;
                foreach (var sample in points)
                {
                    var point = new Point(X(sample.Time), bottom - (sample.Value - scale.Min) / (scale.Max - scale.Min) * area.Height);
                    if (previous is not { } p || sample.Index != p.Index + 1 || sample.Time <= p.Time ||
                        (median > 0 && sample.Time - p.Time > 3 * median))
                        g.BeginFigure(point, false, false);
                    else
                        g.LineTo(point, true, false);
                    previous = sample;
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(color, 1.5), geometry);
        }
    }

    static (double Time, double Value, int Index)[] Samples(Channel channel, double shift) =>
        channel.Samples.Select((point, index) => (point, index))
            .Where(p => p.point.Time is double t && double.IsFinite(t) && p.point.Value is double v && double.IsFinite(v))
            .Select(p => (p.point.Time!.Value - shift, p.point.Value!.Value, p.index)).ToArray();

    static (double Min, double Max) Scale((double Time, double Value, int Index)[] points)
    {
        double min = points.Min(x => x.Value), max = points.Max(x => x.Value);
        return min == max ? (min - .5, max + .5) : (min, max);
    }

    void Text(DrawingContext dc, string value, double x, double y, Brush brush) =>
        dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
}