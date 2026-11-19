using WinFlow.Core.Keyboard;

namespace WinFlow.WindowsInterops.Keyboard;

public class CapsLockStateMachine
{
    private const long DoubleTapThresholdMs = 300;
    private long _lastDownTimeMs = -1000;
    private long _lastUpTimeMs = -1000;
    private bool _inShiftPassThrough;

    public bool IsRecording { get; private set; }
    public bool IsToggleLocked { get; private set; }

    public ShortcutAction? ProcessEvent(KeyDirection dir, bool isShiftDown, long timestampMs)
    {
        if (dir == KeyDirection.Down)
        {
            if (isShiftDown || _inShiftPassThrough)
            {
                _inShiftPassThrough = true;
                return ShortcutAction.ShiftCapsLockPassThrough;
            }

            if (IsToggleLocked)
            {
                IsToggleLocked = false;
                IsRecording = false;
                return ShortcutAction.ToggleStopped;
            }

            // Check if this Down is within double-tap window of the previous Up
            if (timestampMs - _lastUpTimeMs <= DoubleTapThresholdMs)
            {
                IsToggleLocked = true;
                IsRecording = true;
                _lastDownTimeMs = timestampMs;
                return ShortcutAction.ToggleLocked;
            }

            IsRecording = true;
            _lastDownTimeMs = timestampMs;
            return ShortcutAction.HoldStarted;
        }
        else // KeyDirection.Up
        {
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
        _lastDownTimeMs = -1000;
        _lastUpTimeMs = -1000;
    }
}
