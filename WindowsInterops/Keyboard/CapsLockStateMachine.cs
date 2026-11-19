using WinFlow.Core.Keyboard;

namespace WinFlow.WindowsInterops.Keyboard;

/// <summary>
/// Disambiguates tap / double-tap / hold on CapsLock, the way gesture recognizers do:
/// the first press is NOT acted on immediately.
///   - Still held after <see cref="HoldThresholdMs"/>  -> hold-to-talk (release pastes).
///   - Released quickly                               -> wait one double-click window;
///     a second tap starts locked hands-free recording, no second tap does nothing.
/// A single tap never starts (and never flickers) a recording.
/// Time is injected: the hook drives <see cref="OnHoldTimer"/> / <see cref="OnTapTimer"/>
/// from real timers; tests call them directly.
/// </summary>
public class CapsLockStateMachine
{
    public const int HoldThresholdMs = 250;
    public const int DefaultDoubleTapWindowMs = 500; // Windows GetDoubleClickTime() default

    private enum TapPhase
    {
        Idle,
        Deciding,          // first press down; hold-vs-tap not yet known
        HoldRecording,
        WaitingSecondTap,  // quick tap released; double-click window running
        Locked
    }

    private TapPhase _phase = TapPhase.Idle;
    private bool _keyIsDown;
    private bool _consumeKeyUp;
    private bool _inShiftPassThrough;
    private int _generation;

    public bool IsRecording => _phase is TapPhase.HoldRecording or TapPhase.Locked;
    public bool IsToggleLocked => _phase == TapPhase.Locked;

    /// <summary>Bumped whenever scheduled timers become stale (new gesture or reset).</summary>
    public int Generation => _generation;

    /// <summary>True while a quick first tap is waiting for a possible second tap.</summary>
    public bool IsWaitingSecondTap => _phase == TapPhase.WaitingSecondTap;

    /// <summary>When false, a quick tap never latches; only hold-to-talk works.</summary>
    public bool DoubleTapLockEnabled { get; set; } = true;

    /// <summary>
    /// Whether the hook should swallow this CapsLock event so Windows never toggles
    /// the native CapsLock state. Shift+CapsLock pass-through returns false.
    /// </summary>
    public bool ShouldConsumeKey(KeyDirection dir)
    {
        if (_inShiftPassThrough) return false;
        if (dir == KeyDirection.Up) return _consumeKeyUp;
        return true; // every non-shift CapsLock down belongs to the gesture machine
    }

    public ShortcutAction? ProcessEvent(KeyDirection dir, bool isShiftDown, long timestampMs,
        int doubleTapWindowMs = DefaultDoubleTapWindowMs)
    {
        if (dir == KeyDirection.Down)
        {
            // Auto-repeat while physically held: ignore entirely.
            if (_keyIsDown) return null;
            _keyIsDown = true;
            _generation++; // invalidate any pending hold/tap timers

            if (isShiftDown || _inShiftPassThrough)
            {
                _inShiftPassThrough = true;
                _consumeKeyUp = false;
                return ShortcutAction.ShiftCapsLockPassThrough;
            }

            switch (_phase)
            {
                case TapPhase.Locked:
                    // A tap while locked stops and pastes.
                    _phase = TapPhase.Idle;
                    _consumeKeyUp = true;
                    return ShortcutAction.ToggleStopped;

                case TapPhase.WaitingSecondTap when DoubleTapLockEnabled:
                    // Second tap of the double-tap: start locked hands-free recording.
                    _phase = TapPhase.Locked;
                    _consumeKeyUp = true;
                    return ShortcutAction.ToggleLocked;

                default:
                    // First press: decide nothing yet. The hook schedules OnHoldTimer;
                    // a quick release moves us to WaitingSecondTap instead.
                    _phase = TapPhase.Deciding;
                    _consumeKeyUp = true;
                    return null;
            }
        }
        else // KeyDirection.Up
        {
            _keyIsDown = false;

            if (_inShiftPassThrough)
            {
                _inShiftPassThrough = false;
                return null;
            }

            switch (_phase)
            {
                case TapPhase.Deciding:
                    // Released before the hold threshold: this was a tap, not a hold.
                    // Never start recording here — wait for a possible second tap.
                    _phase = DoubleTapLockEnabled ? TapPhase.WaitingSecondTap : TapPhase.Idle;
                    return null;

                case TapPhase.HoldRecording:
                    _phase = TapPhase.Idle;
                    return ShortcutAction.HoldReleased;

                default:
                    return null; // Locked / WaitingSecondTap / Idle ups do nothing
            }
        }
    }

    /// <summary>Fired by the hook HoldThresholdMs after the first press, if still held.</summary>
    public ShortcutAction? OnHoldTimer(bool isShiftDown)
    {
        if (_phase != TapPhase.Deciding || !_keyIsDown) return null;

        if (isShiftDown)
        {
            // Shift went down mid-decision: the user wants the native toggle.
            // The Down was already swallowed, so swallow the Up too (no stray toggle).
            _inShiftPassThrough = true;
            _phase = TapPhase.Idle;
            return null;
        }

        _phase = TapPhase.HoldRecording;
        return ShortcutAction.HoldStarted;
    }

    /// <summary>Fired when the double-click window expires with no second tap.</summary>
    public void OnTapTimer()
    {
        if (_phase == TapPhase.WaitingSecondTap)
        {
            _phase = TapPhase.Idle;
        }
    }

    public void Reset()
    {
        _phase = TapPhase.Idle;
        _keyIsDown = false;
        _consumeKeyUp = false;
        _inShiftPassThrough = false;
        _generation++;
    }
}
