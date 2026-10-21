using System.Runtime.InteropServices;
using TouchNumPad.Interop;

namespace TouchNumPad.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1001;
    private const uint CmdToggleNumpad = 2001;
    private const uint CmdOpenSettings = 2002;
    private const uint CmdExitApp = 2003;

    private IntPtr _hwnd;
    private bool _isAdded;

    public event Action? ToggleRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public void Initialize(IntPtr hwnd, string tooltipText)
    {
        _hwnd = hwnd;

        var data = CreateNotifyIconData(tooltipText);
        _isAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
    }

    public void UpdateTooltip(string tooltipText)
    {
        if (!_isAdded || _hwnd == IntPtr.Zero)
            return;

        var data = CreateNotifyIconData(tooltipText);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private NativeMethods.NOTIFYICONDATA CreateNotifyIconData(string tooltipText)
    {
        IntPtr hIcon = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));

        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = hIcon,
            szTip = tooltipText.Length > 120 ? tooltipText[..120] : tooltipText,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    public void HandleTrayMessage(IntPtr lParam, bool isWindowVisible)
    {
        uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);

        if (mouseMsg == NativeMethods.WM_LBUTTONUP || mouseMsg == NativeMethods.WM_LBUTTONDBLCLK)
        {
            ToggleRequested?.Invoke();
        }
        else if (mouseMsg == NativeMethods.WM_RBUTTONUP || mouseMsg == NativeMethods.WM_CONTEXTMENU)
        {
            ShowContextMenu(isWindowVisible);
        }
    }

    private void ShowContextMenu(bool isWindowVisible)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
            return;

        try
        {
            string toggleText = isWindowVisible ? "넘패드 숨기기 (Hide)" : "넘패드 표시 (Show)";
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, CmdToggleNumpad, toggleText);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, CmdOpenSettings, "설정 (Settings)...");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, CmdExitApp, "종료 (Exit)");

            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.SetForegroundWindow(_hwnd);

            int selected = NativeMethods.TrackPopupMenu(
                hMenu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_BOTTOMALIGN,
                pt.X,
                pt.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            switch ((uint)selected)
            {
                case CmdToggleNumpad:
                    ToggleRequested?.Invoke();
                    break;
                case CmdOpenSettings:
                    SettingsRequested?.Invoke();
                    break;
                case CmdExitApp:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_isAdded && _hwnd != IntPtr.Zero)
        {
            var data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = TrayIconId
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _isAdded = false;
        }
    }
}
