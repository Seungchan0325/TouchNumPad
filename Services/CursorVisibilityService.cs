using TouchNumPad.Interop;

namespace TouchNumPad.Services;

internal static class CursorVisibilityService
{
    private static readonly uint[] SystemCursorIds =
    [
        32512, // IDC_ARROW
        32513, // IDC_IBEAM
        32514, // IDC_WAIT
        32515, // IDC_CROSS
        32516, // IDC_UPARROW
        32642, // IDC_SIZENWSE
        32643, // IDC_SIZENESW
        32644, // IDC_SIZEWE
        32645, // IDC_SIZENS
        32646, // IDC_SIZEALL
        32648, // IDC_NO
        32649, // IDC_HAND
        32650, // IDC_APPSTARTING
        32651  // IDC_HELP
    ];

    private static readonly object SyncLock = new();
    private static bool _isHidden;

    static CursorVisibilityService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemCursors();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreSystemCursors();
    }

    public static bool IsHidden => _isHidden;

    public static void HideCursor()
    {
        lock (SyncLock)
        {
            if (_isHidden)
                return;

            // 32x32 monochrome cursor: AND mask = all 1s (transparent), XOR mask = all 0s
            byte[] andMask = new byte[128];
            byte[] xorMask = new byte[128];
            Array.Fill(andMask, (byte)0xFF);

            IntPtr hBlank = NativeMethods.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
            if (hBlank == IntPtr.Zero)
                return;

            try
            {
                foreach (uint id in SystemCursorIds)
                {
                    IntPtr hCopy = NativeMethods.CopyIcon(hBlank);
                    if (hCopy != IntPtr.Zero)
                    {
                        NativeMethods.SetSystemCursor(hCopy, id);
                    }
                }

                _isHidden = true;
            }
            finally
            {
                NativeMethods.DestroyCursor(hBlank);
            }
        }
    }

    public static void ShowCursor()
    {
        lock (SyncLock)
        {
            if (!_isHidden)
                return;

            RestoreSystemCursors();
            _isHidden = false;
        }
    }

    private static void RestoreSystemCursors()
    {
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
    }
}
