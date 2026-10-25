using System.Windows;
using AlpacaDashboard.ViewModels;

namespace AlpacaDashboard.Views;

public partial class TradePositionWindow : Window
{
    public TradePositionWindow(TradePositionViewModel viewModel)
    {
        DarkTitleBar.Apply(this);
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    // IsCancel only closes dialogs opened with ShowDialog; this window is modeless.
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
