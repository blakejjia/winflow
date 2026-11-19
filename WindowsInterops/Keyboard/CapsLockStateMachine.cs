using WinFlow.Core.Keyboard;

namespace WinFlow.WindowsInterops.Keyboard;

public class CapsLockStateMachine
{
    private const long DoubleTapThresholdMs = 300;
    private long _lastUpTimeMs = -1000;
    private bool _inShiftPassThrough;
    private bool _keyIsDown;
    private bool _suppressNextUpTap;

    public bool IsRecording { get; private set; }
    public bool IsToggleLocked { get; private set; }

    /// <summary>When false, double-tap never latches; every press is plain hold-to-talk.</summary>
    public bool DoubleTapLockEnabled { get; set; } = true;

    public ShortcutAction? ProcessEvent(KeyDirection dir, bool isShiftDown, long timestampMs)
    {
        if (dir == KeyDirection.Down)
        {
            // Windows sends repeated WM_KEYDOWN while the key is held (auto-repeat).
            // Only the first Down of a physical press may start or stop dictation.
            // The hook suppresses repeats itself while recording/locked/pass-through.
            if (_keyIsDown)
            {
                return null;
            }
            _keyIsDown = true;

            if (isShiftDown || _inShiftPassThrough)
            {
                _inShiftPassThrough = true;
                return ShortcutAction.ShiftCapsLockPassThrough;
            }

            if (IsToggleLocked)
            {
                IsToggleLocked = false;
                IsRecording = false;
                // The stop tap must not chain into a new double-tap lock.
                _suppressNextUpTap = true;
                _lastUpTimeMs = -1000;
                return ShortcutAction.ToggleStopped;
            }

            if (DoubleTapLockEnabled && timestampMs - _lastUpTimeMs <= DoubleTapThresholdMs)
            {
                IsToggleLocked = true;
                IsRecording = true;
                return ShortcutAction.ToggleLocked;
            }

            IsRecording = true;
            return ShortcutAction.HoldStarted;
        }
        else // KeyDirection.Up
        {
            _keyIsDown = false;

            if (_suppressNextUpTap)
            {
                _suppressNextUpTap = false;
                return null;
            }

            if (_inShiftPassThrough)
            {
                _inShiftPassThrough = false;
                return null;
            }

            if (IsToggleLocked)
            {
                return null;
            }

            if (IsRecording)
            {
                IsRecording = false;
                _lastUpTimeMs = timestampMs;
                return ShortcutAction.HoldReleased;
            }

            _lastUpTimeMs = timestampMs;
            return null;
        }
    }

    public void Reset()
    {
        IsRecording = false;
        IsToggleLocked = false;
        _inShiftPassThrough = false;
        _keyIsDown = false;
        _suppressNextUpTap = false;
        _lastUpTimeMs = -1000;
    }
}
