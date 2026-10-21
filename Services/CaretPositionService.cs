using System.Runtime.InteropServices;
using TouchNumPad.Interop;

namespace TouchNumPad.Services;

internal static unsafe class CaretPositionService
{
    private static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IID_IUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;
    private const int UIA_TextPattern2Id = 10024;

    private const int UIA_ComboBoxControlTypeId = 50003;
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_DocumentControlTypeId = 50030;

    private const int TextUnit_Character = 0;

    public static bool TryGetActiveInputRect(IntPtr ownHwnd, out NativeMethods.RECT targetRect, out bool isExactCaret)
    {
        targetRect = default;
        isExactCaret = false;

        IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero || fgHwnd == ownHwnd)
            return false;

        // 1. Fast path: Win32 GetGUIThreadInfo caret
        if (TryGetGuiThreadCaretRect(fgHwnd, out targetRect))
        {
            isExactCaret = true;
            return true;
        }

        // 2. Modern apps path (WinUI, WPF, Chrome, Edge, VS Code, Electron, Office):
        // Query UI Automation on a background MTA thread with a short timeout to avoid UI thread deadlocks.
        NativeMethods.RECT uiaRect = default;
        bool uiaExact = false;
        bool uiaFound = false;

        try
        {
            var task = Task.Run(() =>
            {
                if (TryGetUiaCaretOrInputRect(fgHwnd, out var rect, out bool exact))
                {
                    uiaRect = rect;
                    uiaExact = exact;
                    uiaFound = true;
                }
            });

            if (task.Wait(110) && uiaFound)
            {
                targetRect = uiaRect;
                isExactCaret = uiaExact;
                return true;
            }
        }
        catch
        {
            // Ignore any UIA timeout or COM error
        }

