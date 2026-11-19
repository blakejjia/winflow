namespace WinFlow.Core.TextInsertion;

public interface ITextInjector
{
    Task<bool> InjectTextAsync(string text, bool pressEnterAfter = false, int restoreDelayMs = 1000);
}
