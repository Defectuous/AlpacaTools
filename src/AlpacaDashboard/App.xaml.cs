using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using AlpacaDashboard.Services;

namespace AlpacaDashboard;

public partial class App : Application
{
    private static readonly string ErrorLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlpacaTools", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // "AlpacaDashboard.exe --do buy SPY": for controller buttons that run a program (Soomfon, Stream Deck's
        // "Open" action). Hands the command to the dashboard already running, or starts one that runs it.
        var command = ParseCommand(e.Args);
        if (command is not null)
        {
            AllowSetForegroundWindow(AsfwAny); // let the running dashboard come to the front
            var result = Task.Run(() => ControllerServer.SendToRunningAsync(command)).GetAwaiter().GetResult();
            if (result is not null)
            {
                if (result.Length > 0)
                    MessageBox.Show(result, "Alpaca Dashboard", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        LiveCharts.Configure(config => config.AddDarkTheme());

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError("Unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError("Unobserved task", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        MainWindow = new MainWindow(command);
        MainWindow.Show();
    }

    private static ControllerCommand? ParseCommand(string[] args)
    {
        var at = Array.FindIndex(args, a => a is "--do" or "/do");
        if (at < 0 || at + 1 >= args.Length) return null;
        var rest = args[(at + 2)..];
        return new ControllerCommand(args[at + 1], rest.Length > 0 ? string.Join(' ', rest) : null);
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("UI", e.Exception);
        MessageBox.Show($"Something went wrong:\n{e.Exception.Message}\n\nDetails were written to {ErrorLogPath}",
            "Alpaca Dashboard", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // keep the dashboard running
    }

    internal static void LogError(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:O}] {source}: {ex}\n\n");
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
