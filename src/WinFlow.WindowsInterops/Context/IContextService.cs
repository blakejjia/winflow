namespace WinFlow.WindowsInterops.Context;

public interface IContextService
{
    Task<AppContextSnapshot> CaptureContextSnapshotAsync(bool captureScreenshot = true, int maxDimension = 1024);
}
