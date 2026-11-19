namespace WinFlow.Core.Models;

public class VoiceMacro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Command { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

public static class VoiceMacroMatcher
{
    public static string? Match(string rawTranscript, IEnumerable<VoiceMacro>? macros)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript) || macros == null)
            return null;

        var normalizedTranscript = Normalize(rawTranscript);

        foreach (var macro in macros)
        {
            if (string.IsNullOrWhiteSpace(macro.Command))
                continue;

            var normalizedCommand = Normalize(macro.Command);
            if (string.Equals(normalizedTranscript, normalizedCommand, StringComparison.OrdinalIgnoreCase))
            {
                return macro.Payload;
            }
        }

        return null;
    }

    private static string Normalize(string text)
    {
        var span = text.Trim();
        var chars = span.Where(c => !char.IsPunctuation(c)).ToArray();
        return new string(chars).Trim().ToLowerInvariant();
    }
}
