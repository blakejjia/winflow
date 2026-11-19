using System.Runtime.InteropServices;
using System.Text;
using WinFlow.Core.Services;
using WinFlow.Core.TextInsertion;

namespace WinFlow.WindowsInterops.TextInsertion;

public class WindowsTextInjector : ITextInjector
{
    private const uint CF_UNICODETEXT = 13;

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_RETURN = 0x0D;

    private readonly ISettingsProvider? _settingsProvider;

    public WindowsTextInjector()
    {
    }

    public WindowsTextInjector(ISettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public async Task<bool> InjectTextAsync(string text, bool pressEnterAfter = false, int restoreDelayMs = 1000)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        bool preserveClipboard = _settingsProvider?.Current.PreserveClipboard ?? true;

        // 1. Snapshot current clipboard. If it cannot be opened, ABORT rather than
        //    EmptyClipboard over data we failed to back up (privacy contract).
        var snapshot = ClipboardSnapshot.Capture();
        if (snapshot == null)
            return false;

        // 2. Set new text to clipboard (plus history-exclusion markers so Win+V stays clean)
        if (!SetClipboardText(text))
            return false;

        uint pasteSeq = GetClipboardSequenceNumber();

        // 3. Small pause to allow clipboard system to stabilize
        await Task.Delay(30);

        // 4. Send Ctrl+V with any physically-held modifiers neutralized first
        SendCtrlV();

        // 5. Optionally press Enter after 80ms
        if (pressEnterAfter)
        {
            await Task.Delay(80);
            SendKey(VK_RETURN);
        }

        // 6. Schedule background restore with collision check
        if (preserveClipboard)
        {
            var captured = snapshot;
            _ = Task.Run(async () =>
            {
                await Task.Delay(restoreDelayMs);
                uint currentSeq = GetClipboardSequenceNumber();
                // Only restore if user hasn't copied something new in the meantime
                if (currentSeq == pasteSeq)
                {
                    captured.Restore();
                }
            });
        }

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

                    var bytes = Encoding.Unicode.GetBytes(text + "\0");
                    if (!ClipboardSnapshot.SetGlobalData(CF_UNICODETEXT, bytes))
                        return false;

                    // Ask clipboard history (Win+V) and cloud sync to ignore this payload,
                    // so neither the dictated text nor the restore pollutes history.
                    SetHistoryExclusionMarkers();
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(15);
        }
        return false;
    }

    private static void SetHistoryExclusionMarkers()
    {
        uint excludeHistory = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        uint excludeCloud = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        uint excludeUpload = RegisterClipboardFormat("CanUploadToCloudClipboard");

        if (excludeHistory != 0)
        {
            // Presence of this format (any data) opts out of monitor processing.
            ClipboardSnapshot.SetGlobalData(excludeHistory, new byte[4]);
        }
        if (excludeCloud != 0)
        {
            ClipboardSnapshot.SetGlobalData(excludeCloud, BitConverter.GetBytes(0));
        }
        if (excludeUpload != 0)
        {
            ClipboardSnapshot.SetGlobalData(excludeUpload, BitConverter.GetBytes(0));
        }
    }

    private static void SendCtrlV()
    {
        // Plain Ctrl+V. We deliberately do NOT synthesize key-ups for modifiers the user
        // is physically holding: that would desynchronize the physical key state.
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, up: false),
            KeyInput(VK_V, up: false),
            KeyInput(VK_V, up: true),
            KeyInput(VK_CONTROL, up: true)
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort vkCode)
    {
        var inputs = new[]
        {
            KeyInput(vkCode, up: false),
            KeyInput(vkCode, up: true)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 }
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

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
        // Union must be sized by its largest member (MOUSEINPUT) or SendInput rejects cbSize.
        [FieldOffset(0)]
        public MOUSEINPUT mi;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}
