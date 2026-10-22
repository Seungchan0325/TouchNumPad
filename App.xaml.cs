using System.IO;
using Microsoft.UI.Xaml;
using TouchNumPad.Services;

namespace TouchNumPad;

public partial class App : Application
{
    private const string MutexName = "Local\\TouchNumPad_SingleInstance_Mutex";
    private const string ShowEventName = "Local\\TouchNumPad_ShowWindow_Event";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showWindowEvent;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // 1. Immediately heal any system cursor corruption left by previous runs or crashes
        CursorVisibilityService.RestoreSystemCursors();

        // 2. Global exception handlers to prevent abrupt process termination & 0xc000027b stowed exceptions
        UnhandledException += OnWinUIUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnWinUIUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogException("WinUI.UnhandledException", e.Exception);
        CursorVisibilityService.RestoreSystemCursors();
        e.Handled = true; // Mark as handled to prevent process crash
    }

    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("AppDomain.UnhandledException", ex);
        }
        CursorVisibilityService.RestoreSystemCursors();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("TaskScheduler.UnobservedTaskException", e.Exception);
        CursorVisibilityService.RestoreSystemCursors();
        e.SetObserved();
    }

    private static void LogException(string source, Exception ex)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "TouchNumPad");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "crash.log");
            string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {ex}\n\n";
            File.AppendAllText(file, text);
        }
        catch
        {
            // Ignore logging errors
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    using var existingEvent = EventWaitHandle.OpenExisting(ShowEventName);
                    existingEvent.Set();
                }
                catch
                {
                    // Ignore if event is not ready yet
                }

                Environment.Exit(0);
                return;
            }

            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            _window = new MainWindow();
            _window.Activate();
            _window.InitializeAndShow();

            ThreadPool.RegisterWaitForSingleObject(
                _showWindowEvent,
                (_, _) =>
                {
                    _window?.DispatcherQueue.TryEnqueue(() => _window.ShowNumpadWindow());
                },
                null,
                -1,
                false);
        }
        catch (Exception ex)
        {
            LogException("App.OnLaunched", ex);
            CursorVisibilityService.RestoreSystemCursors();
            throw;
        }
    }
}
