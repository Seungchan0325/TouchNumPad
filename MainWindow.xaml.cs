using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TouchNumPad.Interop;
using TouchNumPad.Models;
using TouchNumPad.Services;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace TouchNumPad;

public sealed partial class MainWindow : Window
{
    private const int HotkeyId = 9001;
    private const int BaseWindowWidthDip = 420;

    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private NativeMethods.SubclassProc? _subclassDelegate;
    private SettingsWindow? _settingsWindow;
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    private readonly AppSettings _settings;
    private readonly FeedbackService _feedbackService;
    private readonly TouchpadInputService _touchpadService;
    private readonly TrayIconService _trayService;
    private readonly Dictionary<string, Border> _keyBorders = new();
    private readonly DispatcherTimer _indicatorHideTimer;
    private readonly DispatcherTimer _multiTouchHideTimer;
    private readonly DispatcherTimer _entranceAnimationTimer;

    private const int EntranceAnimationDurationMs = 190;
    private long _entranceAnimStartTick;

    private bool _isNumpadVisible;
    private bool _isNumpadMode;
    private bool _isExiting;
    private string? _highlightedKeyId;

    // Window drag state in mouse mode
    private bool _isDraggingWindow;
    private bool _didMoveWindowDuringDrag;
    private NativeMethods.POINT _dragStartCursorPos;
    private PointInt32 _dragStartWindowPos;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _feedbackService = new FeedbackService(_settings);
        _touchpadService = new TouchpadInputService(_settings, _feedbackService);
        _trayService = new TrayIconService();

        _indicatorHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _indicatorHideTimer.Tick += (_, _) =>
        {
            _indicatorHideTimer.Stop();
            HideTouchIndicator();
        };

        _multiTouchHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _multiTouchHideTimer.Tick += (_, _) =>
        {
            _multiTouchHideTimer.Stop();
            MultiTouchCanvas.Opacity = 0.0;
        };

