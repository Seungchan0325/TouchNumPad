using TouchNumPad.Interop;

namespace TouchNumPad.Services;

/// <summary>
/// Manages cursor presentation during Numpad mode safely.
/// Never mutates Windows OS global cursor bitmaps (SetSystemCursor) to avoid leaving
/// permanent invisible cursor corruption (e.g., missing I-beam in input fields) on crash.
/// </summary>
internal static class CursorVisibilityService
{
    private static readonly object SyncLock = new();
    private static bool _isHidden;
    private static NativeMethods.POINT? _savedPoint;

    static CursorVisibilityService()
    {
        // On module load, immediately heal any previously corrupted system cursors left by older builds
        RestoreSystemCursors();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemCursors();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreSystemCursors();
    }

    public static bool IsHidden => _isHidden;

    /// <summary>
    /// Parks the cursor at the screen edge during numpad mode so it doesn't obstruct active input fields.
    /// Does not alter global OS cursor assets.
    /// </summary>
    public static void HideCursor()
    {
        lock (SyncLock)
        {
            if (_isHidden)
                return;

            if (NativeMethods.GetCursorPos(out var pt))
            {
                _savedPoint = pt;
            }

            // Move cursor to bottom-right corner of virtual screen so it does not obstruct text / input carets
            int maxX = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            int maxY = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            if (maxX > 0 && maxY > 0)
            {
                NativeMethods.SetCursorPos(maxX - 1, maxY - 1);
            }

            _isHidden = true;
        }
    }

    /// <summary>
    /// Restores cursor state when exiting numpad mode.
    /// If restorePosition is true, moves cursor back to where it was before entering numpad mode.
    /// </summary>
    public static void ShowCursor(bool restorePosition = true)
    {
        lock (SyncLock)
        {
            if (!_isHidden)
                return;

            if (restorePosition && _savedPoint.HasValue)
            {
                NativeMethods.SetCursorPos(_savedPoint.Value.X, _savedPoint.Value.Y);
            }

            _savedPoint = null;
            _isHidden = false;
        }
    }

    /// <summary>
    /// Invokes Windows SystemParametersInfo(SPI_SETCURSORS) to reset all OS-level system cursors
    /// back to Windows default schemes.
    /// </summary>
    public static void RestoreSystemCursors()
    {
        try
        {
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }
        catch
        {
            // Ignore
        }
    }
}
