namespace WinFlow.Core.Keyboard;

public enum ShortcutAction
{
    HoldStarted,
    HoldReleased,
    ToggleLocked,
    ToggleStopped,
    ShiftCapsLockPassThrough,
    EscapePressed
}

public enum KeyDirection
{
    Down,
    Up
}

public interface IKeyboardHook : IDisposable
{
    event Action<ShortcutAction>? ActionTriggered;
    void Start();
    void Stop();
}
