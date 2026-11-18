using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinFlow.WindowsInterops.Keyboard;

public interface IKeyboardHook : IDisposable
{
    event Action<ShortcutAction>? ActionTriggered;
    void Start();
    void Stop();
}

public class KeyboardHookManager : IKeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_CAPITAL = 0x14;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SHIFT = 0x10;

    private readonly CapsLockStateMachine _stateMachine = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;
    private Thread? _hookThread;
    private uint _hookThreadId;

    public event Action<ShortcutAction>? ActionTriggered;

    public void Start()
    {
        if (_hookThread != null) return;

        using var readyEvent = new ManualResetEventSlim(false);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _proc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            var modHandle = GetModuleHandle(curModule.ModuleName);

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, modHandle, 0);
            readyEvent.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        })
        {
            IsBackground = true,
            Name = "WinFlow.KeyboardHookThread"
        };

        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        readyEvent.Wait();
    }

    public void Stop()
    {
        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _hookThread?.Join(1000);
            _hookThread = null;
            _hookThreadId = 0;
        }

        _stateMachine.Reset();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            int vkCode = Marshal.ReadInt32(lParam);

            if (vkCode == VK_ESCAPE && isKeyDown && _stateMachine.IsRecording)
            {
                _stateMachine.Reset();
                ActionTriggered?.Invoke(ShortcutAction.EscapePressed);
                return (IntPtr)1; // Consume escape
            }

            if (vkCode == VK_CAPITAL && (isKeyDown || isKeyUp))
            {
                bool isShiftDown = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                var dir = isKeyDown ? KeyDirection.Down : KeyDirection.Up;
                long now = _stopwatch.ElapsedMilliseconds;

                var action = _stateMachine.ProcessEvent(dir, isShiftDown, now);
                if (action != null)
                {
                    if (action == ShortcutAction.ShiftCapsLockPassThrough)
                    {
                        // Let Windows process native CapsLock toggle
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    ActionTriggered?.Invoke(action.Value);
                    return (IntPtr)1; // Consume CapsLock to prevent native uppercase toggle
                }

                // If event was part of active recording or lock, suppress it
                if (_stateMachine.IsRecording || _stateMachine.IsToggleLocked)
                {
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }
}
