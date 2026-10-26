using System.Windows.Media;
using AlpacaDashboard.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace AlpacaDashboard.ViewModels;

public enum ChartOverlay { Trend, Sma20, Sma50, Sma200, Ema9, Ema21, Bollinger, Vwap, TrendChannel, AutoTrend, Levels }

/// <summary>One entry in a price chart's Overlays dropdown. Each overlay keeps its color whatever else is shown.</summary>
public sealed partial class OverlayOption : ObservableObject
{
    public required ChartOverlay Kind { get; init; }
    public required string Name { get; init; }
    public required string Hint { get; init; }
    public required SKColor Color { get; init; }
    public LineDash Dash { get; init; }

    public Brush Swatch => Palette.ToBrush(Color);
    public DoubleCollection? DashArray => Dashes.Legend(Dash);

    [ObservableProperty] private bool _isChecked;
    /// <summary>False when the overlay doesn't apply to the range on screen (VWAP on daily bars).</summary>
    [ObservableProperty] private bool _isAvailable = true;

    /// <summary>The dropdown's options, in menu order.</summary>
    public static OverlayOption[] CreateAll() =>
    [
        new() { Kind = ChartOverlay.Trend, Name = "Trend line", Color = Palette.Ink, Dash = LineDash.Long,
                Hint = "Least-squares line through the closes on screen" },
        new() { Kind = ChartOverlay.TrendChannel, Name = "Trend channel", Color = Palette.Secondary, Dash = LineDash.Short,
                Hint = "The trend line with parallel lines 2 standard deviations above and below it" },
        new() { Kind = ChartOverlay.AutoTrend, Name = "Auto trendlines", Color = Palette.Secondary,
                Hint = "Support through two swing lows and resistance through two swing highs, each unbroken by any close since" },
        new() { Kind = ChartOverlay.Levels, Name = "Support / resistance", Color = Palette.Muted, Dash = LineDash.Dotted,
                Hint = "Price levels the chart turned at more than once (the two nearest above and below the last price)" },
        new() { Kind = ChartOverlay.Sma20, Name = "SMA 20", Color = Palette.Categorical[0], Hint = "Simple moving average of the last 20 closes" },
        new() { Kind = ChartOverlay.Sma50, Name = "SMA 50", Color = Palette.Categorical[1], Hint = "Simple moving average of the last 50 closes" },
        new() { Kind = ChartOverlay.Sma200, Name = "SMA 200", Color = Palette.Categorical[2], Hint = "Simple moving average of the last 200 closes" },
        new() { Kind = ChartOverlay.Ema9, Name = "EMA 9", Color = Palette.Categorical[3], Hint = "Exponential moving average, 9 bars" },
        new() { Kind = ChartOverlay.Ema21, Name = "EMA 21", Color = Palette.Categorical[4], Hint = "Exponential moving average, 21 bars" },
        new() { Kind = ChartOverlay.Bollinger, Name = "Bollinger Bands", Color = Palette.Muted,
                Hint = "SMA 20 ± 2 standard deviations" },
        new() { Kind = ChartOverlay.Vwap, Name = "VWAP", Color = Palette.Categorical[6],
                Hint = "Volume-weighted average price, restarting each day. Intraday ranges only (1D, 1W, 1M)" },
    ];
}

public enum LineDash { Solid, Long, Short, Dotted }

/// <summary>One dash style, drawn the same on the chart (pixels) and in legends (multiples of stroke width).</summary>
public static class Dashes
{
    private static readonly DoubleCollection LongLegend = Freeze([3, 2]);
    private static readonly DoubleCollection ShortLegend = Freeze([1.5, 1.5]);
    private static readonly DoubleCollection DottedLegend = Freeze([1, 1.5]);

    public static float[]? Chart(LineDash dash) => dash switch
    {
        LineDash.Long => [6, 4],
        LineDash.Short => [3, 3],
        LineDash.Dotted => [2, 3],
        _ => null,
    };

    public static DoubleCollection? Legend(LineDash dash) => dash switch
    {
        LineDash.Long => LongLegend,
        LineDash.Short => ShortLegend,
        LineDash.Dotted => DottedLegend,
        _ => null,
    };

    private static DoubleCollection Freeze(DoubleCollection c)
    {
        c.Freeze();
        return c;
    }
}

/// <summary>A least-squares line over x = 0..n-1, with the standard deviation of the points around it.</summary>
public readonly record struct LinearFit(double Intercept, double Slope, double Sd)
{
    public double At(double x) => Intercept + Slope * x;
}

/// <summary>A straight line between two points in chart coordinates (x = bar index).</summary>
public readonly record struct Segment(double X1, double Y1, double X2, double Y2)
{
    public double At(double x) => X2 == X1 ? Y1 : Y1 + (Y2 - Y1) * (x - X1) / (X2 - X1);
}

/// <summary>A line in the legend drawn over the price chart.</summary>
public sealed record OverlayLegendItem(string Text, Brush Swatch, DoubleCollection? DashArray);

/// <summary>Indicator math. Each result lines up with the input bars; null where there isn't enough history yet.</summary>
public static class Indicators
{
    public static double?[] Sma(IReadOnlyList<double> values, int period)
    {
        var result = new double?[values.Count];
        double sum = 0;
        for (var i = 0; i < values.Count; i++)
        {
            sum += values[i];
            if (i >= period) sum -= values[i - period];
            if (i >= period - 1) result[i] = sum / period;
        }
        return result;
    }

