using System.Windows;
using AlpacaDashboard.ViewModels;

namespace AlpacaDashboard.Views;

public partial class BuyWindow : Window
{
    public BuyWindow(BuyViewModel viewModel)
    {
        DarkTitleBar.Apply(this);
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => AmountBox.Focus();
    }

    // IsCancel only closes dialogs opened with ShowDialog; this window is modeless.
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
