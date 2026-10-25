using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AlpacaDashboard;

/// <summary>Asks DWM to draw a dark title bar so the chrome matches the dark UI (Windows 10 20H1+).</summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        };
    }
}
