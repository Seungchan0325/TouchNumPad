using System.Runtime.InteropServices;
using TouchNumPad.Interop;
using TouchNumPad.Models;

namespace TouchNumPad.Services;

public readonly record struct TouchpadContact(
    uint ContactId,
    double NormalizedX,
    double NormalizedY,
    bool TipSwitch,
    bool Confidence);

public sealed class TouchpadInputService : IDisposable
{
    private const ushort HID_USAGE_PAGE_BUTTON = 0x09;
    private const ushort HID_USAGE_BUTTON_1 = 0x01;

    // Top edge swipe thresholds (normalized 0.0 .. 1.0)
    private const double TopEdgeStartMaxY = 0.22;
    private const double TopEdgeTrackMaxY = 0.30;
    private const double LeftEdgeStartMaxX = 0.30;
    private const double RightEdgeStartMinX = 0.70;
    private const double SwipeMinSpanX = 0.45;

    // Edge-exit & watchdog thresholds
    private const double EdgeExitMinDragDistance = 0.25;
    private const double EdgeBoundaryMargin = 0.035;
    private const int LiftWatchdogTimeoutMs = 70;

    // Outside double tap thresholds
    private const long OutsideDoubleTapMaxIntervalMs = 380;
    private const double OutsideDoubleTapMaxDist = 0.12;

    private readonly AppSettings _settings;
    private readonly FeedbackService _feedbackService;
    private readonly Dictionary<IntPtr, CachedDeviceInfo> _deviceCache = new();
    private readonly List<TouchpadContact> _pendingContacts = new();
    private readonly Timer _liftWatchdogTimer;
    private int _expectedContactsInFrame;

    // Aspect ratio detected from hardware
    public double TouchpadAspectRatio { get; private set; } = 16.0 / 10.0;
    public event Action<double>? TouchpadAspectRatioChanged;

    // Touch session state
    private volatile bool _inTouchSession;
    private volatile bool _swipeTriggeredThisSession;
    private volatile bool _edgeExitedThisSession;
    private volatile bool _physicalButtonWasDown;
    private bool _startedAtTopLeft;
    private bool _startedAtTopRight;
    private double _startX;
    private double _startY;
    private double _lastX;
    private double _lastY;
    private double _maxDistance;
    private long _startTimeTick;

    // Two-finger transform gesture state (Anchor-based + 1€ Filter + Pivot around fingers)
    private bool _isTwoFingerGestureActive;
    private int _twoFingerStabilizeFrames;
    private double _anchorDist;
    private double _anchorAngleRad;
    private double _anchorMidX;
    private double _anchorMidY;
    private double _anchorScale;
    private double _anchorAngleDeg;
    private double _anchorNumpadCx;
    private double _anchorNumpadCy;

    private readonly OneEuroFilter _filterScale = new(minCutoff: 1.0, beta: 0.08);
    private readonly OneEuroFilter _filterAngle = new(minCutoff: 1.2, beta: 0.08);
    private readonly OneEuroFilter _filterCenterX = new(minCutoff: 1.0, beta: 0.08);
    private readonly OneEuroFilter _filterCenterY = new(minCutoff: 1.0, beta: 0.08);

    // Outside double-tap tracking
    private long _lastOutsideTapTick;
    private double _lastOutsideTapX;
    private double _lastOutsideTapY;

    // Key border feedback tracking
    private string? _currentEnteredKeyId;

    // Low-level mouse and keyboard hooks to block mouse movement, clicks, and gesture zoom/scroll wheels
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private readonly NativeMethods.LowLevelMouseProc _mouseProcDelegate;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProcDelegate;
    private long _suppressClickUntilTick;
    private long _suppressWheelUntilTick;

    public volatile bool IsNumpadActive;
    public bool IsTouchpadDetected { get; private set; }

    public event Action? ActivateSwipeDetected;
    public event Action? DeactivateSwipeDetected;
    public event Action<double, double, double, double, bool>? TouchPointPreview; // rawX, rawY, localX, localY, isInside
    public event Action? TouchPreviewCancelled;
    public event Action<double, double, double, double>? NumpadTapDetected;       // rawX, rawY, localX, localY
    public event Action<double, double>? EdgeExitToMouseModeDetected; // rawX, rawY
    public event Action<double, double>? OutsideDoubleTapDetected;    // rawX, rawY
    public event Action? TransformUpdated;
    public event Action<double, double, double, double>? MultiTouchPreview; // p1RawX, p1RawY, p2RawX, p2RawY
    public event Action? MultiTouchEnded;
    public event Action? EscapeKeyPressed;

