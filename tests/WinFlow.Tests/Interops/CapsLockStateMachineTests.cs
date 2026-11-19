using WinFlow.Core.Keyboard;
using WinFlow.WindowsInterops.Keyboard;
using Xunit;

namespace WinFlow.Tests.Interops;

public class CapsLockStateMachineTests
{
    [Fact]
    public void HoldToTalk_NormalPressAndRelease_TriggersHoldStartedAndReleased()
    {
        var machine = new CapsLockStateMachine();

        var action1 = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 1000);
        Assert.Equal(ShortcutAction.HoldStarted, action1);
        Assert.True(machine.IsRecording);
        Assert.False(machine.IsToggleLocked);

        var action2 = machine.ProcessEvent(KeyDirection.Up, isShiftDown: false, timestampMs: 1600);
        Assert.Equal(ShortcutAction.HoldReleased, action2);
        Assert.False(machine.IsRecording);
    }

    [Fact]
    public void DoubleTap_Within300Ms_LocksRecording()
    {
        var machine = new CapsLockStateMachine();

        var action1 = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 1000);
        Assert.Equal(ShortcutAction.HoldStarted, action1);

        var action2 = machine.ProcessEvent(KeyDirection.Up, isShiftDown: false, timestampMs: 1100);

        var action3 = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 1250);
        Assert.Equal(ShortcutAction.ToggleLocked, action3);
        Assert.True(machine.IsRecording);
        Assert.True(machine.IsToggleLocked);

        var action4 = machine.ProcessEvent(KeyDirection.Up, isShiftDown: false, timestampMs: 1350);
        Assert.Null(action4);
        Assert.True(machine.IsToggleLocked);

        var action5 = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 2000);
        Assert.Equal(ShortcutAction.ToggleStopped, action5);
        Assert.False(machine.IsRecording);
        Assert.False(machine.IsToggleLocked);
    }

    [Fact]
    public void AutoRepeatDowns_WhileHeld_DoNotRetriggerOrLatch()
    {
        var machine = new CapsLockStateMachine();

        var first = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 1000);
        Assert.Equal(ShortcutAction.HoldStarted, first);

        // Windows auto-repeat: repeated Downs without an Up must be ignored entirely.
        for (long t = 1030; t <= 1500; t += 30)
        {
            Assert.Null(machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: t));
        }
        Assert.True(machine.IsRecording);
        Assert.False(machine.IsToggleLocked);

        var up = machine.ProcessEvent(KeyDirection.Up, isShiftDown: false, timestampMs: 1600);
        Assert.Equal(ShortcutAction.HoldReleased, up);
    }

    [Fact]
    public void DoubleTapDisabled_FastSecondPress_IsPlainHoldToTalk()
    {
        var machine = new CapsLockStateMachine { DoubleTapLockEnabled = false };

        Assert.Equal(ShortcutAction.HoldStarted, machine.ProcessEvent(KeyDirection.Down, false, 1000));
        Assert.Equal(ShortcutAction.HoldReleased, machine.ProcessEvent(KeyDirection.Up, false, 1100));

        var second = machine.ProcessEvent(KeyDirection.Down, false, 1250);
        Assert.Equal(ShortcutAction.HoldStarted, second);
        Assert.False(machine.IsToggleLocked);
    }

    [Fact]
    public void ToggleStopTap_DoesNotChainIntoNewDoubleTapLock()
    {
        var machine = new CapsLockStateMachine();

        // Enter lock mode via double-tap.
        machine.ProcessEvent(KeyDirection.Down, false, 1000);
        machine.ProcessEvent(KeyDirection.Up, false, 1100);
        Assert.Equal(ShortcutAction.ToggleLocked, machine.ProcessEvent(KeyDirection.Down, false, 1250));
        machine.ProcessEvent(KeyDirection.Up, false, 1350);

        // Third tap stops...
        Assert.Equal(ShortcutAction.ToggleStopped, machine.ProcessEvent(KeyDirection.Down, false, 2000));
        Assert.Null(machine.ProcessEvent(KeyDirection.Up, false, 2100));

        // ...and a quick fourth press must be a fresh hold, not an instant re-lock.
        var fourth = machine.ProcessEvent(KeyDirection.Down, false, 2250);
        Assert.Equal(ShortcutAction.HoldStarted, fourth);
        Assert.False(machine.IsToggleLocked);
    }

    [Fact]
    public void ShiftCapsLock_PassesThroughNativeToggle()
    {
        var machine = new CapsLockStateMachine();

        var action = machine.ProcessEvent(KeyDirection.Down, isShiftDown: true, timestampMs: 1000);
        Assert.Equal(ShortcutAction.ShiftCapsLockPassThrough, action);
        Assert.False(machine.IsRecording);

        var actionUp = machine.ProcessEvent(KeyDirection.Up, isShiftDown: true, timestampMs: 1100);
        Assert.Null(actionUp);
        Assert.False(machine.IsRecording);
    }
}
