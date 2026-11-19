namespace WinFlow.Core.Pipeline;

/// <summary>Non-fatal advisory shown in the HUD (e.g. elevated target window).</summary>
public enum PipelineNotice
{
    None,
    TargetElevated
}

public enum PipelinePhase
{
    Idle,
    Initializing,
    Recording,
    Transcribing,
    Inserting,
    Feedback,
    Error
}
