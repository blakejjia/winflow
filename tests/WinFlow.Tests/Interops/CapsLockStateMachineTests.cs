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
