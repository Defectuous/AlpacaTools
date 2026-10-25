using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
}
