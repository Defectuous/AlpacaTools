using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AlpacaDashboard.Services;
using AlpacaDashboard.ViewModels;

namespace AlpacaDashboard;

public partial class MainWindow : Window
{
    private ControllerHost? _controller;

    /// <param name="startupCommand">A controller command that started the app ("--do"), run once the window is up.</param>
    public MainWindow(ControllerCommand? startupCommand = null)
    {
        DarkTitleBar.Apply(this);
        InitializeComponent();
        // No polling while minimized.
        StateChanged += (_, _) => (DataContext as MainViewModel)?.SetPaused(WindowState == WindowState.Minimized);
        Loaded += async (_, _) =>
        {
            _controller = new ControllerHost((MainViewModel)DataContext, this);
            if (_controller.Error is { } listenError) App.LogError("Controller", new InvalidOperationException(listenError));
            if (startupCommand is not null && await _controller.RunAsync(startupCommand) is { } error)
                MessageBox.Show(this, error, "Alpaca Dashboard", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        Closed += (_, _) =>
        {
            _controller?.Dispose();
            (DataContext as MainViewModel)?.Dispose(); // removes the alerts tray icon
        };
    }

    /// <summary>Focuses the symbol search, optionally with text already typed.</summary>
    public void FocusSearch(string? query = null)
    {
        if (DataContext is not MainViewModel { HasSelection: true } vm) return;
        if (!string.IsNullOrEmpty(query)) vm.Search.Query = query;
        SearchBox.FocusInput();
    }

    /// <summary>Ctrl+F jumps to the symbol search.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && DataContext is MainViewModel { HasSelection: true })
        {
            FocusSearch();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>A single click on a position row shows its chart in place of the portfolio value card.</summary>
    private void PositionsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Row buttons (Chart, Trade) mark the click handled, so only clicks on the row itself get here.
        for (var d = e.OriginalSource as DependencyObject; d is not null and not DataGrid; d = VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridRow { Item: PositionRowViewModel row } && PositionsGrid.DataContext is AccountViewModel account)
            {
                account.ShowInlineChartCommand.Execute(row);
                return;
            }
        }
    }
}
