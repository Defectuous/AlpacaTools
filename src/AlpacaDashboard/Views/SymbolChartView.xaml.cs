using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlpacaDashboard.ViewModels;
using LiveChartsCore.Drawing;

namespace AlpacaDashboard.Views;

/// <summary>Candlestick + volume chart for one symbol; hosted by <see cref="SymbolChartWindow"/> and inline on the dashboard.</summary>
public partial class SymbolChartView : UserControl
{
    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(SymbolChartView));

    /// <summary>When set, a ✕ button in the top-right corner runs this command.</summary>
    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public SymbolChartView()
    {
        InitializeComponent();
    }

    private SymbolChartViewModel? Vm => DataContext as SymbolChartViewModel;

    // ----- Drawing trendlines: the view turns mouse pixels into chart coordinates; the view model keeps the lines -----

    private const double SnapPixels = 10;
    private const double HitPixels = 6;

    private void PriceChart_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { IsDrawing: true } vm) return;
        var (x, y) = ToData(e.GetPosition(PriceChart));
        vm.DrawClick(x, y);
        e.Handled = true; // not a pan or a tooltip click
    }

    private void PriceChart_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (Vm is not { IsDrawing: true } vm) return;
        var (x, y) = ToData(e.GetPosition(PriceChart));
        vm.DrawMove(x, y);
    }

    /// <summary>Cancels drawing, or deletes the drawn line under the pointer.</summary>
    private void PriceChart_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.IsDrawing)
        {
            vm.IsDrawing = false;
            e.Handled = true;
            return;
        }
        var p = e.GetPosition(PriceChart);
        foreach (var (id, line) in vm.DrawnSegments)
        {
            if (DistanceToSegment(p, ToPixels(line.X1, line.Y1), ToPixels(line.X2, line.Y2)) > HitPixels) continue;
            vm.DeleteLine(id);
            e.Handled = true;
            return;
        }
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm is not { IsDrawing: true } vm) return;
        vm.IsDrawing = false;
        e.Handled = true;
    }

    /// <summary>The bar under the pointer, and its high/low/open/close when the pointer is within a few pixels of one.</summary>
    private (double X, double Y) ToData(Point p)
    {
        var data = PriceChart.ScalePixelsToData(new LvcPointD(p.X, p.Y), 0, 0);
        var x = Math.Round(data.X);
        var y = data.Y;
        var nearest = SnapPixels;
        foreach (var price in Vm?.PricesAt(x) ?? [])
        {
            var distance = Math.Abs(ToPixels(x, price).Y - p.Y);
            if (distance >= nearest) continue;
            nearest = distance;
            y = price;
        }
        return (x, y);
    }

    private Point ToPixels(double x, double y)
    {
        var pixels = PriceChart.ScaleDataToPixels(new LvcPointD(x, y), 0, 0);
        return new Point(pixels.X, pixels.Y);
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((p - a).X * ab.X + (p - a).Y * ab.Y) / lengthSquared, 0, 1);
        return (p - (a + t * ab)).Length;
    }
}
