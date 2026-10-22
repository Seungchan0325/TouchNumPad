using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TouchNumPad.Interop;
using TouchNumPad.Models;
using TouchNumPad.Services;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace TouchNumPad;

public sealed partial class SettingsWindow : Window
{
    private const int WindowWidthDip = 680;
    private const int WindowHeightDip = 600;
    private const int TestHotkeyId = 9002;

    private enum KeyVisualKind
    {
        Accent,
        Neutral,
        Error
    }

    private readonly AppSettings _settings;
    private readonly FeedbackService _feedbackService;
    private readonly Action<double> _onOpacityChanged;
    private readonly Action<AppThemePreference> _onThemeChanged;
    private readonly Func<bool> _onHotkeyChanged;
    private readonly Action _onSuspendHotkey;
    private readonly Action _onResetTransform;

    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private bool _isInitializing = true;
    private bool _isRecordingHotkey;
    private bool _isWindowActive = true;

    // Low-level keyboard hook for PowerToys-style shortcut capture
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardHookDelegate;

    // Live key tracking while the dialog is open
    private uint _heldModifiers;
    private uint _candidateModifiers;
    private uint _candidateVirtualKey;
    private bool _hasValidCandidate;

    public bool IsRecordingHotkey => _isRecordingHotkey;

    public SettingsWindow(
        AppSettings settings,
        FeedbackService feedbackService,
        Action<double> onOpacityChanged,
        Action<AppThemePreference> onThemeChanged,
        Func<bool> onHotkeyChanged,
        Action onSuspendHotkey,
        Action onResetTransform)
    {
        _settings = settings;
        _feedbackService = feedbackService;
        _onOpacityChanged = onOpacityChanged;
        _onThemeChanged = onThemeChanged;
        _onHotkeyChanged = onHotkeyChanged;
        _onSuspendHotkey = onSuspendHotkey;
        _onResetTransform = onResetTransform;
        _keyboardHookDelegate = KeyboardHookCallback;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindows11Shell();
        SyncControlsFromSettings();
        ApplyTheme(_settings.Theme);

        _isInitializing = false;
        Activated += SettingsWindow_Activated;
        Closed += SettingsWindow_Closed;
    }

