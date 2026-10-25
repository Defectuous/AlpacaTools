using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AlpacaDashboard.ViewModels;

namespace AlpacaDashboard.Views;

/// <summary>Type-ahead symbol search; the keyboard stays in the text box while arrows move through the dropdown.</summary>
public partial class SymbolSearchBox : UserControl
{
    public SymbolSearchBox()
    {
        InitializeComponent();
    }

    private SymbolSearchViewModel? Search => DataContext as SymbolSearchViewModel;

    public void FocusInput()
    {
        Input.Focus();
        Input.SelectAll();
    }

    private void Input_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Search?.Prefetch();
        Search?.Reopen();
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Search is not { } search) return;
        switch (e.Key)
        {
            case Key.Down:
                search.MoveSelection(1);
                break;
            case Key.Up:
                search.MoveSelection(-1);
                break;
            case Key.Enter:
                search.PickSelected();
                break;
            case Key.Escape when search.IsOpen:
                search.Close();
                break;
            case Key.Escape:
                search.Query = "";
                Keyboard.ClearFocus();
                break;
            default:
                return;
        }
        if (search.Selected is { } selected) List.ScrollIntoView(selected);
        e.Handled = true;
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null and not ListBox; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem { DataContext: SymbolSuggestion suggestion })
            {
                Search?.Pick(suggestion);
                return;
            }
        }
    }
}
