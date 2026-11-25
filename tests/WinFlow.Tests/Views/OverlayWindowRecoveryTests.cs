using Microsoft.Win32;
using WinFlow.Views;

namespace WinFlow.Tests.Views;

public class OverlayWindowRecoveryTests
{
    [Fact]
    public void ShouldKeepFrameLoopRunning_ReturnsFalse_WhenStaticIdleOrAdvisoryWithNoActiveTransitions()
    {
        bool keepRunning = OverlayWindow.ShouldKeepFrameLoopRunning(
            morphActive: false,
            fadeActive: false,
            waveformVisible: false,
            spinnerVisible: false,
            meetingVisible: false);

        Assert.False(keepRunning);
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void ShouldKeepFrameLoopRunning_ReturnsTrue_WhenAnyAnimationOrLiveContentIsActive(
        bool morphActive,
        bool fadeActive,
        bool waveformVisible,
        bool spinnerVisible,
        bool meetingVisible)
    {
        bool keepRunning = OverlayWindow.ShouldKeepFrameLoopRunning(
            morphActive, fadeActive, waveformVisible, spinnerVisible, meetingVisible);

        Assert.True(keepRunning);
    }

    [Theory]
    [InlineData(PowerModes.Resume, true)]
    [InlineData(PowerModes.Suspend, false)]
    [InlineData(PowerModes.StatusChange, false)]
    public void ShouldRecoverOnPowerMode_MatchesOnlyResume(PowerModes mode, bool expected)
    {
        Assert.Equal(expected, OverlayWindow.ShouldRecoverOnPowerMode(mode));
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionUnlock, true)]
    [InlineData(SessionSwitchReason.SessionLogon, true)]
    [InlineData(SessionSwitchReason.ConsoleConnect, true)]
    [InlineData(SessionSwitchReason.RemoteConnect, true)]
    [InlineData(SessionSwitchReason.SessionLock, false)]
    [InlineData(SessionSwitchReason.SessionLogoff, false)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, false)]
    [InlineData(SessionSwitchReason.RemoteDisconnect, false)]
    public void ShouldRecoverOnSessionSwitch_MatchesActiveSessionTransitions(
        SessionSwitchReason reason, bool expected)
    {
        Assert.Equal(expected, OverlayWindow.ShouldRecoverOnSessionSwitch(reason));
    }

    [Theory]
    [InlineData(0x007Eu, 0L, 0xC001u, true, false)]  // WM_DISPLAYCHANGE
    [InlineData(0x02E0u, 0L, 0xC001u, true, false)]  // WM_DPICHANGED
    [InlineData(0x031Eu, 0L, 0xC001u, true, true)]   // WM_DWMCOMPOSITIONCHANGED
    [InlineData(0xC001u, 0L, 0xC001u, true, true)]   // TaskbarCreated
    [InlineData(0x0000u, 0L, 0u, false, false)]      // Unregistered TaskbarCreated (0) must not match WM_NULL
    [InlineData(0x0218u, 0x0007L, 0xC001u, true, true)]  // WM_POWERBROADCAST + PBT_APMRESUMESUSPEND
    [InlineData(0x0218u, 0x0012L, 0xC001u, true, true)]  // WM_POWERBROADCAST + PBT_APMRESUMEAUTOMATIC
    [InlineData(0x0218u, 0x0004L, 0xC001u, false, true)] // WM_POWERBROADCAST + PBT_APMSUSPEND (ignored)
    public void WindowMessagePolicies_ReturnExpectedRecoveryAndRecreateDecisions(
        uint msg,
        long wParam,
        uint taskbarCreatedMsg,
        bool expectedRecover,
        bool expectedRecreate)
    {
        Assert.Equal(expectedRecover,
            OverlayWindow.ShouldRecoverOnWindowMessage(msg, new IntPtr(wParam), taskbarCreatedMsg));
        Assert.Equal(expectedRecreate,
            OverlayWindow.ShouldRecreateOnWindowMessage(msg, taskbarCreatedMsg));
    }
}
