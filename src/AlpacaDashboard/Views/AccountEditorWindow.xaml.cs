using System.Windows;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;

namespace AlpacaDashboard.Views;

public partial class AccountEditorWindow : Window
{
    private readonly AccountProfile _original;

    public AccountEditorWindow(AccountProfile profile)
    {
        DarkTitleBar.Apply(this);
        InitializeComponent();
        _original = profile;
        NameBox.Text = profile.Name;
        KeyIdBox.Text = profile.KeyId;
        SecretBox.Password = profile.SecretKey;
        PaperCheck.IsChecked = profile.IsPaper;
        PlanCombo.SelectedIndex = (int)profile.DataPlan;
        BotPriorityCheck.IsChecked = profile.BotPriority;
        Loaded += (_, _) => NameBox.Focus();
    }

    public AccountProfile Result { get; private set; } = new();

    private AccountProfile BuildProfile() => new()
    {
        Id = _original.Id,
        Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Alpaca account" : NameBox.Text.Trim(),
        KeyId = KeyIdBox.Text.Trim(),
        SecretKey = SecretBox.Password.Trim(),
        IsPaper = PaperCheck.IsChecked == true,
        DataPlan = (MarketDataPlanSetting)Math.Max(0, PlanCombo.SelectedIndex),
        BotPriority = BotPriorityCheck.IsChecked == true,
    };

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfile();
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        StatusText.Text = "Connecting…";
        try
        {
            using var client = new AlpacaClient(profile);
            var account = await client.GetAccountAsync();
            string planText;
            try
            {
                using var data = new MarketDataClient(profile);
                planText = DataFeedProfile.For(await data.DetectPlanAsync()).PlanName;
            }
            catch (Exception ex)
            {
                planText = $"unknown ({ex.Message})";
            }
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("GoodBrush");
            StatusText.Text = $"✓ Connected: account {account.AccountNumber} ({account.Status}), equity {account.Equity:C}\n" +
                              $"   Market data plan detected: {planText}";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("BadBrush");
            StatusText.Text = $"✕ {ex.Message}";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfile();
        if (profile.KeyId.Length == 0 || profile.SecretKey.Length == 0)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("BadBrush");
            StatusText.Text = "✕ Both the key ID and secret key are required.";
            return;
        }
        Result = profile;
        DialogResult = true;
    }
}
