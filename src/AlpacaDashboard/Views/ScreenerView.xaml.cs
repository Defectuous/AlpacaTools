using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AlpacaDashboard.ViewModels;

namespace AlpacaDashboard.Views;

public partial class ScreenerView : UserControl
{
    public ScreenerView()
    {
        InitializeComponent();
    }

    /// <summary>A single click on a result shows its chart beside the results.</summary>
    private void ResultsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is { } row && DataContext is ScreenerViewModel screener)
            screener.ShowChartCommand.Execute(row);
    }

    /// <summary>
    /// Right-click menu, built from the row's position at the moment it opens: Buy when nothing is held,
    /// otherwise every action the trade window offers for that position.
    /// </summary>
    private void ResultsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not { } row || DataContext is not ScreenerViewModel screener || ResultsGrid.ContextMenu is not { } menu)
        {
            e.Handled = true; // header or empty space: no menu
            return;
        }
        ResultsGrid.SelectedItem = row;

        var items = menu.Items;
        items.Clear();
        var position = screener.PositionFor(row);
        if (position is null)
        {
            items.Add(Item($"Buy {row.Symbol}…", () => screener.BuyCommand.Execute(row)));
        }
        else
        {
            var side = position.IsShort ? "Short" : "Long";
            var qty = Math.Abs(position.Qty).ToString("0.#########", CultureInfo.CurrentCulture);
            var unit = position.IsCrypto ? "" : " shares";
            var pl = position.UnrealizedPl >= 0 ? $"+{position.UnrealizedPl:C2}" : $"−{Math.Abs(position.UnrealizedPl):C2}";
            items.Add(new MenuItem { Header = $"{side} {qty}{unit} · {pl}", IsEnabled = false });
            items.Add(Separator());
            // Adding to a short would be a short sale; this app only opens longs.
            if (!position.IsShort) items.Add(Item("Buy more…", () => screener.BuyCommand.Execute(row)));
            items.Add(Item(position.IsShort ? "Buy to cover…" : "Sell amount…", () => screener.OpenTrade(position, TradeMode.SellQty)));
            items.Add(Item("Set stop limit…", () => screener.OpenTrade(position, TradeMode.StopLimit)));
            items.Add(Item("Liquidate…", () => screener.OpenTrade(position, TradeMode.Liquidate)));
        }
        items.Add(Separator());
        items.Add(Item("Open chart in window", () => screener.OpenChartWindowCommand.Execute(row)));
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private Separator Separator() => new() { Style = (Style)FindResource("MenuSeparator") };

    private static ScreenerRow? RowAt(object source)
    {
        for (var d = source as DependencyObject; d is not null and not DataGrid; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow { Item: ScreenerRow row }) return row;
        return null;
    }
}
