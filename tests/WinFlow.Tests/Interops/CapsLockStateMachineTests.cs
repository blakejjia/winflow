using WinFlow.WindowsInterops.Keyboard;
using Xunit;

namespace WinFlow.Tests.Interops;

public class CapsLockStateMachineTests
{
    [Fact]
    public void HoldToTalk_NormalPressAndRelease_TriggersHoldStartedAndReleased()
    {
        var machine = new CapsLockStateMachine();

        // CapsLock pressed down at t = 1000ms, Shift is NOT down
        var action1 = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 1000);
        Assert.Equal(ShortcutAction.HoldStarted, action1);
        Assert.True(machine.IsRecording);
        Assert.False(machine.IsToggleLocked);

        // Released at t = 1600ms (held for 600ms)
        var action2 = machine.ProcessEvent(KeyDirection.Up, isShiftDown: false, timestampMs: 1600);
        Assert.Equal(ShortcutAction.HoldReleased, action2);
        Assert.False(machine.IsRecording);
    }

    [Fact]
    public void DoubleTap_Within300Ms_LocksRecording()
    {
        var machine = new CapsLockStateMachine();

        // Tap 1: Down at t = 1000, Up at t = 1100
        var action1 = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 1000);
        Assert.Equal(ShortcutAction.HoldStarted, action1);

        var action2 = machine.ProcessEvent(KeyDirection.Up, isShiftDown: false, timestampMs: 1100);
        // On quick release within double-tap window, hold released is deferred or fired, but let's check double-tap:
        // Tap 2: Down at t = 1250 (within 300ms of t = 1100)
        var action3 = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 1250);
        Assert.Equal(ShortcutAction.ToggleLocked, action3);
        Assert.True(machine.IsRecording);
        Assert.True(machine.IsToggleLocked);

        // Up at t = 1350 does nothing because we are in ToggleLocked mode
        var action4 = machine.ProcessEvent(KeyDirection.Up, isShiftDown: false, timestampMs: 1350);
        Assert.Null(action4);
        Assert.True(machine.IsToggleLocked);

        // Tap 3: Down at t = 2000 stops the locked recording
        var action5 = machine.ProcessEvent(KeyDirection.Down, isShiftDown: false, timestampMs: 2000);
        Assert.Equal(ShortcutAction.ToggleStopped, action5);
        Assert.False(machine.IsRecording);
        Assert.False(machine.IsToggleLocked);
    }

    [Fact]
    public void ShiftCapsLock_PassesThroughNativeToggle()
    {
        var machine = new CapsLockStateMachine();

        // Shift is pressed down when CapsLock is pressed
        var action = machine.ProcessEvent(KeyDirection.Down, isShiftDown: true, timestampMs: 1000);
        Assert.Equal(ShortcutAction.ShiftCapsLockPassThrough, action);
        Assert.False(machine.IsRecording);

        var actionUp = machine.ProcessEvent(KeyDirection.Up, isShiftDown: true, timestampMs: 1100);
        Assert.Null(actionUp);
        Assert.False(machine.IsRecording);
    }
}