        return false;
    }

    private static bool TryGetGuiThreadCaretRect(IntPtr fgHwnd, out NativeMethods.RECT screenRect)
    {
        screenRect = default;

        uint threadId = NativeMethods.GetWindowThreadProcessId(fgHwnd, out _);
        if (threadId == 0)
            return false;

        var guiInfo = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(threadId, ref guiInfo))
            return false;

        if (guiInfo.hwndCaret == IntPtr.Zero)
            return false;

        bool isBlinking = (guiInfo.flags & NativeMethods.GUI_CARETBLINKING) != 0;
        int caretHeight = guiInfo.rcCaret.Bottom - guiInfo.rcCaret.Top;

        // Filter out inactive dummy carets at (0,0) with 0 height
        if (!isBlinking && caretHeight <= 0)
            return false;
        if (!isBlinking && guiInfo.rcCaret.Left == 0 && guiInfo.rcCaret.Top == 0)
            return false;

        var topLeft = new NativeMethods.POINT
        {
            X = guiInfo.rcCaret.Left,
            Y = guiInfo.rcCaret.Top
        };
        var bottomRight = new NativeMethods.POINT
        {
            X = guiInfo.rcCaret.Right,
            Y = guiInfo.rcCaret.Bottom
        };

        if (!NativeMethods.ClientToScreen(guiInfo.hwndCaret, ref topLeft) ||
            !NativeMethods.ClientToScreen(guiInfo.hwndCaret, ref bottomRight))
        {
            return false;
        }

        if (topLeft.X == 0 && topLeft.Y == 0 && bottomRight.X == 0 && bottomRight.Y == 0)
            return false;

        screenRect = new NativeMethods.RECT
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = Math.Max(topLeft.X + 2, bottomRight.X),
            Bottom = Math.Max(topLeft.Y + 16, bottomRight.Y)
        };

        return IsRectReasonableForWindow(fgHwnd, screenRect);
    }

    private static bool TryGetUiaCaretOrInputRect(
        IntPtr fgHwnd,
        out NativeMethods.RECT screenRect,
        out bool isExactCaret)
    {
        screenRect = default;
        isExactCaret = false;

        IntPtr pAutomation = IntPtr.Zero;
        IntPtr pFocused = IntPtr.Zero;

        try
        {
            int hr = NativeMethods.CoCreateInstance(
                in CLSID_CUIAutomation,
                IntPtr.Zero,
                NativeMethods.CLSCTX_INPROC_SERVER,
                in IID_IUIAutomation,
                out pAutomation);

            if (hr < 0 || pAutomation == IntPtr.Zero)
                return false;

            // IUIAutomation::GetFocusedElement is vtable slot 8
            var getFocusedElement = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)GetVtableMethod(pAutomation, 8);
            hr = getFocusedElement(pAutomation, out pFocused);
            if (hr < 0 || pFocused == IntPtr.Zero)
                return false;

            // Check if element is offscreen (slot 38: get_CurrentIsOffscreen)
            var getIsOffscreen = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVtableMethod(pFocused, 38);
            if (getIsOffscreen(pFocused, out int isOffscreen) >= 0 && isOffscreen != 0)
                return false;

            // 1. Try IUIAutomationTextPattern2 (UIA_TextPattern2Id = 10024) -> GetCaretRange (slot 10)
            if (TryGetPattern(pFocused, UIA_TextPattern2Id, out IntPtr pTextPattern2))
            {
                try
                {
                    var getCaretRange = (delegate* unmanaged[Stdcall]<IntPtr, out int, out IntPtr, int>)GetVtableMethod(pTextPattern2, 10);
                    if (getCaretRange(pTextPattern2, out int isActive, out IntPtr pCaretRange) >= 0 && pCaretRange != IntPtr.Zero)
                    {
                        try
                        {
                            if (TryGetRangeRect(pCaretRange, out screenRect) &&
                                IsRectReasonableForWindow(fgHwnd, screenRect))
                            {
                                isExactCaret = true;
                                return true;
                            }
                        }
                        finally
                        {
                            ReleaseComObject(pCaretRange);
                        }
                    }
                }
                finally
                {
                    ReleaseComObject(pTextPattern2);
                }
            }

            // 2. Try IUIAutomationTextPattern (UIA_TextPatternId = 10014) -> GetSelection (slot 5)
            bool hasTextPattern = false;
            if (TryGetPattern(pFocused, UIA_TextPatternId, out IntPtr pTextPattern))
            {
                hasTextPattern = true;
                try
                {
                    var getSelection = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)GetVtableMethod(pTextPattern, 5);
                    if (getSelection(pTextPattern, out IntPtr pRangeArray) >= 0 && pRangeArray != IntPtr.Zero)
                    {
                        try
                        {
                            // IUIAutomationTextRangeArray::get_Length (slot 3), GetElement (slot 4)
                            var getLength = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVtableMethod(pRangeArray, 3);
                            var getElement = (delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int>)GetVtableMethod(pRangeArray, 4);

                            if (getLength(pRangeArray, out int length) >= 0 && length > 0 &&
                                getElement(pRangeArray, 0, out IntPtr pRange) >= 0 && pRange != IntPtr.Zero)
                            {
                                try
                                {
                                    if (TryGetRangeRect(pRange, out screenRect) &&
                                        IsRectReasonableForWindow(fgHwnd, screenRect))
                                    {
                                        isExactCaret = true;
                                        return true;
                                    }
                                }
                                finally
                                {
                                    ReleaseComObject(pRange);
                                }
                            }
                        }
                        finally
                        {
                            ReleaseComObject(pRangeArray);
                        }
                    }
                }
                finally
                {
                    ReleaseComObject(pTextPattern);
                }
            }

            // 3. Fallback for empty/focused editable controls (e.g., empty browser <input>, empty TextBox, Excel cell):
            // Check control type (slot 21: get_CurrentControlType) and ValuePattern read-only status
            var getControlType = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVtableMethod(pFocused, 21);
            int controlType = 0;
            getControlType(pFocused, out controlType);

            bool isEditable = controlType == UIA_EditControlTypeId ||
                              (hasTextPattern && (controlType == UIA_DocumentControlTypeId || controlType == UIA_ComboBoxControlTypeId));

            if (!isEditable && TryGetPattern(pFocused, UIA_ValuePatternId, out IntPtr pValuePattern))
            {
                try
                {
                    // IUIAutomationValuePattern::get_CurrentIsReadOnly (slot 5)
                    var getIsReadOnly = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVtableMethod(pValuePattern, 5);
                    if (getIsReadOnly(pValuePattern, out int isReadOnly) >= 0 && isReadOnly == 0)
                    {
                        isEditable = true;
                    }
                }
                finally
                {
                    ReleaseComObject(pValuePattern);
                }
            }

            if (isEditable)
            {
                // IUIAutomationElement::get_CurrentBoundingRectangle (slot 43)
                var getBoundingRect = (delegate* unmanaged[Stdcall]<IntPtr, out NativeMethods.RECT, int>)GetVtableMethod(pFocused, 43);
                if (getBoundingRect(pFocused, out var elemRect) >= 0 &&
                    elemRect.Right > elemRect.Left &&
                    elemRect.Bottom > elemRect.Top &&
                    IsRectReasonableForWindow(fgHwnd, elemRect))
                {
                    int width = elemRect.Right - elemRect.Left;
                    int height = elemRect.Bottom - elemRect.Top;

                    // If the editable element is a large multi-line document/editor and had no caret rect,
                    // anchor near its top-left typing area rather than below the entire window.
                    if (width > 500 || height > 160)
                    {
                        screenRect = new NativeMethods.RECT
                        {
                            Left = elemRect.Left + 24,
                            Top = elemRect.Top + 24,
                            Right = elemRect.Left + 40,
                            Bottom = elemRect.Top + 48
                        };
                        isExactCaret = true;
                    }
                    else
                    {
                        screenRect = elemRect;
                        isExactCaret = false;
                    }

                    return true;
                }
            }
        }
        catch
        {
            // Ignore COM / marshalling errors
        }
        finally
        {
            ReleaseComObject(pFocused);
            ReleaseComObject(pAutomation);
        }

        return false;
    }

    private static bool TryGetPattern(IntPtr pElement, int patternId, out IntPtr pPattern)
    {
        pPattern = IntPtr.Zero;
        // IUIAutomationElement::GetCurrentPattern is slot 16
        var getCurrentPattern = (delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int>)GetVtableMethod(pElement, 16);
        int hr = getCurrentPattern(pElement, patternId, out pPattern);
        return hr >= 0 && pPattern != IntPtr.Zero;
    }

    private static bool TryGetRangeRect(IntPtr pTextRange, out NativeMethods.RECT rect)
    {
        if (TryExtractBoundingRectangles(pTextRange, out rect))
            return true;

        // If degenerate 0-length caret range returned an empty SAFEARRAY, expand to Character (unit 0) and retry
        // IUIAutomationTextRange::ExpandToEnclosingUnit is slot 6
        var expand = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)GetVtableMethod(pTextRange, 6);
        if (expand(pTextRange, TextUnit_Character) >= 0)
        {
            if (TryExtractBoundingRectangles(pTextRange, out rect))
                return true;
        }

        return false;
    }

    private static bool TryExtractBoundingRectangles(IntPtr pTextRange, out NativeMethods.RECT rect)
    {
        rect = default;

        // IUIAutomationTextRange::GetBoundingRectangles is slot 10
        var getBoundingRectangles = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)GetVtableMethod(pTextRange, 10);
        int hr = getBoundingRectangles(pTextRange, out IntPtr psa);
        if (hr < 0 || psa == IntPtr.Zero)
            return false;

        try
        {
            if (NativeMethods.SafeArrayGetLBound(psa, 1, out int lBound) < 0 ||
                NativeMethods.SafeArrayGetUBound(psa, 1, out int uBound) < 0)
            {
                return false;
            }

            int count = uBound - lBound + 1;
            if (count < 4)
                return false;

            if (NativeMethods.SafeArrayAccessData(psa, out IntPtr pData) < 0 || pData == IntPtr.Zero)
                return false;

            try
            {
                double* vals = (double*)pData;
                double left = vals[0];
                double top = vals[1];
                double width = vals[2];
                double height = vals[3];

                if (double.IsNaN(left) || double.IsNaN(top) || (left == 0 && top == 0 && width == 0 && height == 0))
                    return false;

                int l = (int)Math.Round(left);
                int t = (int)Math.Round(top);
                int r = l + Math.Max(2, (int)Math.Round(width));
                int b = t + Math.Max(16, (int)Math.Round(height));

                rect = new NativeMethods.RECT
                {
                    Left = l,
                    Top = t,
                    Right = r,
                    Bottom = b
                };
                return true;
            }
            finally
            {
                NativeMethods.SafeArrayUnaccessData(psa);
            }
        }
        finally
        {
            NativeMethods.SafeArrayDestroy(psa);
        }
    }

    private static bool IsRectReasonableForWindow(IntPtr fgHwnd, NativeMethods.RECT rect)
    {
        if (!NativeMethods.GetWindowRect(fgHwnd, out var winRect))
            return true;

        // Allow some tolerance for popups/child windows
        const int tolerance = 64;
        return rect.Right >= winRect.Left - tolerance &&
               rect.Left <= winRect.Right + tolerance &&
               rect.Bottom >= winRect.Top - tolerance &&
               rect.Top <= winRect.Bottom + tolerance;
    }

    private static IntPtr GetVtableMethod(IntPtr comObject, int slot)
    {
        IntPtr vtable = *(IntPtr*)comObject;
        return *((IntPtr*)vtable + slot);
    }

    private static void ReleaseComObject(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetVtableMethod(comObject, 2);
            release(comObject);
        }
    }
}
