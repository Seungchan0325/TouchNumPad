using System.Runtime.InteropServices;
using TouchNumPad.Interop;

namespace TouchNumPad.Services;

public static class KeyboardInjector
{
    private const int VK_NUMLOCK = 0x90;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    public static void SendKey(NumpadKeyDefinition key)
    {
        bool isNumLockOn = (GetKeyState(VK_NUMLOCK) & 0x0001) != 0;
        ushort vk = isNumLockOn ? key.VirtualKey : key.FallbackVirtualKey;
        SendVirtualKey(vk);
    }

    public static void SendVirtualKey(ushort vk)
    {
        ushort scan = (ushort)NativeMethods.MapVirtualKey(vk, 0);

        NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[2];

        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].U.ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            wScan = scan,
            dwFlags = 0,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].U.ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            wScan = scan,
            dwFlags = NativeMethods.KEYEVENTF_KEYUP,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
