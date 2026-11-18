using System.Runtime.InteropServices;

namespace WinFlow.WindowsInterops.TextInsertion;

public class WindowsTextInjector : ITextInjector
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_RETURN = 0x0D;

    public async Task<bool> InjectTextAsync(string text, bool pressEnterAfter = false, int restoreDelayMs = 1000)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // 1. Snapshot current clipboard
        var snapshot = ClipboardSnapshot.Capture();

        // 2. Set new text to clipboard
        if (!SetClipboardText(text))
            return false;

        uint pasteSeq = GetClipboardSequenceNumber();

        // 3. Small pause to allow clipboard system to stabilize
        await Task.Delay(30);

        // 4. Send Ctrl+V
        SendCtrlV();

        // 5. Optionally press Enter after 80ms
        if (pressEnterAfter)
        {
            await Task.Delay(80);
            SendKey(VK_RETURN);
        }

        // 6. Schedule background restore with collision check
        _ = Task.Run(async () =>
        {
            await Task.Delay(restoreDelayMs);
            uint currentSeq = GetClipboardSequenceNumber();
            // Only restore if user hasn't copied something new in the meantime
            if (currentSeq == pasteSeq)
            {
                snapshot.Restore();
            }
        });

        return true;
    }

    private static bool SetClipboardText(string text)
    {
        for (int retry = 0; retry < 5; retry++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero) return false;

                    var target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0); // null terminator
                        SetClipboardData(CF_UNICODETEXT, hGlobal);
                        return true;
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(10);
        }
        return false;
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];

        // Ctrl down
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL } }
        };

        // V down
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V } }
        };

        // V up
        inputs[2] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } }
        };

        // Ctrl up
        inputs[3] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } }
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort vkCode)
    {
        var inputs = new INPUT[2];

        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vkCode } }
        };

        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vkCode, dwFlags = KEYEVENTF_KEYUP } }
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}
