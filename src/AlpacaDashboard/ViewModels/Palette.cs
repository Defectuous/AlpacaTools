using SkiaSharp;

namespace AlpacaDashboard.ViewModels;

/// <summary>
/// Chart colors (dark-mode steps of the validated categorical palette + chrome).
/// Keep these in sync with the brushes in App.xaml.
/// </summary>
public static class Palette
{
    public static readonly SKColor Surface = SKColor.Parse("#1a1a19");
    public static readonly SKColor Grid = SKColor.Parse("#2c2c2a");
    public static readonly SKColor Muted = SKColor.Parse("#898781");
    public static readonly SKColor Secondary = SKColor.Parse("#c3c2b7");
    public static readonly SKColor OtherSlice = SKColor.Parse("#5f5e5a");

    /// <summary>Categorical slots in fixed order — never cycled. A 9th series folds into "Other".</summary>
    public static readonly SKColor[] Categorical =
    [
        SKColor.Parse("#3987e5"), // blue
        SKColor.Parse("#d95926"), // orange
        SKColor.Parse("#199e70"), // aqua
        SKColor.Parse("#c98500"), // yellow
        SKColor.Parse("#d55181"), // magenta
        SKColor.Parse("#008300"), // green
        SKColor.Parse("#9085e9"), // violet
        SKColor.Parse("#e66767"), // red
    ];

    // Price direction (status palette; always paired with candle shape or an arrow, never color alone).
    public static readonly SKColor Up = SKColor.Parse("#0ca30c");
    public static readonly SKColor Down = SKColor.Parse("#d03b3b");

    public static System.Windows.Media.SolidColorBrush ToBrush(SKColor c)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.Red, c.Green, c.Blue));
        brush.Freeze();
        return brush;
    }

    /// <summary>Axis labeler for index-based time series (gaps like nights/weekends collapsed).</summary>
    public static string FormatIndexTime(DateTime[] times, double index, bool intraday)
    {
        var i = (int)Math.Round(index);
        if (i < 0 || i >= times.Length) return "";
        return intraday ? times[i].ToString("ddd h:mm tt") : times[i].ToString("MMM d, yy");
    }
}