    /// <summary>Seeded with the SMA of the first <paramref name="period"/> values.</summary>
    public static double?[] Ema(IReadOnlyList<double> values, int period)
    {
        var result = new double?[values.Count];
        if (values.Count < period) return result;
        var k = 2.0 / (period + 1);
        var ema = values.Take(period).Average();
        result[period - 1] = ema;
        for (var i = period; i < values.Count; i++)
        {
            ema = values[i] * k + ema * (1 - k);
            result[i] = ema;
        }
        return result;
    }

    public static (double?[] Middle, double?[] Upper, double?[] Lower) Bollinger(IReadOnlyList<double> values, int period, double width)
    {
        var middle = Sma(values, period);
        var upper = new double?[values.Count];
        var lower = new double?[values.Count];
        for (var i = period - 1; i < values.Count; i++)
        {
            var mean = middle[i]!.Value;
            double squares = 0;
            for (var j = i - period + 1; j <= i; j++) squares += (values[j] - mean) * (values[j] - mean);
            var sd = Math.Sqrt(squares / period);
            upper[i] = mean + width * sd;
            lower[i] = mean - width * sd;
        }
        return (middle, upper, lower);
    }

    /// <summary>Typical price weighted by volume, restarting whenever <paramref name="dayOf"/> changes.</summary>
    public static double?[] Vwap(IReadOnlyList<StockBar> bars, Func<StockBar, DateTime> dayOf)
    {
        var result = new double?[bars.Count];
        double pv = 0, volume = 0;
        DateTime? day = null;
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var d = dayOf(b);
            if (d != day) { pv = 0; volume = 0; day = d; }
            pv += (b.High + b.Low + b.Close) / 3 * b.Volume;
            volume += b.Volume;
            result[i] = volume > 0 ? pv / volume : b.Close;
        }
        return result;
    }

    public static LinearFit? Regression(IReadOnlyList<double> values)
    {
        var n = values.Count;
        if (n < 2) return null;
        var xMean = (n - 1) / 2.0;
        var yMean = values.Average();
        double num = 0, den = 0;
        for (var x = 0; x < n; x++)
        {
            num += (x - xMean) * (values[x] - yMean);
            den += (x - xMean) * (x - xMean);
        }
        var slope = num / den;
        var intercept = yMean - slope * xMean;
        double squares = 0;
        for (var x = 0; x < n; x++)
        {
            var residual = values[x] - (intercept + slope * x);
            squares += residual * residual;
        }
        return new LinearFit(intercept, slope, Math.Sqrt(squares / n));
    }

    /// <summary>
    /// Swing highs and lows: bars whose high (low) is the most extreme of the <paramref name="k"/> bars on each side.
    /// The last k bars can't be confirmed yet, so they're never swings.
    /// </summary>
    public static (List<int> Highs, List<int> Lows) Swings(IReadOnlyList<StockBar> bars, int k)
    {
        var highs = new List<int>();
        var lows = new List<int>();
        for (var i = k; i < bars.Count - k; i++)
        {
            bool isHigh = true, isLow = true;
            for (var j = i - k; j <= i + k && (isHigh || isLow); j++)
            {
                if (j == i) continue;
                // Ties go to the earlier bar, so a flat top counts once.
                if (bars[j].High > bars[i].High || (bars[j].High == bars[i].High && j < i)) isHigh = false;
                if (bars[j].Low < bars[i].Low || (bars[j].Low == bars[i].Low && j < i)) isLow = false;
            }
            if (isHigh) highs.Add(i);
            if (isLow) lows.Add(i);
        }
        return (highs, lows);
    }

    /// <summary>
    /// Price levels where swings bunch up: swing highs and lows within <paramref name="tolerance"/> of each other
    /// form one level, and a level needs at least <paramref name="minTouches"/> of them.
    /// </summary>
    public static List<(double Price, int Touches)> Levels(IReadOnlyList<StockBar> bars, int k, double tolerance, int minTouches = 2)
    {
        var (highs, lows) = Swings(bars, k);
        var prices = highs.Select(i => bars[i].High).Concat(lows.Select(i => bars[i].Low)).Order().ToList();
        var levels = new List<(double, int)>();
        var cluster = new List<double>();
        foreach (var price in prices)
        {
            if (cluster.Count > 0 && price - cluster.Average() > tolerance)
            {
                if (cluster.Count >= minTouches) levels.Add((cluster.Average(), cluster.Count));
                cluster.Clear();
            }
            cluster.Add(price);
        }
        if (cluster.Count >= minTouches) levels.Add((cluster.Average(), cluster.Count));
        return levels;
    }

    /// <summary>
    /// A trendline through two swing points that no close after the first one breaks (by more than
    /// <paramref name="tolerance"/>): under every close for support, over every close for resistance. Tries the
    /// newest swings first and, for each, the earliest partner that works, so the line is recent and long.
    /// </summary>
    public static Segment? TrendLine(IReadOnlyList<StockBar> bars, IReadOnlyList<int> swings, bool support, double tolerance)
    {
        for (var b = swings.Count - 1; b >= Math.Max(1, swings.Count - 3); b--)
        {
            for (var a = 0; a < b; a++)
            {
                int x1 = swings[a], x2 = swings[b];
                var line = new Segment(x1, support ? bars[x1].Low : bars[x1].High, x2, support ? bars[x2].Low : bars[x2].High);
                var holds = true;
                for (var x = x1; x < bars.Count && holds; x++)
                    holds = support ? bars[x].Close >= line.At(x) - tolerance : bars[x].Close <= line.At(x) + tolerance;
                if (holds) return line;
            }
        }
        return null;
    }
}
