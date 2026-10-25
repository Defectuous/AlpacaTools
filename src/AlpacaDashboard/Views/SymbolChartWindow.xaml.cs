using System.Windows;
using AlpacaDashboard.ViewModels;

namespace AlpacaDashboard.Views;

public partial class SymbolChartWindow : Window
{
    public SymbolChartWindow(SymbolChartViewModel viewModel)
    {
        DarkTitleBar.Apply(this);
        InitializeComponent();
        DataContext = viewModel;
    }
}