    public void BringToFront()
    {
        Activate();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(_hwnd);
        }
    }

    private void ConfigureWindows11Shell()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (_appWindow is null)
            return;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
        }

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        int widthPx = (int)Math.Round(WindowWidthDip * scale);
        int heightPx = (int)Math.Round(WindowHeightDip * scale);

        NativeMethods.GetCursorPos(out var cursor);
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(cursor, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

        if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
        {
            int workW = mi.rcWork.Right - mi.rcWork.Left;
            int workH = mi.rcWork.Bottom - mi.rcWork.Top;
            int x = mi.rcWork.Left + (workW - widthPx) / 2;
            int y = mi.rcWork.Top + (workH - heightPx) / 2;
            _appWindow.MoveAndResize(new RectInt32(x, y, widthPx, heightPx));
        }
        else
        {
            _appWindow.Resize(new SizeInt32(widthPx, heightPx));
        }
    }

    private void SyncControlsFromSettings()
    {
        int pct = (int)Math.Round(_settings.WindowOpacity * 100);
        OpacitySlider.Value = pct;
        OpacityValueText.Text = $"{pct}%";

        ThemeComboBox.SelectedIndex = _settings.Theme switch
        {
            AppThemePreference.Light => 1,
            AppThemePreference.Dark => 2,
            _ => 0
        };

        RenderKeycaps(
            KeycapsPanel,
            _settings.HotkeyModifiers,
            _settings.HotkeyVirtualKey,
            isLarge: false,
            KeyVisualKind.Accent);
        ToolTipService.SetToolTip(EditHotkeyButton, $"활성화 단축키 ({_settings.GetHotkeyDisplayText()})");

        // Sync Touchpad Feedback & Transform
        HapticsToggleSwitch.IsOn = _settings.EnableHaptics;
        if (!_feedbackService.IsHapticsSupported)
        {
            HapticStatusSubtext.Text = "정밀 햅틱 터치패드가 감지되지 않아 햅틱 기능이 비활성화되었습니다 (사운드 피드백 자동 사용)";
            HapticsToggleSwitch.IsEnabled = false;
        }

        SoundToggleSwitch.IsOn = _settings.EnableSound;
        int soundPct = (int)Math.Round(_settings.SoundVolume * 100);
        SoundVolumeSlider.Value = soundPct;
        SoundVolumeText.Text = $"{soundPct}%";

        var appVer = typeof(App).Assembly.GetName().Version;
        if (appVer is not null)
        {
            AppVersionText.Text = $"버전 {appVer.Major}.{appVer.Minor}.{appVer.Build}";
        }
    }

    private void RenderKeycaps(
        StackPanel container,
        uint modifiers,
        uint vk,
        bool isLarge,
        KeyVisualKind visualKind)
    {
        container.Children.Clear();

        var keys = new List<string>();
        if ((modifiers & NativeMethods.MOD_WIN) != 0) keys.Add("Win");
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) keys.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) keys.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) keys.Add("Shift");
        if (vk != 0) keys.Add(AppSettings.VirtualKeyToDisplayName(vk));

        bool isDark = IsEffectiveDarkTheme();

        (Brush background, Brush borderBrush, Brush foreground) = visualKind switch
        {
            KeyVisualKind.Accent => (
                GetResourceBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(255, 0, 120, 212)),
                GetResourceBrush("ControlStrokeColorOnAccentSecondaryBrush", Windows.UI.Color.FromArgb(64, 0, 0, 0)),
                GetResourceBrush("TextOnAccentFillColorPrimaryBrush", Colors.White)),
            KeyVisualKind.Error => (
                GetResourceBrush("SystemFillColorCriticalBackgroundBrush", isDark
                    ? Windows.UI.Color.FromArgb(255, 68, 39, 38)
                    : Windows.UI.Color.FromArgb(255, 253, 231, 233)),
                GetResourceBrush("SystemFillColorCriticalBrush", isDark
                    ? Windows.UI.Color.FromArgb(255, 255, 153, 164)
                    : Windows.UI.Color.FromArgb(255, 196, 43, 28)),
                GetResourceBrush("SystemFillColorCriticalBrush", isDark
                    ? Windows.UI.Color.FromArgb(255, 255, 153, 164)
                    : Windows.UI.Color.FromArgb(255, 196, 43, 28))),
            _ => (
                GetResourceBrush("ControlFillColorDefaultBrush", isDark
                    ? Windows.UI.Color.FromArgb(18, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(179, 255, 255, 255)),
                GetResourceBrush("ControlElevationBorderBrush", isDark
                    ? Windows.UI.Color.FromArgb(36, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(24, 0, 0, 0)),
                GetResourceBrush("TextFillColorPrimaryBrush", isDark ? Colors.White : Colors.Black))
        };

        foreach (string keyText in keys)
        {
            var pill = new Border
            {
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = isLarge ? new Thickness(1, 1, 1, 3) : new Thickness(1, 1, 1, 2),
                BackgroundSizing = BackgroundSizing.OuterBorderEdge,
                CornerRadius = isLarge ? new CornerRadius(8) : new CornerRadius(4),
                Padding = isLarge ? new Thickness(20, 14, 20, 14) : new Thickness(8, 6, 8, 6),
                MinWidth = isLarge ? 54 : 36,
                Height = isLarge ? 56 : 32,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (keyText == "Win")
            {
                // PowerToys KeyVisual renders the Windows key with the native Windows glyph (\uE782)
                pill.Child = new FontIcon
                {
                    Glyph = "\uE782",
                    FontSize = isLarge ? 18 : 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = foreground
                };
            }
            else
            {
                pill.Child = new TextBlock
                {
                    Text = keyText,
                    FontSize = isLarge ? 16 : 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = foreground
                };
            }

            container.Children.Add(pill);
        }
    }

    private bool IsEffectiveDarkTheme()
    {
        return _settings.Theme switch
        {
            AppThemePreference.Dark => true,
            AppThemePreference.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
        };
    }

    private static Brush GetResourceBrush(string key, Windows.UI.Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? val) && val is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallbackColor);
    }

    public void ApplyTheme(AppThemePreference theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        bool isDark = theme switch
        {
            AppThemePreference.Dark => true,
            AppThemePreference.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
        };

        if (_hwnd != IntPtr.Zero)
        {
            int darkAttr = isDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref darkAttr,
                sizeof(int));
        }

        if (_appWindow?.TitleBar is { } titleBar)
        {
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverBackgroundColor = isDark
                ? Windows.UI.Color.FromArgb(24, 255, 255, 255)
                : Windows.UI.Color.FromArgb(18, 0, 0, 0);
        }

        RenderKeycaps(
            KeycapsPanel,
            _settings.HotkeyModifiers,
            _settings.HotkeyVirtualKey,
            isLarge: false,
            KeyVisualKind.Accent);
    }

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        int pct = (int)Math.Round(e.NewValue);
        OpacityValueText.Text = $"{pct}%";

        double opacity = Math.Clamp(pct / 100.0, 0.25, 1.0);
        _settings.WindowOpacity = opacity;
        _settings.Save();
        _onOpacityChanged(opacity);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        var pref = ThemeComboBox.SelectedIndex switch
        {
            1 => AppThemePreference.Light,
            2 => AppThemePreference.Dark,
            _ => AppThemePreference.System
        };

        _settings.Theme = pref;
        _settings.Save();
        ApplyTheme(pref);
        _onThemeChanged(pref);
    }

    private void HapticsToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _settings.EnableHaptics = HapticsToggleSwitch.IsOn;
        _settings.Save();
    }

    private void SoundToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _settings.EnableSound = SoundToggleSwitch.IsOn;
        _settings.Save();

        if (_settings.EnableSound)
        {
            _feedbackService.KeyCommitted();
        }
    }

    private void SoundVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        int pct = (int)Math.Round(e.NewValue);
        SoundVolumeText.Text = $"{pct}%";

        _settings.SoundVolume = pct / 100.0;
        _feedbackService.EnsureBuffersUpdated();
        _feedbackService.KeyCommitted();
        _settings.Save();
    }

    private void ResetTransformButton_Click(object sender, RoutedEventArgs e)
    {
        _onResetTransform?.Invoke();
    }

    // PowerToys-style Shortcut Recorder Dialog
    private async void EditHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
            return;

        _isRecordingHotkey = true;
        _onSuspendHotkey();

        _heldModifiers = 0;
        _candidateModifiers = _settings.HotkeyModifiers;
        _candidateVirtualKey = _settings.HotkeyVirtualKey;
        _hasValidCandidate = true;

        UpdateDialogState(
            _candidateModifiers,
            _candidateVirtualKey,
            KeyVisualKind.Accent,
            canSave: true,
            errorMessage: null,
            showAltGrWarning: IsAltGrCombination(_candidateModifiers, _candidateVirtualKey));

        HotkeyDialog.XamlRoot = RootGrid.XamlRoot;
        HotkeyDialog.RequestedTheme = RootGrid.RequestedTheme;

        await HotkeyDialog.ShowAsync();
    }

    private void HotkeyDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _heldModifiers = 0;
        InstallKeyboardHook();
    }

    private void DialogResetButton_Click(object sender, RoutedEventArgs e)
    {
        _heldModifiers = 0;
        _candidateModifiers = NativeMethods.MOD_SHIFT | NativeMethods.MOD_ALT;
        _candidateVirtualKey = 0x4E; // 'N'
        _hasValidCandidate = true;

        UpdateDialogState(
            _candidateModifiers,
            _candidateVirtualKey,
            KeyVisualKind.Accent,
            canSave: true,
            errorMessage: null,
            showAltGrWarning: false);
    }

    private void HotkeyDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_hasValidCandidate)
        {
            args.Cancel = true;
            return;
        }

        UninstallKeyboardHook();

        uint oldMod = _settings.HotkeyModifiers;
        uint oldVk = _settings.HotkeyVirtualKey;

        _settings.HotkeyModifiers = _candidateModifiers;
        _settings.HotkeyVirtualKey = _candidateVirtualKey;
        _isRecordingHotkey = false;

        if (_onHotkeyChanged())
        {
            _settings.Save();
            RenderKeycaps(
                KeycapsPanel,
                _settings.HotkeyModifiers,
                _settings.HotkeyVirtualKey,
                isLarge: false,
                KeyVisualKind.Accent);
            ToolTipService.SetToolTip(EditHotkeyButton, $"활성화 단축키 ({_settings.GetHotkeyDisplayText()})");
            HotkeyStatusText.Text = "넘패드 창을 열거나 닫는 전역 단축키입니다";
        }
        else
        {
            _settings.HotkeyModifiers = oldMod;
            _settings.HotkeyVirtualKey = oldVk;
            _onHotkeyChanged();
            RenderKeycaps(
                KeycapsPanel,
                _settings.HotkeyModifiers,
                _settings.HotkeyVirtualKey,
                isLarge: false,
                KeyVisualKind.Accent);
            HotkeyStatusText.Text = "이미 다른 앱에서 사용 중인 단축키여서 이전 단축키를 유지했습니다.";
        }
    }

    private void HotkeyDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        UninstallKeyboardHook();

        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            _onHotkeyChanged();
        }
    }

    private void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_isWindowActive)
        {
            _heldModifiers = 0;
        }
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        UninstallKeyboardHook();

        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            _onHotkeyChanged();
        }
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHookId != IntPtr.Zero)
            return;

        IntPtr hMod = NativeMethods.GetModuleHandle(null);
        _keyboardHookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _keyboardHookDelegate,
            hMod,
            0);
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isRecordingHotkey && _isWindowActive)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            bool isKeyUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                uint vkCode = hookStruct.vkCode;

                uint modBit = GetModifierBit(vkCode);
                if (modBit != 0)
                {
                    if (isKeyDown)
                    {
                        if ((_heldModifiers & modBit) == 0)
                        {
                            _heldModifiers |= modBit;
                            uint modsSnapshot = _heldModifiers;
                            DispatcherQueue.TryEnqueue(() => OnModifierKeyDown(modsSnapshot));
                        }
                    }
                    else
                    {
                        _heldModifiers &= ~modBit;
                    }

                    // Swallow modifier keys while recording so Win doesn't open Start Menu and Alt doesn't activate window menu
                    return new IntPtr(1);
                }

                if (isKeyDown)
                {
                    if (vkCode == (uint)VirtualKey.Escape)
                    {
                        DispatcherQueue.TryEnqueue(() => HotkeyDialog.Hide());
                        return new IntPtr(1);
                    }

                    uint modsSnapshot = _heldModifiers;
                    DispatcherQueue.TryEnqueue(() => OnActionKeyDown(modsSnapshot, vkCode));
                    return new IntPtr(1);
                }

                if (isKeyUp)
                {
                    return new IntPtr(1);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private static uint GetModifierBit(uint vkCode)
    {
        return (VirtualKey)vkCode switch
        {
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => NativeMethods.MOD_CONTROL,
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => NativeMethods.MOD_SHIFT,
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => NativeMethods.MOD_ALT,
            VirtualKey.LeftWindows or VirtualKey.RightWindows => NativeMethods.MOD_WIN,
            _ => 0
        };
    }

    private void OnModifierKeyDown(uint heldModifiers)
    {
        if (!_isRecordingHotkey)
            return;

        _hasValidCandidate = false;
        UpdateDialogState(
            heldModifiers,
            vk: 0,
            KeyVisualKind.Neutral,
            canSave: false,
            errorMessage: null,
            showAltGrWarning: false);
    }

    private void OnActionKeyDown(uint heldModifiers, uint vkCode)
    {
        if (!_isRecordingHotkey)
            return;

        bool isFunctionKey = vkCode >= (uint)VirtualKey.F1 && vkCode <= (uint)VirtualKey.F24;

        // Single key without modifiers (and not F1-F24) -> PowerToys Error state (red keycap + Error InfoBar)
        if (heldModifiers == 0 && !isFunctionKey)
        {
            _hasValidCandidate = false;
            UpdateDialogState(
                modifiers: 0,
                vk: vkCode,
                KeyVisualKind.Error,
                canSave: false,
                errorMessage: "유효하지 않은 단축키입니다. Ctrl, Alt, Shift 또는 Win 키와 다른 키를 조합하세요.",
                showAltGrWarning: false);
            return;
        }

        // Check if this hotkey conflicts with another running application or Windows system hotkey
        if (!CanRegisterHotkey(heldModifiers, vkCode))
        {
            _hasValidCandidate = false;
            UpdateDialogState(
                heldModifiers,
                vkCode,
                KeyVisualKind.Error,
                canSave: false,
                errorMessage: "이 단축키는 이미 Windows 시스템 또는 다른 앱에서 사용 중입니다.",
                showAltGrWarning: false);
            return;
        }

        _candidateModifiers = heldModifiers;
        _candidateVirtualKey = vkCode;
        _hasValidCandidate = true;

        UpdateDialogState(
            _candidateModifiers,
            _candidateVirtualKey,
            KeyVisualKind.Accent,
            canSave: true,
            errorMessage: null,
            showAltGrWarning: IsAltGrCombination(_candidateModifiers, _candidateVirtualKey));
    }

    private bool CanRegisterHotkey(uint modifiers, uint vk)
    {
        if (_hwnd == IntPtr.Zero)
            return true;

        bool ok = NativeMethods.RegisterHotKey(
            _hwnd,
            TestHotkeyId,
            modifiers | NativeMethods.MOD_NOREPEAT,
            vk);

        if (ok)
        {
            NativeMethods.UnregisterHotKey(_hwnd, TestHotkeyId);
        }

        return ok;
    }

    private static bool IsAltGrCombination(uint modifiers, uint vk)
    {
        return vk != 0
            && (modifiers & NativeMethods.MOD_CONTROL) != 0
            && (modifiers & NativeMethods.MOD_ALT) != 0
            && (modifiers & NativeMethods.MOD_WIN) == 0;
    }

    private void UpdateDialogState(
        uint modifiers,
        uint vk,
        KeyVisualKind visualKind,
        bool canSave,
        string? errorMessage,
        bool showAltGrWarning)
    {
        RenderKeycaps(DialogKeycapsPanel, modifiers, vk, isLarge: true, visualKind);
        HotkeyDialog.IsPrimaryButtonEnabled = canSave;

        if (!string.IsNullOrEmpty(errorMessage))
        {
            DialogErrorInfoBar.Message = errorMessage;
            DialogErrorInfoBar.IsOpen = true;
            DialogWarningInfoBar.IsOpen = false;
        }
        else
        {
            DialogErrorInfoBar.IsOpen = false;
            DialogWarningInfoBar.IsOpen = showAltGrWarning;
        }
    }
}
