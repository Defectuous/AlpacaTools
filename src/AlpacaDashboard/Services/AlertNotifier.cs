using System.Drawing;
using Forms = System.Windows.Forms;

namespace AlpacaDashboard.Services;

/// <summary>
/// Tray icon that shows triggered alerts as Windows notifications (Windows 10/11 render the balloon as a toast,
/// listed in the notification center). Shown only while there are alerts, so it isn't clutter otherwise.
/// </summary>
public sealed class AlertNotifier : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public AlertNotifier()
    {
        _icon = new Forms.NotifyIcon { Text = "Alpaca Dashboard", Icon = LoadIcon() };
        _icon.BalloonTipClicked += (_, _) => Clicked?.Invoke();
        _icon.DoubleClick += (_, _) => Clicked?.Invoke();
    }

    /// <summary>The notification or the tray icon was clicked.</summary>
    public event Action? Clicked;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>Tray tooltip; Windows allows at most 63 characters.</summary>
    public string Tooltip
    {
        set => _icon.Text = value.Length > 63 ? value[..63] : value;
    }

    public void Show(string title, string text)
    {
        _icon.Visible = true; // a hidden icon can't show a notification
        _icon.ShowBalloonTip(10_000, Truncate(title, 63), Truncate(text, 255), Forms.ToolTipIcon.Info);
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    private static Icon LoadIcon()
    {
        try
        {
            return Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application : SystemIcons.Application;
        }
        catch (Exception)
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false; // otherwise a dead icon lingers in the tray until hovered
        _icon.Dispose();
    }
}
