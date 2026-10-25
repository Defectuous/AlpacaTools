using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using AlpacaDashboard.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlpacaDashboard.ViewModels;

public enum MainTab { Portfolio, Screener }

public sealed partial class MainViewModel : ObservableObject
{
    // Each account decides what is actually due (see AccountViewModel's cadence); this is just the heartbeat.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _timer;
    private bool _isPaused;

    public MainViewModel()
    {
        Search = new SymbolSearchViewModel(() => SelectedAccount, ShowSymbol);
        try
        {
            foreach (var profile in AccountStore.Load())
                Accounts.Add(new AccountViewModel(profile));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load saved accounts:\n{ex.Message}", "Alpaca Dashboard",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SelectedAccount = Accounts.FirstOrDefault();

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        _ = TickAsync();
    }

    /// <summary>Stops polling while the window is minimized; catches up on whatever is stale when restored.</summary>
    public void SetPaused(bool paused)
    {
        if (_isPaused == paused) return;
        _isPaused = paused;
        if (!paused) _ = TickAsync();
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(EditAccountCommand), nameof(RemoveAccountCommand))]
    private AccountViewModel? _selectedAccount;

    public bool HasSelection => SelectedAccount is not null;

    partial void OnSelectedAccountChanged(AccountViewModel? oldValue, AccountViewModel? newValue)
    {
        oldValue?.Deactivate();
        newValue?.Activate();
        if (SelectedTab == MainTab.Screener) newValue?.Screener.EnsureLoaded();
    }

    /// <summary>Which view the main area shows for the selected account; kept when switching accounts.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPortfolioTab), nameof(IsScreenerTab))]
    private MainTab _selectedTab = MainTab.Portfolio;

    public bool IsPortfolioTab => SelectedTab == MainTab.Portfolio;
    public bool IsScreenerTab => SelectedTab == MainTab.Screener;

    [RelayCommand]
    private void SetTab(MainTab tab)
    {
        SelectedTab = tab;
        if (tab == MainTab.Screener) SelectedAccount?.Screener.EnsureLoaded();
    }

    /// <summary>Symbol search in the tab strip, over every tradable stock and crypto pair.</summary>
    public SymbolSearchViewModel Search { get; }

    /// <summary>A search pick opens its chart where the current tab shows charts.</summary>
    private void ShowSymbol(SymbolAsset asset) => ShowSymbol(asset.Symbol, asset.IsCrypto);

    /// <summary>A symbol's chart where the current tab shows charts (search picks, controller keys).</summary>
    public void ShowSymbol(string symbol, bool isCrypto)
    {
        if (SelectedAccount is not { } account) return;
        if (SelectedTab == MainTab.Screener) account.Screener.ShowSymbolChart(symbol, isCrypto);
        else account.ShowSymbolChart(symbol, isCrypto);
    }

    private async Task TickAsync()
    {
        if (_isPaused) return;
        var tasks = Accounts.Where(a => a != SelectedAccount).Select(a => a.RefreshSummaryIfDueAsync()).ToList();
        if (SelectedAccount is not null) tasks.Add(SelectedAccount.TickAsync());
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void AddAccount()
    {
        var editor = new AccountEditorWindow(new AccountProfile()) { Owner = Application.Current.MainWindow };
        if (editor.ShowDialog() != true) return;

        var vm = new AccountViewModel(editor.Result);
        Accounts.Add(vm);
        Save();
        SelectedAccount = vm;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditAccount()
    {
        if (SelectedAccount is not { } vm) return;
        var editor = new AccountEditorWindow(vm.Profile) { Owner = Application.Current.MainWindow };
        if (editor.ShowDialog() != true) return;

        vm.UpdateProfile(editor.Result); // reconnects and reloads if it's the account on screen
        Save();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveAccount()
    {
        if (SelectedAccount is not { } vm) return;
        var confirm = MessageBox.Show($"Remove \"{vm.Name}\" and its saved API keys?", "Remove account",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        Accounts.Remove(vm);
        vm.Dispose();
        try { LedgerStore.Default.DeleteAccount(vm.Profile.Id); }
        catch (Exception) { /* the ledger is a cache of Alpaca's history; a leftover copy does no harm */ }
        Save();
        SelectedAccount = Accounts.FirstOrDefault();
    }

    private void Save() => AccountStore.Save(Accounts.Select(a => a.Profile));
}
