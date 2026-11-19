using WinFlow.Core.Keyboard;
using WinFlow.WindowsInterops.Keyboard;
using Xunit;

namespace WinFlow.Tests.Interops;

public class CapsLockStateMachineTests
{
    [Fact]
    public void Hold_StillDownAfterThreshold_StartsRecording_ReleasePastes()
    {
        var m = new CapsLockStateMachine();

        // First press: nothing happens yet (tap-vs-hold undecided).
        Assert.Null(m.ProcessEvent(KeyDirection.Down, false, 1000));
        Assert.False(m.IsRecording);

        // Still held when the hold timer fires -> hold-to-talk begins.
        Assert.Equal(ShortcutAction.HoldStarted, m.OnHoldTimer(isShiftDown: false));
        Assert.True(m.IsRecording);
        Assert.False(m.IsToggleLocked);

        Assert.Equal(ShortcutAction.HoldReleased, m.ProcessEvent(KeyDirection.Up, false, 1600));
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void QuickTap_NeverStartsRecording_NoFlicker()
    {
        var m = new CapsLockStateMachine();

        Assert.Null(m.ProcessEvent(KeyDirection.Down, false, 1000));
        // Released before the hold threshold: this was a tap.
        Assert.Null(m.ProcessEvent(KeyDirection.Up, false, 1100));
        Assert.False(m.IsRecording);          // crucial: no start-then-retract flicker
        Assert.True(m.IsWaitingSecondTap);    // waiting for a possible second tap

        // Hold timer fires late (stale): must not start anything.
        Assert.Null(m.OnHoldTimer(false));
        Assert.False(m.IsRecording);

        // Window expires with no second tap: quietly back to idle.
        m.OnTapTimer();
        Assert.False(m.IsWaitingSecondTap);
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void DoubleTap_SecondTapStartsLocked_ThirdTapStops()
    {
        var m = new CapsLockStateMachine();

        // First quick tap.
        m.ProcessEvent(KeyDirection.Down, false, 1000);
        m.ProcessEvent(KeyDirection.Up, false, 1100);
        Assert.False(m.IsRecording);

        // Second tap within the window: locked hands-free recording starts.
        Assert.Equal(ShortcutAction.ToggleLocked, m.ProcessEvent(KeyDirection.Down, false, 1300));
        Assert.True(m.IsToggleLocked);
        Assert.Null(m.ProcessEvent(KeyDirection.Up, false, 1400)); // locked: up ignored
        Assert.True(m.IsToggleLocked);

        // Third tap stops and pastes.
        Assert.Equal(ShortcutAction.ToggleStopped, m.ProcessEvent(KeyDirection.Down, false, 3000));
        Assert.False(m.IsRecording);
        Assert.False(m.IsToggleLocked);
        m.ProcessEvent(KeyDirection.Up, false, 3100);

        // A quick next press is a fresh gesture, not an instant re-lock.
        Assert.Null(m.ProcessEvent(KeyDirection.Down, false, 3200));
        Assert.False(m.IsToggleLocked);
    }

    [Fact]
    public void SecondTapAfterWindowExpired_IsFreshFirstTap()
    {
        var m = new CapsLockStateMachine();

        m.ProcessEvent(KeyDirection.Down, false, 1000);
        m.ProcessEvent(KeyDirection.Up, false, 1100);
        m.OnTapTimer(); // 500ms window passed

        Assert.Null(m.ProcessEvent(KeyDirection.Down, false, 1800)); // new undecided press
        Assert.False(m.IsRecording);
        Assert.Equal(ShortcutAction.HoldStarted, m.OnHoldTimer(false)); // holding it -> hold mode
    }

    [Fact]
    public void DoubleTapDisabled_QuickTapDoesNothing_HoldStillWorks()
    {
        var m = new CapsLockStateMachine { DoubleTapLockEnabled = false };

        m.ProcessEvent(KeyDirection.Down, false, 1000);
        Assert.Null(m.ProcessEvent(KeyDirection.Up, false, 1100));
        Assert.False(m.IsWaitingSecondTap); // no second-tap window at all

        // A fast second press is just another undecided press, never a lock.
        Assert.Null(m.ProcessEvent(KeyDirection.Down, false, 1250));
        Assert.False(m.IsToggleLocked);
        Assert.Equal(ShortcutAction.HoldStarted, m.OnHoldTimer(false));
    }

    [Fact]
    public void AutoRepeatDowns_AreIgnored()
    {
        var m = new CapsLockStateMachine();

        m.ProcessEvent(KeyDirection.Down, false, 1000);
        m.OnHoldTimer(false);
        for (long t = 1030; t <= 1500; t += 30)
        {
            Assert.Null(m.ProcessEvent(KeyDirection.Down, false, t));
        }
        Assert.True(m.IsRecording);
        Assert.Equal(ShortcutAction.HoldReleased, m.ProcessEvent(KeyDirection.Up, false, 1600));
    }

    [Fact]
    public void ShiftCapsLock_PassesThroughNativeToggle()
    {
        var m = new CapsLockStateMachine();

        Assert.Equal(ShortcutAction.ShiftCapsLockPassThrough, m.ProcessEvent(KeyDirection.Down, true, 1000));
        Assert.False(m.ShouldConsumeKey(KeyDirection.Down)); // let Windows toggle
        Assert.False(m.IsRecording);

        Assert.Null(m.ProcessEvent(KeyDirection.Up, true, 1100));
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void ShiftPressedMidDecision_DoesNotStartRecording()
    {
        var m = new CapsLockStateMachine();

        m.ProcessEvent(KeyDirection.Down, false, 1000);
        // User presses Shift while still deciding; hold timer sees shift down.
        Assert.Null(m.OnHoldTimer(isShiftDown: true));
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void StaleTapTimer_AfterNewGesture_DoesNotClearWait()
    {
        var m = new CapsLockStateMachine();

        m.ProcessEvent(KeyDirection.Down, false, 1000);
        m.ProcessEvent(KeyDirection.Up, false, 1100);
        int gen = m.Generation;

        // Second tap arrives before the first window's timer fires.
        m.ProcessEvent(KeyDirection.Down, false, 1300);
        Assert.True(m.IsToggleLocked);
        Assert.NotEqual(gen, m.Generation); // hook uses this to drop the stale callback

        m.OnTapTimer(); // stale timer body still runs; must not disturb the lock
        Assert.True(m.IsToggleLocked);
    }

    [Fact]
    public void NonShiftCapsLock_IsAlwaysConsumed()
    {
        var m = new CapsLockStateMachine();
        m.ProcessEvent(KeyDirection.Down, false, 1000);
        Assert.True(m.ShouldConsumeKey(KeyDirection.Down));
        m.ProcessEvent(KeyDirection.Up, false, 1100);
        Assert.True(m.ShouldConsumeKey(KeyDirection.Up)); // tap up swallowed too: no stray toggle
    }
}