    public TouchpadInputService(AppSettings settings, FeedbackService feedbackService)
    {
        _settings = settings;
        _feedbackService = feedbackService;
        _mouseProcDelegate = MouseHookCallback;
        _keyboardProcDelegate = KeyboardHookCallback;
        _liftWatchdogTimer = new Timer(OnLiftWatchdogTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Initialize(IntPtr hwnd)
    {
        InstallMouseHook();
        InstallKeyboardHook();

        var rid = new NativeMethods.RAWINPUTDEVICE[]
        {
            new()
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_DIGITIZER,
                usUsage = NativeMethods.HID_USAGE_DIGITIZER_TOUCH_PAD,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        bool registered = NativeMethods.RegisterRawInputDevices(
            rid,
            (uint)rid.Length,
            (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());

        IsTouchpadDetected = registered;
        return registered;
    }

    private void InstallMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
            return;

        IntPtr hMod = NativeMethods.GetModuleHandle(null);
        _mouseHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseProcDelegate,
            hMod,
            0);
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
            return;

        IntPtr hMod = NativeMethods.GetModuleHandle(null);
        _keyboardHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _keyboardProcDelegate,
            hMod,
            0);
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                long now = Environment.TickCount64;
                var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                // Only when Numpad Mode is actively controlling the touchpad (has focus), pressing ESC exits numpad & closes window
                if (IsNumpadActive && kb.vkCode == 0x1B) // VK_ESCAPE
                {
                    int msg = wParam.ToInt32();
                    if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
                    {
                        EscapeKeyPressed?.Invoke();
                    }
                    return new IntPtr(1);
                }

                // While in two-finger gesture or right after it, suppress system synthetic Ctrl key (zoom modifier)
                if (IsNumpadActive && (_isTwoFingerGestureActive || now <= _suppressWheelUntilTick))
                {
                    if (kb.vkCode is 0x11 or 0xA2 or 0xA3) // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
                    {
                        return new IntPtr(1);
                    }
                }
            }
        }
        catch
        {
            // Defensive: Never allow exceptions to escape to OS hook dispatcher
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                uint msg = (uint)wParam;
                long now = Environment.TickCount64;

                // While in Numpad Mode, suppress ALL mouse movements, button clicks, and scroll/zoom wheels
                if (IsNumpadActive)
                {
                    if (msg is NativeMethods.WM_MOUSEMOVE
                        or NativeMethods.WM_LBUTTONDOWN
                        or NativeMethods.WM_LBUTTONUP
                        or NativeMethods.WM_LBUTTONDBLCLK
                        or NativeMethods.WM_RBUTTONDOWN
                        or NativeMethods.WM_RBUTTONUP
                        or NativeMethods.WM_RBUTTONDBLCLK
                        or NativeMethods.WM_MBUTTONDOWN
                        or NativeMethods.WM_MBUTTONUP
                        or NativeMethods.WM_MBUTTONDBLCLK
                        or NativeMethods.WM_MOUSEWHEEL
                        or NativeMethods.WM_MOUSEHWHEEL)
                    {
                        return new IntPtr(1);
                    }
                }
                else if (now <= _suppressClickUntilTick || now <= _suppressWheelUntilTick)
                {
                    if (msg is NativeMethods.WM_LBUTTONDOWN 
                        or NativeMethods.WM_LBUTTONUP
                        or NativeMethods.WM_MOUSEWHEEL
                        or NativeMethods.WM_MOUSEHWHEEL)
                    {
                        return new IntPtr(1);
                    }
                }
            }
        }
        catch
        {
            // Defensive: Never allow exceptions to escape to OS hook dispatcher
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    public void ProcessWmInput(IntPtr lParam)
    {
        uint dwSize = 0;
        uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();

        if (NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, IntPtr.Zero, ref dwSize, headerSize) != 0 || dwSize == 0)
            return;

        IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            if (NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, buffer, ref dwSize, headerSize) != dwSize)
                return;

            var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buffer);
            if (header.dwType != NativeMethods.RIM_TYPEHID || header.hDevice == IntPtr.Zero)
                return;

            var deviceInfo = GetOrCreateDeviceInfo(header.hDevice);
            if (deviceInfo is null)
                return;

            uint dwSizeHid = (uint)Marshal.ReadInt32(buffer, (int)headerSize);
            uint dwCount = (uint)Marshal.ReadInt32(buffer, (int)headerSize + 4);
            if (dwSizeHid == 0 || dwCount == 0)
                return;

            IntPtr rawDataPtr = IntPtr.Add(buffer, (int)headerSize + 8);
            for (uint i = 0; i < dwCount; i++)
            {
                IntPtr reportPtr = IntPtr.Add(rawDataPtr, (int)(i * dwSizeHid));
                ProcessHidReport(deviceInfo, reportPtr, dwSizeHid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private CachedDeviceInfo? GetOrCreateDeviceInfo(IntPtr hDevice)
    {
        if (_deviceCache.TryGetValue(hDevice, out var cached))
            return cached;

        uint preparsedSize = 0;
        if (NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize) != 0 || preparsedSize == 0)
            return null;

        IntPtr preparsedData = Marshal.AllocHGlobal((int)preparsedSize);
        bool success = false;
        try
        {
            if (NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_PREPARSEDDATA, preparsedData, ref preparsedSize) == unchecked((uint)-1))
                return null;

            if (NativeMethods.HidP_GetCaps(preparsedData, out var caps) != NativeMethods.HIDP_STATUS_SUCCESS)
                return null;

            ushort valueCapsLen = caps.NumberInputValueCaps;
            var valueCaps = new NativeMethods.HIDP_VALUE_CAPS[valueCapsLen];
            if (valueCapsLen > 0)
            {
                if (NativeMethods.HidP_GetValueCaps(NativeMethods.HidP_Input, valueCaps, ref valueCapsLen, preparsedData) != NativeMethods.HIDP_STATUS_SUCCESS)
                    return null;
            }

            ushort buttonCapsLen = caps.NumberInputButtonCaps;
            var buttonCaps = new NativeMethods.HIDP_BUTTON_CAPS[buttonCapsLen];
            if (buttonCapsLen > 0)
            {
                NativeMethods.HidP_GetButtonCaps(NativeMethods.HidP_Input, buttonCaps, ref buttonCapsLen, preparsedData);
            }

            var contactLinks = new List<ContactLinkInfo>();
            var linksByIndex = new Dictionary<ushort, ContactLinkInfo>();

            for (int i = 0; i < valueCapsLen; i++)
            {
                var vc = valueCaps[i];
                if (vc.LinkCollection == 0)
                    continue;

                if (!linksByIndex.TryGetValue(vc.LinkCollection, out var link))
                {
                    link = new ContactLinkInfo
                    {
                        LinkCollection = vc.LinkCollection,
                        ButtonLinkCollection = vc.LinkCollection
                    };
                    linksByIndex[vc.LinkCollection] = link;
                }

                if (vc.UsagePage == NativeMethods.HID_USAGE_PAGE_GENERIC)
                {
                    if (vc.UsageMin == NativeMethods.HID_USAGE_GENERIC_X)
                    {
                        link.HasX = true;
                        link.MinX = vc.LogicalMin;
                        link.MaxX = vc.LogicalMax > vc.LogicalMin ? vc.LogicalMax : 1;
                        link.PhysicalMinX = vc.PhysicalMin;
                        link.PhysicalMaxX = vc.PhysicalMax;
                    }
                    else if (vc.UsageMin == NativeMethods.HID_USAGE_GENERIC_Y)
                    {
                        link.HasY = true;
                        link.MinY = vc.LogicalMin;
                        link.MaxY = vc.LogicalMax > vc.LogicalMin ? vc.LogicalMax : 1;
                        link.PhysicalMinY = vc.PhysicalMin;
                        link.PhysicalMaxY = vc.PhysicalMax;
                    }
                }
            }

            foreach (var link in linksByIndex.Values.OrderBy(l => l.LinkCollection))
            {
                if (link.HasX && link.HasY)
                {
                    contactLinks.Add(link);
                }
            }

            if (contactLinks.Count == 0)
                return null;

            var tipSwitchLinks = new List<ushort>();
            for (int i = 0; i < buttonCapsLen; i++)
            {
                var bc = buttonCaps[i];
                if (bc.UsagePage == NativeMethods.HID_USAGE_PAGE_DIGITIZER &&
                    bc.UsageMin <= NativeMethods.HID_USAGE_DIGITIZER_TIP_SWITCH &&
                    (bc.IsRange == 0 ? bc.UsageMin : bc.UsageMax) >= NativeMethods.HID_USAGE_DIGITIZER_TIP_SWITCH)
                {
                    if (!tipSwitchLinks.Contains(bc.LinkCollection))
                        tipSwitchLinks.Add(bc.LinkCollection);
                }
            }

            if (tipSwitchLinks.Count == contactLinks.Count)
            {
                for (int i = 0; i < contactLinks.Count; i++)
                {
                    contactLinks[i].ButtonLinkCollection = tipSwitchLinks[i];
                }
            }

            // Calculate actual aspect ratio
            var primaryLink = contactLinks[0];
            double width = (primaryLink.PhysicalMaxX > primaryLink.PhysicalMinX)
                ? (primaryLink.PhysicalMaxX - primaryLink.PhysicalMinX)
                : (primaryLink.MaxX - primaryLink.MinX);

            double height = (primaryLink.PhysicalMaxY > primaryLink.PhysicalMinY)
                ? (primaryLink.PhysicalMaxY - primaryLink.PhysicalMinY)
                : (primaryLink.MaxY - primaryLink.MinY);

            double aspect = (width > 0 && height > 0) ? (width / height) : (16.0 / 10.0);
            TouchpadAspectRatio = Math.Clamp(aspect, 1.15, 2.4);
            TouchpadAspectRatioChanged?.Invoke(TouchpadAspectRatio);

            var info = new CachedDeviceInfo(preparsedData, contactLinks, TouchpadAspectRatio);
            _deviceCache[hDevice] = info;
            success = true;
            return info;
        }
        finally
        {
            if (!success)
            {
                Marshal.FreeHGlobal(preparsedData);
            }
        }
    }

    private void ProcessHidReport(CachedDeviceInfo device, IntPtr reportPtr, uint reportLength)
    {
        uint contactCount = 0;
        int status = NativeMethods.HidP_GetUsageValue(
            NativeMethods.HidP_Input,
            NativeMethods.HID_USAGE_PAGE_DIGITIZER,
            0,
            NativeMethods.HID_USAGE_DIGITIZER_CONTACT_COUNT,
            out contactCount,
            device.PreparsedData,
            reportPtr,
            reportLength);

        if (status != NativeMethods.HIDP_STATUS_SUCCESS)
            return;

        bool physicalButtonPressed = false;
        uint btnLen = 4;
        ushort[] btnUsages = new ushort[btnLen];
        if (NativeMethods.HidP_GetUsages(
                NativeMethods.HidP_Input,
                HID_USAGE_PAGE_BUTTON,
                0,
                btnUsages,
                ref btnLen,
                device.PreparsedData,
                reportPtr,
                reportLength) == NativeMethods.HIDP_STATUS_SUCCESS)
        {
            for (int b = 0; b < btnLen; b++)
            {
                if (btnUsages[b] == HID_USAGE_BUTTON_1)
                {
                    physicalButtonPressed = true;
                    break;
                }
            }
        }

        if (contactCount > 0)
        {
            _pendingContacts.Clear();
            _expectedContactsInFrame = (int)contactCount;
        }

        int linksToRead = device.ContactLinks.Count;

        for (int i = 0; i < linksToRead; i++)
        {
            var link = device.ContactLinks[i];

            if (NativeMethods.HidP_GetUsageValue(
                    NativeMethods.HidP_Input,
                    NativeMethods.HID_USAGE_PAGE_GENERIC,
                    link.LinkCollection,
                    NativeMethods.HID_USAGE_GENERIC_X,
                    out uint rawX,
                    device.PreparsedData,
                    reportPtr,
                    reportLength) != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                continue;
            }

            if (NativeMethods.HidP_GetUsageValue(
                    NativeMethods.HidP_Input,
                    NativeMethods.HID_USAGE_PAGE_GENERIC,
                    link.LinkCollection,
                    NativeMethods.HID_USAGE_GENERIC_Y,
                    out uint rawY,
                    device.PreparsedData,
                    reportPtr,
                    reportLength) != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                continue;
            }

            uint contactId = 0;
            NativeMethods.HidP_GetUsageValue(
                NativeMethods.HidP_Input,
                NativeMethods.HID_USAGE_PAGE_DIGITIZER,
                link.ButtonLinkCollection,
                NativeMethods.HID_USAGE_DIGITIZER_CONTACT_ID,
                out contactId,
                device.PreparsedData,
                reportPtr,
                reportLength);

            uint usageLength = 16;
            ushort[] usages = new ushort[usageLength];
            bool tipSwitch = false;
            bool confidence = true;

            if (NativeMethods.HidP_GetUsages(
                    NativeMethods.HidP_Input,
                    NativeMethods.HID_USAGE_PAGE_DIGITIZER,
                    link.ButtonLinkCollection,
                    usages,
                    ref usageLength,
                    device.PreparsedData,
                    reportPtr,
                    reportLength) == NativeMethods.HIDP_STATUS_SUCCESS)
            {
                for (int u = 0; u < usageLength; u++)
                {
                    if (usages[u] == NativeMethods.HID_USAGE_DIGITIZER_TIP_SWITCH)
                        tipSwitch = true;
                    else if (usages[u] == NativeMethods.HID_USAGE_DIGITIZER_CONFIDENCE)
                        confidence = true;
                }
            }

            double normX = Math.Clamp((double)((int)rawX - link.MinX) / (link.MaxX - link.MinX), 0.0, 1.0);
            double normY = Math.Clamp((double)((int)rawY - link.MinY) / (link.MaxY - link.MinY), 0.0, 1.0);

            _pendingContacts.Add(new TouchpadContact(contactId, normX, normY, tipSwitch, confidence));
        }

        if (_expectedContactsInFrame <= 1 || _pendingContacts.Count >= _expectedContactsInFrame || device.ContactLinks.Count > 1)
        {
            ProcessContactFrame(_pendingContacts, physicalButtonPressed);
            _pendingContacts.Clear();
            _expectedContactsInFrame = 0;
        }
    }

    private void OnLiftWatchdogTick(object? state)
    {
        if (_inTouchSession)
        {
            ProcessContactFrame([], physicalButtonPressed: false);
        }
    }

    /// <summary>
    /// Transforms normalized raw touchpad coordinate (0..1) to local numpad unit coordinate (0..1)
    /// accounting for user-configured Scale, Rotation Angle, and Center offset with aspect ratio preservation.
    /// </summary>
    public (double localX, double localY, bool isInside) TransformToLocal(double rawX, double rawY)
    {
        double cx = _settings.NumpadCenterX;
        double cy = _settings.NumpadCenterY;
        double scale = Math.Max(0.2, _settings.NumpadScale);
        double rad = -_settings.NumpadAngle * (Math.PI / 180.0); // Inverse rotation

        double ar = TouchpadAspectRatio > 0 ? TouchpadAspectRatio : 1.5;
        double dx = (rawX - cx) * ar;
        double dy = (rawY - cy);

        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        double rx = dx * cos - dy * sin;
        double ry = dx * sin + dy * cos;

        double localX = (rx / ar) / scale + 0.5;
        double localY = ry / scale + 0.5;

        bool isInside = localX >= 0.0 && localX <= 1.0 && localY >= 0.0 && localY <= 1.0;
        return (localX, localY, isInside);
    }

    private void ProcessContactFrame(List<TouchpadContact> contacts, bool physicalButtonPressed)
    {
        var activeContacts = new List<TouchpadContact>(contacts.Count);
        for (int i = 0; i < contacts.Count; i++)
        {
            if (contacts[i].TipSwitch && contacts[i].Confidence)
            {
                activeContacts.Add(contacts[i]);
            }
        }

        int activeCount = activeContacts.Count;

        if (activeCount > 0)
        {
            _liftWatchdogTimer.Change(LiftWatchdogTimeoutMs, Timeout.Infinite);
        }
        else
        {
            _liftWatchdogTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        long now = Environment.TickCount64;

        // -------------------------------------------------------------
        // Case A: 2 Fingers -> Adaptive Transform Gesture (Pinch, Rotate, Drag)
        // -------------------------------------------------------------
        if (activeCount >= 2 && IsNumpadActive)
        {
            var p1 = activeContacts[0];
            var p2 = activeContacts[1];

            double midX = (p1.NormalizedX + p2.NormalizedX) / 2.0;
            double midY = (p1.NormalizedY + p2.NormalizedY) / 2.0;

            double ar = TouchpadAspectRatio > 0 ? TouchpadAspectRatio : 1.5;
            double dx = (p2.NormalizedX - p1.NormalizedX) * ar;
            double dy = p2.NormalizedY - p1.NormalizedY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double angleRad = Math.Atan2(dy, dx);

            // Phase 1: Contact stabilization (wait 2 frames to avoid initial tap spikes)
            if (!_isTwoFingerGestureActive)
            {
                _isTwoFingerGestureActive = true;
                _inTouchSession = false;
                _twoFingerStabilizeFrames = 0;
                _anchorDist = dist;
                _anchorAngleRad = angleRad;
                _anchorMidX = midX;
                _anchorMidY = midY;
                _anchorScale = _settings.NumpadScale;
                _anchorAngleDeg = _settings.NumpadAngle;
                _anchorNumpadCx = _settings.NumpadCenterX;
                _anchorNumpadCy = _settings.NumpadCenterY;

                _filterScale.Reset();
                _filterAngle.Reset();
                _filterCenterX.Reset();
                _filterCenterY.Reset();

                TouchPreviewCancelled?.Invoke();
                MultiTouchPreview?.Invoke(p1.NormalizedX, p1.NormalizedY, p2.NormalizedX, p2.NormalizedY);
                return;
            }

            if (_twoFingerStabilizeFrames < 2)
            {
                _twoFingerStabilizeFrames++;
                _anchorDist = dist;
                _anchorAngleRad = angleRad;
                _anchorMidX = midX;
                _anchorMidY = midY;
                MultiTouchPreview?.Invoke(p1.NormalizedX, p1.NormalizedY, p2.NormalizedX, p2.NormalizedY);
                return;
            }

            // Phase 2: Stable gesture transformation
            if (_anchorDist > 0.01 && dist > 0.01)
            {
                // 1. Scale Calculation with Deadband Thresholding
                double rawScaleRatio = dist / _anchorDist;
                // Deadband: If within +/-4%, don't scale (prevents accidental zoom during pan)
                double effectiveScaleRatio;
                if (Math.Abs(rawScaleRatio - 1.0) < 0.04)
                {
                    effectiveScaleRatio = 1.0;
                }
                else
                {
                    effectiveScaleRatio = rawScaleRatio > 1.0 
                        ? (rawScaleRatio - 0.04) / 0.96 
                        : (rawScaleRatio + 0.04) / 1.04;
                }
                double targetScale = Math.Clamp(_anchorScale * effectiveScaleRatio, 0.40, 1.0);
                double smoothedScale = _filterScale.Filter(targetScale, now);

                // 2. Rotation Angle with Deadband & Magnetic Soft Snap to 0 degrees
                double angleDeltaRad = angleRad - _anchorAngleRad;
                while (angleDeltaRad > Math.PI) angleDeltaRad -= 2.0 * Math.PI;
                while (angleDeltaRad < -Math.PI) angleDeltaRad -= 2.0 * Math.PI;
                double angleDeltaDeg = angleDeltaRad * (180.0 / Math.PI);

                // Deadband: If within +/-2.5 degrees of anchor, don't rotate (prevents wobble during pinch)
                double effectiveAngleDeltaDeg = Math.Abs(angleDeltaDeg) < 2.5 
                    ? 0.0 
                    : (angleDeltaDeg > 0 ? angleDeltaDeg - 2.5 : angleDeltaDeg + 2.5);

                double targetAngle = Math.Clamp(_anchorAngleDeg + effectiveAngleDeltaDeg, -45.0, 45.0);

                // Magnetic snap to 0.0 degrees
                if (Math.Abs(targetAngle) <= 2.2)
                {
                    targetAngle = 0.0;
                }

                double smoothedAngle = _filterAngle.Filter(targetAngle, now);

                // 3. Under-the-finger Pivot Tracking:
                // Keep the exact point under the fingers stationary as the numpad rotates and scales!
                double dAngleRad = (smoothedAngle - _anchorAngleDeg) * (Math.PI / 180.0);
                double scaleFactor = smoothedScale / Math.Max(0.1, _anchorScale);

                // Vector from anchor fingers midpoint to anchor numpad center (aspect-ratio corrected)
                double v0x = (_anchorNumpadCx - _anchorMidX) * ar;
                double v0y = (_anchorNumpadCy - _anchorMidY);

                // Rotate and scale the vector around the fingers midpoint
                double cos = Math.Cos(dAngleRad);
                double sin = Math.Sin(dAngleRad);
                double rx = (v0x * cos - v0y * sin) * scaleFactor;
                double ry = (v0x * sin + v0y * cos) * scaleFactor;

                // New numpad center = current fingers midpoint + rotated/scaled vector
                double targetCx = midX + (rx / ar);
                double targetCy = midY + ry;

                targetCx = Math.Clamp(targetCx, 0.15, 0.85);
                targetCy = Math.Clamp(targetCy, 0.15, 0.85);

                double smoothedCx = _filterCenterX.Filter(targetCx, now);
                double smoothedCy = _filterCenterY.Filter(targetCy, now);

                // Apply smoothed values to settings
                _settings.NumpadScale = smoothedScale;
                _settings.NumpadAngle = smoothedAngle;
                _settings.NumpadCenterX = smoothedCx;
                _settings.NumpadCenterY = smoothedCy;

                TransformUpdated?.Invoke();
            }

            MultiTouchPreview?.Invoke(p1.NormalizedX, p1.NormalizedY, p2.NormalizedX, p2.NormalizedY);
            return;
        }

        // Lifted two-finger gesture -> Freeze immediately and save!
        if (_isTwoFingerGestureActive && activeCount < 2)
        {
            _isTwoFingerGestureActive = false;
            _twoFingerStabilizeFrames = 0;
            _suppressWheelUntilTick = Environment.TickCount64 + 350; // Suppress lingering inertia scroll
            MultiTouchEnded?.Invoke();
            _settings.Save();
        }

        // -------------------------------------------------------------
        // Case B: 1 Finger Active -> Normal Numpad Touch / Tap / Edge Exit
        // -------------------------------------------------------------
        if (activeCount == 1)
        {
            var primaryActive = activeContacts[0];
            double rawX = primaryActive.NormalizedX;
            double rawY = primaryActive.NormalizedY;

            if (!_inTouchSession)
            {
                _inTouchSession = true;
                _swipeTriggeredThisSession = false;
                _edgeExitedThisSession = false;
                _physicalButtonWasDown = physicalButtonPressed;
                _startX = rawX;
                _startY = rawY;
                _lastX = rawX;
                _lastY = rawY;
                _maxDistance = 0.0;
                _startTimeTick = now;
                _currentEnteredKeyId = null;

                _startedAtTopLeft = rawX <= LeftEdgeStartMaxX && rawY <= TopEdgeStartMaxY;
                _startedAtTopRight = rawX >= RightEdgeStartMinX && rawY <= TopEdgeStartMaxY;

                if (IsNumpadActive)
                {
                    var (u, v, isInside) = TransformToLocal(rawX, rawY);
                    if (isInside)
                    {
                        var key = NumpadKeyMap.HitTestNormalized(u, v);
                        if (key is not null)
                        {
                            _currentEnteredKeyId = key.Id;
                            _feedbackService.KeyEntered(key);
                        }
                    }
                    TouchPointPreview?.Invoke(rawX, rawY, u, v, isInside);
                }
            }
            else
            {
                _lastX = rawX;
                _lastY = rawY;

                double dx = rawX - _startX;
                double dy = rawY - _startY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > _maxDistance)
                    _maxDistance = dist;

                // 1. Check top-edge open/close swipes
                if (!_swipeTriggeredThisSession && !_edgeExitedThisSession)
                {
                    if (_startedAtTopLeft && rawY <= TopEdgeTrackMaxY && rawX >= RightEdgeStartMinX && (rawX - _startX) >= SwipeMinSpanX)
                    {
                        _swipeTriggeredThisSession = true;
                        TouchPreviewCancelled?.Invoke();
                        ActivateSwipeDetected?.Invoke();
                        return;
                    }

                    if (_startedAtTopRight && rawY <= TopEdgeTrackMaxY && rawX <= LeftEdgeStartMaxX && (_startX - rawX) >= SwipeMinSpanX)
                    {
                        _swipeTriggeredThisSession = true;
                        TouchPreviewCancelled?.Invoke();
                        DeactivateSwipeDetected?.Invoke();
                        return;
                    }
                }

                if (!IsNumpadActive || _swipeTriggeredThisSession || _edgeExitedThisSession)
                {
                    return;
                }

                // 2. Large drag reaching outer edge of touchpad -> Edge Exit to Mouse mode
                bool isAtOuterEdge = rawX <= EdgeBoundaryMargin
                    || rawX >= (1.0 - EdgeBoundaryMargin)
                    || rawY <= EdgeBoundaryMargin
                    || rawY >= (1.0 - EdgeBoundaryMargin);

                bool isTrackingTopRightCloseSwipe = _startedAtTopRight && rawY <= TopEdgeTrackMaxY && rawX < _startX;

                if (_maxDistance >= EdgeExitMinDragDistance && isAtOuterEdge && !isTrackingTopRightCloseSwipe)
                {
                    _edgeExitedThisSession = true;
                    IsNumpadActive = false;
                    TouchPreviewCancelled?.Invoke();
                    EdgeExitToMouseModeDetected?.Invoke(rawX, rawY);
                    return;
                }

                // 3. Physical button click while aiming finger
                var (localX, localY, isInside) = TransformToLocal(rawX, rawY);

                if (physicalButtonPressed && !_physicalButtonWasDown)
                {
                    _physicalButtonWasDown = true;
                    _suppressClickUntilTick = now + 200;

                    if (!isInside)
                    {
                        // Outside physical click: check double click to mouse mode
                        CheckOutsideDoubleTap(rawX, rawY, now);
                    }
                }
                else if (!physicalButtonPressed && _physicalButtonWasDown)
                {
                    _physicalButtonWasDown = false;
                }

                // 4. Key boundary feedback during slide
                if (isInside)
                {
                    var hitKey = NumpadKeyMap.HitTestNormalized(localX, localY);
                    string? newKeyId = hitKey?.Id;
                    if (newKeyId != _currentEnteredKeyId && hitKey is not null)
                    {
                        _currentEnteredKeyId = newKeyId;
                        _feedbackService.KeyEntered(hitKey);
                    }
                }
                else
                {
                    _currentEnteredKeyId = null;
                }

                TouchPointPreview?.Invoke(rawX, rawY, localX, localY, isInside);
            }

            return;
        }

        // -------------------------------------------------------------
        // Case C: All fingers lifted (activeCount == 0) -> Commit key on lift-off!
        // -------------------------------------------------------------
        if (_inTouchSession)
        {
            if (IsNumpadActive
                && !_swipeTriggeredThisSession
                && !_edgeExitedThisSession)
            {
                var (localX, localY, isInside) = TransformToLocal(_lastX, _lastY);
                if (isInside)
                {
                    var hitKey = NumpadKeyMap.HitTestNormalized(localX, localY);
                    if (hitKey is not null)
                    {
                        _suppressClickUntilTick = now + 150;
                        _feedbackService.KeyCommitted();
                        NumpadTapDetected?.Invoke(_lastX, _lastY, localX, localY);
                    }
                    else
                    {
                        TouchPreviewCancelled?.Invoke();
                    }
                }
                else
                {
                    // User lifted outside the numpad area:
                    // Single tap is IGNORED (palm rejection), double tap exits to mouse mode!
                    CheckOutsideDoubleTap(_lastX, _lastY, now);
                }
            }
            else
            {
                TouchPreviewCancelled?.Invoke();
            }

            _inTouchSession = false;
            _swipeTriggeredThisSession = false;
            _edgeExitedThisSession = false;
            _physicalButtonWasDown = false;
            _currentEnteredKeyId = null;
        }
    }

    private void CheckOutsideDoubleTap(double rawX, double rawY, long now)
    {
        long interval = now - _lastOutsideTapTick;
        double dx = rawX - _lastOutsideTapX;
        double dy = rawY - _lastOutsideTapY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (interval <= OutsideDoubleTapMaxIntervalMs && dist <= OutsideDoubleTapMaxDist)
        {
            // Double-tap outside confirmed -> Turn off numpad window and switch to mouse mode!
            _lastOutsideTapTick = 0;
            IsNumpadActive = false;
            TouchPreviewCancelled?.Invoke();
            OutsideDoubleTapDetected?.Invoke(rawX, rawY);
        }
        else
        {
            // First tap outside -> Ignore and record timestamp
            _lastOutsideTapTick = now;
            _lastOutsideTapX = rawX;
            _lastOutsideTapY = rawY;
            TouchPreviewCancelled?.Invoke();
        }
    }

    public void Dispose()
    {
        _liftWatchdogTimer.Dispose();
        UninstallKeyboardHook();

        if (_mouseHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }

        foreach (var device in _deviceCache.Values)
        {
            device.Dispose();
        }
        _deviceCache.Clear();
    }

    private sealed class ContactLinkInfo
    {
        public ushort LinkCollection { get; init; }
        public ushort ButtonLinkCollection { get; set; }
        public bool HasX { get; set; }
        public bool HasY { get; set; }
        public int MinX { get; set; }
        public int MaxX { get; set; } = 1;
        public int MinY { get; set; }
        public int MaxY { get; set; } = 1;
        public int PhysicalMinX { get; set; }
        public int PhysicalMaxX { get; set; }
        public int PhysicalMinY { get; set; }
        public int PhysicalMaxY { get; set; }
    }

    private sealed class CachedDeviceInfo(IntPtr preparsedData, List<ContactLinkInfo> contactLinks, double aspectRatio) : IDisposable
    {
        public IntPtr PreparsedData { get; } = preparsedData;
        public List<ContactLinkInfo> ContactLinks { get; } = contactLinks;
        public double AspectRatio { get; } = aspectRatio;

        public void Dispose()
        {
            if (PreparsedData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PreparsedData);
            }
        }
    }
}
