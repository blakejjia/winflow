using System.Diagnostics;
using System.Runtime.InteropServices;
using WinFlow.Core.Keyboard;

namespace WinFlow.WindowsInterops.Keyboard;

/// <summary>
/// Installs WH_KEYBOARD_LL on a dedicated message-pump thread.
/// The hook callback ONLY runs the state machine and queues actions; all pipeline work
/// happens on the thread pool so a busy UI can never stall global keyboard input
/// (a stalled LL hook is silently removed by Windows after its timeout).
/// </summary>
public class KeyboardHookManager : IKeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;

    private const int VK_CAPITAL = 0x14;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SHIFT = 0x10;

    private const uint LLKHF_INJECTED = 0x10;

    private readonly CapsLockStateMachine _stateMachine = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _lifecycleLock = new();

    // Deferred gesture decisions run on timers, never inside the hook callback.
    private System.Threading.Timer? _holdTimer;
    private System.Threading.Timer? _tapTimer;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc; // rooted field: the native hook holds this delegate
    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _doubleTapLockEnabled = true;
    private volatile bool _pauseInFullscreenGames = true;

    public event Action<ShortcutAction>? ActionTriggered;

    public void UpdateSettings(bool doubleTapLockEnabled, bool pauseInFullscreenGames = true)
    {
        _doubleTapLockEnabled = doubleTapLockEnabled;
        _pauseInFullscreenGames = pauseInFullscreenGames;
        _stateMachine.DoubleTapLockEnabled = doubleTapLockEnabled;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_hookThread != null) return;

            using var readyEvent = new ManualResetEventSlim(false);
            Exception? startError = null;

            _hookThread = new Thread(() =>
            {
                try
                {
                    _hookThreadId = GetCurrentThreadId();
                    _proc = HookCallback;

                    // WH_KEYBOARD_LL is a global hook: hMod may be NULL and the callback
                    // runs on this installing thread's message loop.
                    _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
                    if (_hookId == IntPtr.Zero)
                    {
                        startError = new InvalidOperationException(
                            $"SetWindowsHookEx(WH_KEYBOARD_LL) failed (Win32 error {Marshal.GetLastWin32Error()}).");
                        return;
                    }

                    // Force the thread's message queue to exist before Start() returns,
                    // so a fast Stop() cannot post WM_QUIT into the void.
                    PeekMessage(out _, IntPtr.Zero, 0, 0, 0 /* PM_NOREMOVE */);
                }
                finally
                {
                    readyEvent.Set();
                }

                if (startError != null) return;

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

            if (startError != null)
            {
                _hookThread = null;
                _hookThreadId = 0;
                _proc = null;
                throw startError;
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _hookThread?.Join(2000);
                _hookThread = null;
                _hookThreadId = 0;
                _proc = null;
            }

            _stateMachine.Reset();
            CancelGestureTimers();
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vkCode = (int)info.vkCode;
            bool injected = (info.flags & LLKHF_INJECTED) != 0;

            if (vkCode == VK_ESCAPE && isKeyDown && !injected && _stateMachine.IsRecording)
            {
                _stateMachine.Reset();
                Dispatch(ShortcutAction.EscapePressed);
                return (IntPtr)1; // Consume escape
            }

            // Fullscreen games own the keyboard: pass CapsLock through untouched and
            // reset any gesture state so nothing fires when the game exits mid-hold.
            // (Detection is cached ~500ms; the check itself is a few Win32 calls.)
            if (vkCode == VK_CAPITAL && _pauseInFullscreenGames && Context.FullscreenDetector.IsFullscreenAppActive())
            {
                if (_stateMachine.IsRecording || _stateMachine.IsToggleLocked || _stateMachine.IsWaitingSecondTap)
                {
                    _stateMachine.Reset();
                    CancelGestureTimers();
                }
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Never intercept our own synthetic input (paste Ctrl+V etc. never uses CapsLock,
            // but injected CapsLock from other tools must pass through untouched).
            if (vkCode == VK_CAPITAL && (isKeyDown || isKeyUp) && !injected)
            {
                bool isShiftDown = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                var dir = isKeyDown ? KeyDirection.Down : KeyDirection.Up;
                long now = _stopwatch.ElapsedMilliseconds;

                _stateMachine.DoubleTapLockEnabled = _doubleTapLockEnabled;
                int doubleTapWindow = GetDoubleClickTime();
                var action = _stateMachine.ProcessEvent(dir, isShiftDown, now, doubleTapWindow);

                // Schedule/cancel the deferred decisions. Generation guards make late
                // timer callbacks harmless if a newer gesture already superseded them.
                CancelGestureTimers();
                if (dir == KeyDirection.Down && action == null && !_stateMachine.IsWaitingSecondTap)
                {
                    int gen = _stateMachine.Generation;
                    _holdTimer = new System.Threading.Timer(_ =>
                    {
                        if (gen != _stateMachine.Generation) return;
                        bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                        var holdAction = _stateMachine.OnHoldTimer(shift);
                        if (holdAction != null) Dispatch(holdAction.Value);
                    }, null, CapsLockStateMachine.HoldThresholdMs, Timeout.Infinite);
                }
                else if (_stateMachine.IsWaitingSecondTap)
                {
                    int gen = _stateMachine.Generation;
                    _tapTimer = new System.Threading.Timer(_ =>
                    {
                        if (gen != _stateMachine.Generation) return;
                        _stateMachine.OnTapTimer();
                    }, null, doubleTapWindow, Timeout.Infinite);
                }

                if (action == ShortcutAction.ShiftCapsLockPassThrough)
                {
                    // Let Windows process the native CapsLock toggle.
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                if (action != null)
                {
                    Dispatch(action.Value);
                }

                if (_stateMachine.ShouldConsumeKey(dir))
                {
                    return (IntPtr)1; // swallow so Windows never toggles CapsLock
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void CancelGestureTimers()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
        _tapTimer?.Dispose();
        _tapTimer = null;
    }

    /// <summary>
    /// Queue the action off the hook thread. The callback must return fast; subscribers
    /// (AppState) may do real work and must never block global input.
    /// </summary>
    private void Dispatch(ShortcutAction action)
    {
        var handlers = ActionTriggered;
        if (handlers == null) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                handlers(action);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WinFlow] Keyboard action handler failed: {ex}");
            }
        }, null);
    }

    public void Dispose()
    {
        Stop();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
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