        _entranceAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(14)
        };
        _entranceAnimationTimer.Tick += OnEntranceAnimationTick;

        InitializeComponent();
        BuildNumpadKeysUI();
    }

    public void InitializeAndShow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureBorderlessWindow();
        ConfigureMicaBackdrop();
        SubclassWindow();
        ApplyTheme(_settings.Theme);
        ApplyOpacity(_settings.WindowOpacity);
        RegisterGlobalHotkey();

        _touchpadService.Initialize(_hwnd);
        _touchpadService.TouchpadAspectRatioChanged += aspect => DispatcherQueue.TryEnqueue(() => ResizeWindowToAspectRatio(aspect));
        _touchpadService.ActivateSwipeDetected += () => DispatcherQueue.TryEnqueue(ShowNumpadWindow);
        _touchpadService.DeactivateSwipeDetected += () => DispatcherQueue.TryEnqueue(HideNumpadWindow);
        _touchpadService.TouchPointPreview += (rx, ry, lx, ly, inside) => DispatcherQueue.TryEnqueue(() => OnTouchPointPreview(rx, ry, lx, ly, inside));
        _touchpadService.TouchPreviewCancelled += () => DispatcherQueue.TryEnqueue(HideTouchIndicator);
        _touchpadService.NumpadTapDetected += (rx, ry, lx, ly) => DispatcherQueue.TryEnqueue(() => OnNumpadTap(rx, ry, lx, ly));
        _touchpadService.EdgeExitToMouseModeDetected += (rx, ry) => DispatcherQueue.TryEnqueue(() => ExitToMouseMode(rx, ry));
        _touchpadService.OutsideDoubleTapDetected += (rx, ry) => DispatcherQueue.TryEnqueue(() =>
        {
            ExitToMouseMode(rx, ry);
            HideNumpadWindow();
        });
        _touchpadService.EscapeKeyPressed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_isNumpadMode)
            {
                ExitToMouseMode();
                HideNumpadWindow();
            }
        });
        _touchpadService.TransformUpdated += () => DispatcherQueue.TryEnqueue(ApplyNumpadBoardTransform);
        _touchpadService.MultiTouchPreview += (p1x, p1y, p2x, p2y) => DispatcherQueue.TryEnqueue(() => OnMultiTouchPreview(p1x, p1y, p2x, p2y));
        _touchpadService.MultiTouchEnded += () => DispatcherQueue.TryEnqueue(OnMultiTouchEnded);

        _trayService.Initialize(_hwnd, $"TouchNumPad ({_settings.GetHotkeyDisplayText()})");
        _trayService.ToggleRequested += () => DispatcherQueue.TryEnqueue(ToggleNumpadWindow);
        _trayService.SettingsRequested += () => DispatcherQueue.TryEnqueue(OpenSettingsWindow);
        _trayService.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);

        PositionWindowInitial();
        ApplyNumpadBoardTransform();
        ShowNumpadWindow();
    }

    private void ConfigureBorderlessWindow()
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // Remove WS_CAPTION, WS_SYSMENU and standard buttons
        long style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_SYSMENU);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, (nint)style);

        // Extended styles: Layered, TopMost, ToolWindow, and NoActivate
        long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, (nint)exStyle);

        // Windows 11 rounded corners
        int cornerPref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPref,
            sizeof(int));

        ResizeWindowToAspectRatio(_touchpadService.TouchpadAspectRatio);
    }

    private void ResizeWindowToAspectRatio(double aspect)
    {
        if (_appWindow is null)
            return;

        double scale = GetDpiScale();
        int widthPx = (int)Math.Round(BaseWindowWidthDip * scale);
        double validAspect = aspect > 0.5 ? aspect : (16.0 / 10.0);
        int heightPx = (int)Math.Round((BaseWindowWidthDip / validAspect) * scale);

        var currentPos = _appWindow.Position;
        _appWindow.MoveAndResize(new RectInt32(currentPos.X, currentPos.Y, widthPx, heightPx));
        ApplyNumpadBoardTransform();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyNumpadBoardTransform();
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            WindowScaleTransform.CenterX = e.NewSize.Width / 2.0;
            WindowScaleTransform.CenterY = e.NewSize.Height / 2.0;
        }
    }

    /// <summary>
    /// Applies real-time 2D photo-like Scale, Rotation, and Pan to the NumpadBoard.
    /// All buttons, labels, and indicators rotate and scale together.
    /// </summary>
    private void ApplyNumpadBoardTransform()
    {
        double width = NumpadBoard.ActualWidth;
        double height = NumpadBoard.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            width = RootGrid.ActualWidth;
            height = RootGrid.ActualHeight;
            if (width <= 0 || height <= 0)
                return;
        }

        double centerX = width / 2.0;
        double centerY = height / 2.0;

        BoardScaleTransform.CenterX = centerX;
        BoardScaleTransform.CenterY = centerY;
        BoardScaleTransform.ScaleX = _settings.NumpadScale;
        BoardScaleTransform.ScaleY = _settings.NumpadScale;

        BoardRotateTransform.CenterX = centerX;
        BoardRotateTransform.CenterY = centerY;
        BoardRotateTransform.Angle = _settings.NumpadAngle;

        BoardTranslateTransform.X = (_settings.NumpadCenterX - 0.5) * width;
        BoardTranslateTransform.Y = (_settings.NumpadCenterY - 0.5) * height;
    }

    private void ConfigureMicaBackdrop()
    {
        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = _settings.Theme switch
            {
                AppThemePreference.Light => SystemBackdropTheme.Light,
                AppThemePreference.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default
            }
        };

        var target = this.As<ICompositionSupportsSystemBackdrop>();

        if (MicaController.IsSupported())
        {
            _micaController = new MicaController();
            _micaController.AddSystemBackdropTarget(target);
            _micaController.SetSystemBackdropConfiguration(_backdropConfig);
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            _acrylicController = new DesktopAcrylicController();
            _acrylicController.AddSystemBackdropTarget(target);
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        }
    }

    private double GetDpiScale()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private void PositionWindowInitial()
    {
        if (_appWindow is null)
            return;

        double scale = GetDpiScale();
        int widthPx = (int)Math.Round(BaseWindowWidthDip * scale);
        double validAspect = _touchpadService.TouchpadAspectRatio > 0.5 ? _touchpadService.TouchpadAspectRatio : (16.0 / 10.0);
        int heightPx = (int)Math.Round((BaseWindowWidthDip / validAspect) * scale);

        if (_settings.WindowX.HasValue && _settings.WindowY.HasValue)
        {
            _appWindow.MoveAndResize(new RectInt32(_settings.WindowX.Value, _settings.WindowY.Value, widthPx, heightPx));
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(cursor, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

        if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
        {
            int x = mi.rcWork.Right - widthPx - (int)(24 * scale);
            int y = mi.rcWork.Bottom - heightPx - (int)(24 * scale);
            _appWindow.MoveAndResize(new RectInt32(x, y, widthPx, heightPx));
        }
    }

    private void SubclassWindow()
    {
        _subclassDelegate = CustomSubclassProc;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, 1, 0);
    }

    private IntPtr CustomSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        switch (msg)
        {
            case NativeMethods.WM_INPUT:
                _touchpadService.ProcessWmInput(lParam);
                break;

            case NativeMethods.WM_HOTKEY:
                if (wParam.ToInt32() == HotkeyId && (_settingsWindow is null || !_settingsWindow.IsRecordingHotkey))
                {
                    ToggleNumpadWindow();
                    return IntPtr.Zero;
                }
                break;

            case NativeMethods.WM_TRAYICON:
                _trayService.HandleTrayMessage(lParam, _isNumpadVisible && _isNumpadMode);
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEACTIVATE:
                return new IntPtr(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_CLOSE:
                if (!_isExiting)
                {
                    HideNumpadWindow();
                    return IntPtr.Zero;
                }
                break;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void BuildNumpadKeysUI()
    {
        KeysGrid.Children.Clear();
        _keyBorders.Clear();

        foreach (var key in NumpadKeyMap.Keys)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Tag = key
            };

            Grid.SetRow(card, key.Row);
            Grid.SetColumn(card, key.Column);
            Grid.SetRowSpan(card, key.RowSpan);
            Grid.SetColumnSpan(card, key.ColumnSpan);

            var contentGrid = new Grid();

            if (key.Id == "Backspace")
            {
                var icon = new FontIcon
                {
                    Glyph = "\uE94F",
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                contentGrid.Children.Add(icon);
            }
            else if (key.Id == "Enter")
            {
                var stack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 2
                };
                stack.Children.Add(new TextBlock
                {
                    Text = "Enter",
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                stack.Children.Add(new TextBlock
                {
                    Text = "↵",
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                contentGrid.Children.Add(stack);
            }
            else
            {
                var label = new TextBlock
                {
                    Text = key.Label,
                    FontSize = key.IsOperatorKey ? 20 : 21,
                    FontWeight = key.IsOperatorKey
                        ? Microsoft.UI.Text.FontWeights.Normal
                        : Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                contentGrid.Children.Add(label);

                // Physical bump indicator line for Home Key "5"
                if (key.IsHomeKey)
                {
                    var homeBar = new Border
                    {
                        Width = 14,
                        Height = 2.5,
                        CornerRadius = new CornerRadius(1.25),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 7),
                        Opacity = 0.55,
                        Background = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    };
                    contentGrid.Children.Add(homeBar);
                }
            }

            card.Child = contentGrid;
            card.PointerPressed += KeyCard_PointerPressed;
            card.PointerReleased += KeyCard_PointerReleased;
            card.PointerExited += KeyCard_PointerExited;

            KeysGrid.Children.Add(card);
            _keyBorders[key.Id] = card;
        }

        UpdateAllKeyVisualStates();
    }

    private void UpdateAllKeyVisualStates()
    {
        foreach (var (id, border) in _keyBorders)
        {
            if (border.Tag is NumpadKeyDefinition key)
            {
                ApplyKeyVisualState(border, key, isHighlighted: id == _highlightedKeyId);
            }
        }
    }

    private void ApplyKeyVisualState(Border border, NumpadKeyDefinition key, bool isHighlighted)
    {
        var resources = Application.Current.Resources;

        if (key.IsAccentKey)
        {
            border.Background = (Brush)resources[isHighlighted ? "AccentFillColorSecondaryBrush" : "AccentFillColorDefaultBrush"];
            border.BorderBrush = (Brush)resources["ControlStrokeColorOnAccentDefaultBrush"];
            SetKeyForeground(border, (Brush)resources["TextOnAccentFillColorPrimaryBrush"]);
            return;
        }

        if (isHighlighted)
        {
            border.Background = (Brush)resources["SubtleFillColorSecondaryBrush"];
            border.BorderBrush = (Brush)resources["AccentFillColorDefaultBrush"];
            SetKeyForeground(border, (Brush)resources["TextFillColorPrimaryBrush"]);
            return;
        }

        border.Background = (Brush)resources[key.IsOperatorKey ? "ControlFillColorSecondaryBrush" : "ControlFillColorDefaultBrush"];
        border.BorderBrush = (Brush)resources["ControlStrokeColorDefaultBrush"];
        SetKeyForeground(border, (Brush)resources["TextFillColorPrimaryBrush"]);
    }

    private static void SetKeyForeground(Border border, Brush foreground)
    {
        if (border.Child is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is TextBlock tb)
                    tb.Foreground = foreground;
                else if (child is FontIcon fi)
                    fi.Foreground = foreground;
                else if (child is StackPanel sp)
                {
                    foreach (var item in sp.Children)
                    {
                        if (item is TextBlock stb)
                            stb.Foreground = foreground;
                    }
                }
            }
        }
    }

    private void SetHighlightedKey(string? keyId)
    {
        if (_highlightedKeyId == keyId)
            return;

        string? previous = _highlightedKeyId;
        _highlightedKeyId = keyId;

        if (previous is not null && _keyBorders.TryGetValue(previous, out var prevBorder) && prevBorder.Tag is NumpadKeyDefinition prevKey)
        {
            ApplyKeyVisualState(prevBorder, prevKey, false);
        }

        if (keyId is not null && _keyBorders.TryGetValue(keyId, out var nextBorder) && nextBorder.Tag is NumpadKeyDefinition nextKey)
        {
            ApplyKeyVisualState(nextBorder, nextKey, true);
        }
    }

    private NumpadKeyDefinition? _pressedKeyCard;

    // Direct mouse / touch screen interaction on the Numpad window
    private void KeyCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isNumpadMode)
        {
            EnterNumpadMode();
            e.Handled = true;
            return;
        }

        if (sender is Border border && border.Tag is NumpadKeyDefinition key)
        {
            _pressedKeyCard = key;
            var pt = e.GetCurrentPoint(TouchOverlayCanvas).Position;
            ShowTouchIndicatorAtCanvasPoint(pt.X, pt.Y);
            SetHighlightedKey(key.Id);
            _feedbackService.KeyEntered(key);
            _indicatorHideTimer.Stop();
            e.Handled = true;
        }
    }

    private void KeyCard_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isNumpadMode)
            return;

        if (sender is Border border && border.Tag is NumpadKeyDefinition key && _pressedKeyCard == key)
        {
            _pressedKeyCard = null;
            _feedbackService.KeyCommitted();
            KeyboardInjector.SendKey(key);

            _indicatorHideTimer.Stop();
            _indicatorHideTimer.Start();
            e.Handled = true;
        }
        else
        {
            _pressedKeyCard = null;
            _indicatorHideTimer.Stop();
            _indicatorHideTimer.Start();
        }
    }

    private void KeyCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pressedKeyCard = null;
        if (!_indicatorHideTimer.IsEnabled)
        {
            SetHighlightedKey(null);
        }
    }

    // Window surface pointer events (for dragging window while in mouse mode)
    private void WindowSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isNumpadMode && _appWindow is not null)
        {
            var props = e.GetCurrentPoint(RootGrid).Properties;
            if (props.IsLeftButtonPressed)
            {
                _isDraggingWindow = true;
                _didMoveWindowDuringDrag = false;
                RootGrid.CapturePointer(e.Pointer);
                NativeMethods.GetCursorPos(out _dragStartCursorPos);
                _dragStartWindowPos = _appWindow.Position;
                e.Handled = true;
            }
        }
        else if (!_isNumpadMode)
        {
            EnterNumpadMode();
            e.Handled = true;
        }
    }

    private void WindowSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingWindow || _appWindow is null)
            return;

        NativeMethods.GetCursorPos(out var currentCursor);
        int dx = currentCursor.X - _dragStartCursorPos.X;
        int dy = currentCursor.Y - _dragStartCursorPos.Y;

        if (Math.Abs(dx) >= 3 || Math.Abs(dy) >= 3)
        {
            _didMoveWindowDuringDrag = true;
        }

        int newX = _dragStartWindowPos.X + dx;
        int newY = _dragStartWindowPos.Y + dy;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            newX,
            newY,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        e.Handled = true;
    }

    private void WindowSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowSurfaceDrag(e.Pointer, isCanceled: false);
    }

    private void WindowSurface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndWindowSurfaceDrag(e.Pointer, isCanceled: true);
    }

    private void EndWindowSurfaceDrag(Pointer pointer, bool isCanceled)
    {
        if (!_isDraggingWindow)
            return;

        bool moved = _didMoveWindowDuringDrag;
        _isDraggingWindow = false;
        _didMoveWindowDuringDrag = false;
        RootGrid.ReleasePointerCapture(pointer);

        if (moved && _appWindow is not null)
        {
            var pos = _appWindow.Position;
            _settings.WindowX = pos.X;
            _settings.WindowY = pos.Y;
            _settings.Save();
        }
        else if (!isCanceled && !_isNumpadMode)
        {
            EnterNumpadMode();
        }
    }

    private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var pt = e.GetPosition(KeysGrid);
        double width = KeysGrid.ActualWidth;
        double height = KeysGrid.ActualHeight;

        // If double-tapped on the background outside the Numpad key grid
        bool isInsideKeys = pt.X >= 0.0 && pt.X <= width && pt.Y >= 0.0 && pt.Y <= height;
        if (!isInsideKeys)
        {
            ExitToMouseMode();
            HideNumpadWindow();
            e.Handled = true;
        }
    }

    // Touchpad touch events
    private void OnTouchPointPreview(double rawX, double rawY, double localX, double localY, bool isInside)
    {
        if (!_isNumpadVisible || !_isNumpadMode || (_settingsWindow is not null && _settingsWindow.IsRecordingHotkey))
            return;

        _indicatorHideTimer.Stop();

        double width = TouchOverlayCanvas.ActualWidth;
        double height = TouchOverlayCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            width = RootGrid.ActualWidth;
            height = RootGrid.ActualHeight;
            if (width <= 0 || height <= 0)
                return;
        }

        double canvasX = Math.Clamp(rawX, 0.0, 1.0) * width;
        double canvasY = Math.Clamp(rawY, 0.0, 1.0) * height;

        ShowTouchIndicatorAtCanvasPoint(canvasX, canvasY);

        if (isInside)
        {
            var hitKey = NumpadKeyMap.HitTestNormalized(localX, localY);
            SetHighlightedKey(hitKey?.Id);
        }
        else
        {
            SetHighlightedKey(null);
        }
    }

    private void OnNumpadTap(double rawX, double rawY, double localX, double localY)
    {
        if (!_isNumpadVisible || !_isNumpadMode || (_settingsWindow is not null && _settingsWindow.IsRecordingHotkey))
            return;

        double width = TouchOverlayCanvas.ActualWidth;
        double height = TouchOverlayCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            width = RootGrid.ActualWidth;
            height = RootGrid.ActualHeight;
        }

        if (width > 0 && height > 0)
        {
            double canvasX = Math.Clamp(rawX, 0.0, 1.0) * width;
            double canvasY = Math.Clamp(rawY, 0.0, 1.0) * height;
            ShowTouchIndicatorAtCanvasPoint(canvasX, canvasY);
        }

        var hitKey = NumpadKeyMap.HitTestNormalized(localX, localY);
        if (hitKey is not null)
        {
            SetHighlightedKey(hitKey.Id);
            KeyboardInjector.SendKey(hitKey);
        }

        _indicatorHideTimer.Stop();
        _indicatorHideTimer.Start();
    }

    private void ShowTouchIndicatorAtCanvasPoint(double canvasX, double canvasY)
    {
        double radius = TouchIndicatorCircle.Width / 2.0;
        Canvas.SetLeft(TouchIndicatorCircle, canvasX - radius);
        Canvas.SetTop(TouchIndicatorCircle, canvasY - radius);
        TouchIndicatorCircle.Visibility = Visibility.Visible;
    }

    private void HideTouchIndicator()
    {
        TouchIndicatorCircle.Visibility = Visibility.Collapsed;
        SetHighlightedKey(null);
    }

    private void OnMultiTouchPreview(double p1X, double p1Y, double p2X, double p2Y)
    {
        _multiTouchHideTimer.Stop();

        double width = MultiTouchCanvas.ActualWidth;
        double height = MultiTouchCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            width = RootGrid.ActualWidth;
            height = RootGrid.ActualHeight;
            if (width <= 0 || height <= 0)
                return;
        }

        MultiTouchCanvas.Opacity = 1.0;

        double radius = Finger1Indicator.Width / 2.0;
        double x1 = Math.Clamp(p1X, 0.0, 1.0) * width;
        double y1 = Math.Clamp(p1Y, 0.0, 1.0) * height;
        double x2 = Math.Clamp(p2X, 0.0, 1.0) * width;
        double y2 = Math.Clamp(p2Y, 0.0, 1.0) * height;

        Canvas.SetLeft(Finger1Indicator, x1 - radius);
        Canvas.SetTop(Finger1Indicator, y1 - radius);
        Canvas.SetLeft(Finger2Indicator, x2 - radius);
        Canvas.SetTop(Finger2Indicator, y2 - radius);
    }

    private void OnMultiTouchEnded()
    {
        _multiTouchHideTimer.Stop();
        _multiTouchHideTimer.Start();
    }

    private void EnterNumpadMode()
    {
        if (!_isNumpadVisible || (_settingsWindow is not null && _settingsWindow.IsRecordingHotkey))
            return;

        _isNumpadMode = true;
        _touchpadService.IsNumpadActive = true;
        CursorVisibilityService.HideCursor();
    }

    private void ExitToMouseMode(double? edgeRawX = null, double? edgeRawY = null)
    {
        _isNumpadMode = false;
        _touchpadService.IsNumpadActive = false;
        HideTouchIndicator();

        if (edgeRawX.HasValue && edgeRawY.HasValue && _appWindow is not null)
        {
            var winPos = _appWindow.Position;
            var winSize = _appWindow.Size;

            int cursorX = winPos.X + (int)Math.Round(Math.Clamp(edgeRawX.Value, 0.0, 1.0) * winSize.Width);
            int cursorY = winPos.Y + (int)Math.Round(Math.Clamp(edgeRawY.Value, 0.0, 1.0) * winSize.Height);
            NativeMethods.SetCursorPos(cursorX, cursorY);
        }

        CursorVisibilityService.ShowCursor();
    }

    public void ShowNumpadWindow()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        _entranceAnimationTimer.Stop();
        TryPositionNearActiveInput();
        _isNumpadVisible = true;

        // Reset to initial animation state (hidden, slightly scaled down, translated down)
        SetWindowAlphaDirect(0);
        RootGrid.Opacity = 0.0;

        double width = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : BaseWindowWidthDip;
        double height = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : (BaseWindowWidthDip / 1.6);
        WindowScaleTransform.CenterX = width / 2.0;
        WindowScaleTransform.CenterY = height / 2.0;
        WindowScaleTransform.ScaleX = 0.93;
        WindowScaleTransform.ScaleY = 0.93;
        WindowTranslateTransform.Y = 10.0;

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        _entranceAnimStartTick = Environment.TickCount64;
        _entranceAnimationTimer.Start();

        if (_settingsWindow is null || !_settingsWindow.IsRecordingHotkey)
        {
            EnterNumpadMode();
        }
    }

    private void OnEntranceAnimationTick(object? sender, object e)
    {
        long elapsed = Environment.TickCount64 - _entranceAnimStartTick;
        double progress = Math.Clamp((double)elapsed / EntranceAnimationDurationMs, 0.0, 1.0);

        // Quartic Ease-Out curve for fluid, snappy deceleration
        double ease = 1.0 - Math.Pow(1.0 - progress, 4);

        double targetOpacity = _settings.WindowOpacity;
        byte maxAlpha = (byte)Math.Clamp((int)Math.Round(targetOpacity * 255), 64, 255);
        byte currentAlpha = (byte)Math.Clamp((int)Math.Round(maxAlpha * ease), 0, 255);

        SetWindowAlphaDirect(currentAlpha);

        RootGrid.Opacity = ease;
        WindowScaleTransform.ScaleX = 0.93 + 0.07 * ease;
        WindowScaleTransform.ScaleY = 0.93 + 0.07 * ease;
        WindowTranslateTransform.Y = 10.0 * (1.0 - ease);

        if (progress >= 1.0)
        {
            _entranceAnimationTimer.Stop();
            WindowScaleTransform.ScaleX = 1.0;
            WindowScaleTransform.ScaleY = 1.0;
            WindowTranslateTransform.Y = 0.0;
            RootGrid.Opacity = 1.0;
            ApplyOpacity(_settings.WindowOpacity);
        }
    }

    private void SetWindowAlphaDirect(byte alpha)
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
        }
    }

    private void TryPositionNearActiveInput()
    {
        if (_appWindow is null)
            return;

        if (!CaretPositionService.TryGetActiveInputRect(_hwnd, out var inputRect, out bool isExactCaret))
            return;

        double scale = GetDpiScale();
        int widthPx = (int)Math.Round(BaseWindowWidthDip * scale);
        double validAspect = _touchpadService.TouchpadAspectRatio > 0.5 ? _touchpadService.TouchpadAspectRatio : (16.0 / 10.0);
        int heightPx = (int)Math.Round((BaseWindowWidthDip / validAspect) * scale);
        int gap = (int)Math.Round(12 * scale);
        int margin = (int)Math.Round(12 * scale);

        var anchorPt = new NativeMethods.POINT
        {
            X = inputRect.Left,
            Y = inputRect.Top
        };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(anchorPt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            return;

        int x;
        int y;

        if (isExactCaret)
        {
            x = inputRect.Right + gap;
            y = inputRect.Bottom + gap;

            if (y + heightPx > mi.rcWork.Bottom - margin)
            {
                int yAbove = inputRect.Top - heightPx - gap;
                if (yAbove >= mi.rcWork.Top + margin)
                {
                    y = yAbove;
                }
                else
                {
                    y = Math.Clamp(inputRect.Top, mi.rcWork.Top + margin, mi.rcWork.Bottom - heightPx - margin);
                }
            }

            bool overlapsCaretVertically = y < inputRect.Bottom + 4 && (y + heightPx) > inputRect.Top - 4;
            if (overlapsCaretVertically && x + widthPx > mi.rcWork.Right - margin)
            {
                x = inputRect.Left - widthPx - gap;
            }
        }
        else
        {
            x = inputRect.Left;
            y = inputRect.Bottom + gap;

            if (y + heightPx > mi.rcWork.Bottom - margin)
            {
                int yAbove = inputRect.Top - heightPx - gap;
                if (yAbove >= mi.rcWork.Top + margin)
                {
                    y = yAbove;
                }
                else if (inputRect.Right + gap + widthPx <= mi.rcWork.Right - margin)
                {
                    x = inputRect.Right + gap;
                    y = inputRect.Top;
                }
                else
                {
                    x = inputRect.Left - widthPx - gap;
                    y = inputRect.Top;
                }
            }
        }

        x = Math.Clamp(x, mi.rcWork.Left + margin, Math.Max(mi.rcWork.Left + margin, mi.rcWork.Right - widthPx - margin));
        y = Math.Clamp(y, mi.rcWork.Top + margin, Math.Max(mi.rcWork.Top + margin, mi.rcWork.Bottom - heightPx - margin));

        _appWindow.MoveAndResize(new RectInt32(x, y, widthPx, heightPx));
    }

    public void HideNumpadWindow()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        _entranceAnimationTimer.Stop();
        _isNumpadVisible = false;
        _isNumpadMode = false;
        _touchpadService.IsNumpadActive = false;
        HideTouchIndicator();
        CursorVisibilityService.ShowCursor();

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    public void ToggleNumpadWindow()
    {
        if (_isNumpadVisible && _isNumpadMode)
            HideNumpadWindow();
        else
            ShowNumpadWindow();
    }

    // Independent Settings Window Management
    private void OpenSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.BringToFront();
            return;
        }

        ExitToMouseMode();

        _settingsWindow = new SettingsWindow(
            _settings,
            _feedbackService,
            onOpacityChanged: ApplyOpacity,
            onThemeChanged: ApplyTheme,
            onHotkeyChanged: () =>
            {
                bool ok = RegisterGlobalHotkey();
                if (ok)
                {
                    _trayService.UpdateTooltip($"TouchNumPad ({_settings.GetHotkeyDisplayText()})");
                }
                return ok;
            },
            onSuspendHotkey: () =>
            {
                NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
                ExitToMouseMode();
            },
            onResetTransform: () =>
            {
                _settings.ResetTransform();
                ApplyNumpadBoardTransform();
            });

        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_isNumpadVisible && !_isNumpadMode)
            {
                EnterNumpadMode();
            }
        };

        _settingsWindow.BringToFront();
    }

    private void ApplyOpacity(double opacity)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 64, 255);
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);

        if (_micaController is not null)
        {
            _micaController.TintOpacity = (float)Math.Clamp(0.55 * opacity, 0.15, 0.75);
        }
    }

    private void ApplyTheme(AppThemePreference theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (_backdropConfig is not null)
        {
            _backdropConfig.Theme = theme switch
            {
                AppThemePreference.Light => SystemBackdropTheme.Light,
                AppThemePreference.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default
            };
        }

        if (_hwnd != IntPtr.Zero)
        {
            bool isDark = theme switch
            {
                AppThemePreference.Dark => true,
                AppThemePreference.Light => false,
                _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
            };
            int darkAttr = isDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref darkAttr,
                sizeof(int));
        }

        UpdateAllKeyVisualStates();
    }

    private bool RegisterGlobalHotkey()
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        return NativeMethods.RegisterHotKey(
            _hwnd,
            HotkeyId,
            _settings.HotkeyModifiers | NativeMethods.MOD_NOREPEAT,
            _settings.HotkeyVirtualKey);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        CursorVisibilityService.ShowCursor();
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        _touchpadService.Dispose();
        _feedbackService.Dispose();
        _trayService.Dispose();
        _micaController?.Dispose();
        _acrylicController?.Dispose();
        _settingsWindow?.Close();
        Close();
        Application.Current.Exit();
    }
}
