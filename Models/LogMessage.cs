namespace MirrorFlux.Models;

/// <summary>
/// ログ欄の1行。時刻、種別、本文と、どのログ表示レベルから出すかを持つ。
/// </summary>
public sealed class LogMessage
{
    public LogMessage(string timestamp, string level, string message, int verbosityLevel = 0)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
        VerbosityLevel = verbosityLevel;
    }

    public string Timestamp { get; }
    public string Level { get; }
    public string Message { get; }
    public int VerbosityLevel { get; }
    public string LevelTag => $"[{Level}]";
}
