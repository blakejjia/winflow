namespace WinFlow.Core.Pipeline;

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
