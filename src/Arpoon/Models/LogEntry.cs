using System;

namespace Arpoon
{
    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>One line in the log: when it happened, how severe it is, and the message.</summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Message { get; }

        public LogEntry(DateTime timestamp, LogLevel level, string message)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
        }

        public string TimeDisplay => Timestamp.ToString("HH:mm:ss.fff");
        public string LevelText => Level switch
        {
            LogLevel.Success => "OK",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO",
        };

        // Matches the app's existing accent palette (see App.axaml AccentXBrush).
        public string LevelColorHex => Level switch
        {
            LogLevel.Success => "#76A86E",
            LogLevel.Warning => "#CEA65C",
            LogLevel.Error => "#B05345",
            _ => "#4A7AA4",
        };

        // Low-alpha tint of the level color, used as the row background so entries
        // stay readable while still being distinguishable at a glance.
        public string RowTintHex => Level switch
        {
            LogLevel.Success => "#1A76A86E",
            LogLevel.Warning => "#1ACEA65C",
            LogLevel.Error => "#1FB05345",
            _ => "#1A4A7AA4",
        };
    }
}
