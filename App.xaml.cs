using Microsoft.UI.Xaml;

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
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), ex.ToString());
            throw;
        }
    }
}
