using System.Windows;
using System.Windows.Threading;

namespace WinFlow;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine(" [WinFlow] Starting WinFlow Dictation Engine...");
        Console.WriteLine("==================================================");

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[WinFlow Fatal Error] {ex?.Message}\n{ex?.StackTrace}");
            Console.ResetColor();
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[WinFlow UI Error] {args.Exception.Message}\n{args.Exception.StackTrace}");
            Console.ResetColor();
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WinFlow Task Error] {args.Exception.Message}");
            Console.ResetColor();
            args.SetObserved();
        };

        bool createdNew;
        _mutex = new Mutex(true, "WinFlow_SingleInstance_Mutex_Blake", out createdNew);
        if (!createdNew)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[WinFlow Warning] Another instance of WinFlow is already running in the background!");
            Console.WriteLine("[WinFlow Warning] Please check your system tray (bottom-right) or task manager.");
            Console.ResetColor();
        }

        base.OnStartup(e);

        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
            Console.WriteLine("[WinFlow] Applied system Fluent appearance theme.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WinFlow] Theme initialization note: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
