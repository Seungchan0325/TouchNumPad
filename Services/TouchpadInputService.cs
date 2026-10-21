using System.Runtime.InteropServices;
using TouchNumPad.Interop;

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

    // Tap vs slide-aim vs edge-exit thresholds
    private const double TapMaxMovement = 0.085;
    private const long MaxTapDurationMs = 1500;
    private const double EdgeExitMinDragDistance = 0.22;
    private const double EdgeBoundaryMargin = 0.045;
    private const int LiftWatchdogTimeoutMs = 70;

    private readonly Dictionary<IntPtr, CachedDeviceInfo> _deviceCache = new();
    private readonly List<TouchpadContact> _pendingContacts = new();
    private readonly Timer _liftWatchdogTimer;
    private int _expectedContactsInFrame;

    // Touch session state
    private volatile bool _inTouchSession;
    private volatile bool _swipeTriggeredThisSession;
    private volatile bool _edgeExitedThisSession;
    private volatile bool _physicalClickSentThisSession;
    private volatile bool _physicalButtonWasDown;
    private bool _startedAtTopLeft;
    private bool _startedAtTopRight;
    private double _startX;
    private double _startY;
    private double _lastX;
    private double _lastY;
    private double _maxDistance;
    private long _startTimeTick;

    // Low-level mouse hook to block mouse movement & clicks while in Numpad mode
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private readonly NativeMethods.LowLevelMouseProc _mouseProcDelegate;
    private long _suppressClickUntilTick;

    public volatile bool IsNumpadActive;
    public bool IsTouchpadDetected { get; private set; }

    public event Action? ActivateSwipeDetected;
    public event Action? DeactivateSwipeDetected;
    public event Action<double, double>? TouchPointPreview;
    public event Action? TouchPreviewCancelled;
    public event Action<double, double>? NumpadTapDetected;
    public event Action<double, double>? EdgeExitToMouseModeDetected;

    public TouchpadInputService()
    {
        _mouseProcDelegate = MouseHookCallback;
        _liftWatchdogTimer = new Timer(OnLiftWatchdogTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Initialize(IntPtr hwnd)
    {
        InstallMouseHook();

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

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam;
            long now = Environment.TickCount64;

            // While in Numpad Mode, suppress all touchpad/mouse movement and button clicks
            if (IsNumpadActive)
            {
                if (msg is NativeMethods.WM_MOUSEMOVE
                    or NativeMethods.WM_LBUTTONDOWN
                    or NativeMethods.WM_LBUTTONUP
                    or NativeMethods.WM_LBUTTONDBLCLK
                    or NativeMethods.WM_RBUTTONDOWN
                    or NativeMethods.WM_RBUTTONUP)
                {
                    return new IntPtr(1);
                }
            }
            else if (now <= _suppressClickUntilTick)
            {
                if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP)
                {
                    return new IntPtr(1);
                }
            }
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
                    }
                    else if (vc.UsageMin == NativeMethods.HID_USAGE_GENERIC_Y)
                    {
                        link.HasY = true;
                        link.MinY = vc.LogicalMin;
                        link.MaxY = vc.LogicalMax > vc.LogicalMin ? vc.LogicalMax : 1;
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

            var info = new CachedDeviceInfo(preparsedData, contactLinks);
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

    private void ProcessContactFrame(List<TouchpadContact> contacts, bool physicalButtonPressed)
    {
        int activeCount = 0;
        TouchpadContact primaryActive = default;

        for (int i = 0; i < contacts.Count; i++)
        {
            if (contacts[i].TipSwitch && contacts[i].Confidence)
            {
                if (activeCount == 0)
                    primaryActive = contacts[i];
                activeCount++;
            }
        }

        if (activeCount > 0)
        {
            _liftWatchdogTimer.Change(LiftWatchdogTimeoutMs, Timeout.Infinite);
        }
        else
        {
            _liftWatchdogTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (activeCount >= 1)
        {
            double x = primaryActive.NormalizedX;
            double y = primaryActive.NormalizedY;
            long now = Environment.TickCount64;

            if (!_inTouchSession)
            {
                _inTouchSession = true;
                _swipeTriggeredThisSession = false;
                _edgeExitedThisSession = false;
                _physicalClickSentThisSession = false;
                _physicalButtonWasDown = physicalButtonPressed;
                _startX = x;
                _startY = y;
                _lastX = x;
                _lastY = y;
                _maxDistance = 0.0;
                _startTimeTick = now;

                _startedAtTopLeft = x <= LeftEdgeStartMaxX && y <= TopEdgeStartMaxY;
                _startedAtTopRight = x >= RightEdgeStartMinX && y <= TopEdgeStartMaxY;

                if (IsNumpadActive)
                {
                    TouchPointPreview?.Invoke(x, y);
                }
            }
            else
            {
                _lastX = x;
                _lastY = y;

                double dx = x - _startX;
                double dy = y - _startY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > _maxDistance)
                    _maxDistance = dist;

                // 1. Check top-edge open/close swipes
                if (!_swipeTriggeredThisSession && !_edgeExitedThisSession)
                {
                    if (_startedAtTopLeft && y <= TopEdgeTrackMaxY && x >= RightEdgeStartMinX && (x - _startX) >= SwipeMinSpanX)
                    {
                        _swipeTriggeredThisSession = true;
                        TouchPreviewCancelled?.Invoke();
                        ActivateSwipeDetected?.Invoke();
                        return;
                    }

                    if (_startedAtTopRight && y <= TopEdgeTrackMaxY && x <= LeftEdgeStartMaxX && (_startX - x) >= SwipeMinSpanX)
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

                // 2. Check if user dragged largely and reached the edge of the Numpad window -> switch to Mouse/Touchpad mode!
                bool isAtOuterEdge = x <= EdgeBoundaryMargin
                    || x >= (1.0 - EdgeBoundaryMargin)
                    || y <= EdgeBoundaryMargin
                    || y >= (1.0 - EdgeBoundaryMargin);

                // Don't trigger edge-exit while in the middle of a top-right-to-left close swipe track
                bool isTrackingTopRightCloseSwipe = _startedAtTopRight && y <= TopEdgeTrackMaxY && x < _startX;

                if (_maxDistance >= EdgeExitMinDragDistance && isAtOuterEdge && !isTrackingTopRightCloseSwipe)
                {
                    _edgeExitedThisSession = true;
                    IsNumpadActive = false;
                    TouchPreviewCancelled?.Invoke();
                    EdgeExitToMouseModeDetected?.Invoke(x, y);
                    return;
                }

                // 3. If physical touchpad button is clicked while resting/aiming finger in Numpad mode, send the key
                if (physicalButtonPressed && !_physicalButtonWasDown)
                {
                    _physicalButtonWasDown = true;
                    _physicalClickSentThisSession = true;
                    _suppressClickUntilTick = now + 200;
                    NumpadTapDetected?.Invoke(x, y);
                }
                else if (!physicalButtonPressed && _physicalButtonWasDown)
                {
                    _physicalButtonWasDown = false;
                }

                // 4. Continuously display the finger's resting position on the Numpad window
                TouchPointPreview?.Invoke(x, y);
            }

            return;
        }

        // All fingers lifted (activeCount == 0)
        if (_inTouchSession)
        {
            long now = Environment.TickCount64;
            long elapsedMs = now - _startTimeTick;

            if (IsNumpadActive
                && !_swipeTriggeredThisSession
                && !_edgeExitedThisSession
                && !_physicalClickSentThisSession
                && _maxDistance <= TapMaxMovement
                && elapsedMs <= MaxTapDurationMs)
            {
                _suppressClickUntilTick = now + 150;
                NumpadTapDetected?.Invoke(_lastX, _lastY);
            }
            else
            {
                TouchPreviewCancelled?.Invoke();
            }

            _inTouchSession = false;
            _swipeTriggeredThisSession = false;
            _edgeExitedThisSession = false;
            _physicalClickSentThisSession = false;
            _physicalButtonWasDown = false;
        }
    }

    public void Dispose()
    {
        _liftWatchdogTimer.Dispose();

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
    }

    private sealed class CachedDeviceInfo(IntPtr preparsedData, List<ContactLinkInfo> contactLinks) : IDisposable
    {
        public IntPtr PreparsedData { get; } = preparsedData;
        public List<ContactLinkInfo> ContactLinks { get; } = contactLinks;

        public void Dispose()
        {
            if (PreparsedData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PreparsedData);
            }
        }
    }
}
