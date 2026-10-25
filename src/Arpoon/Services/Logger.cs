using System;
using System.Collections.Generic;

namespace Arpoon
{
    /// <summary>
    /// Process-wide, in-memory log used to back the Logs window. Every engine/service
    /// Console.WriteLine call routes through here (see <see cref="Write"/>) so the Logs
    /// window shows the same verbose detail that used to only go to the console, with
    /// each line auto-leveled (info/success/warning/error) from its own text.
    /// </summary>
    public static class Logger
    {
        private const int MaxEntries = 5000;
        private static readonly object Sync = new();
        private static readonly List<LogEntry> Entries = new();

        /// <summary>Raised whenever a new entry is added. May fire on any thread.</summary>
        public static event Action<LogEntry> EntryAdded;

        public static IReadOnlyList<LogEntry> Snapshot()
        {
            lock (Sync) return Entries.ToArray();
        }

        public static void Clear()
        {
            lock (Sync) Entries.Clear();
        }

        public static void Log(LogLevel level, string message)
        {
            var entry = new LogEntry(DateTime.Now, level, message ?? string.Empty);
            lock (Sync)
            {
                Entries.Add(entry);
                if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
            }
            Console.WriteLine(message);
            EntryAdded?.Invoke(entry);
        }

        public static void Info(string message) => Log(LogLevel.Info, message);
        public static void Success(string message) => Log(LogLevel.Success, message);
        public static void Warn(string message) => Log(LogLevel.Warning, message);
        public static void Error(string message) => Log(LogLevel.Error, message);

        /// <summary>Drop-in replacement for Console.WriteLine across the engine/service
        /// layer: infers the level from the message's own `[TAG]`/text instead of requiring
        /// every call site to be rewritten by hand.</summary>
        public static void Write(string message)
        {
            Log(InferLevel(message), message);
        }

        private static LogLevel InferLevel(string message)
        {
            if (string.IsNullOrEmpty(message)) return LogLevel.Info;
            if (message.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) return LogLevel.Error;
            if (message.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return LogLevel.Warning;
            if (message.Contains("RESTORED", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return LogLevel.Success;
            return LogLevel.Info;
        }

        /// <summary>Maps the MainWindow status-pill's level strings ("success"/"warning"/
        /// "error"/"info") onto <see cref="LogLevel"/> so every status message the user
        /// sees also lands in the log.</summary>
        public static void FromStatus(string message, string statusLevel)
        {
            LogLevel level = statusLevel?.ToLowerInvariant() switch
            {
                "success" => LogLevel.Success,
                "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                _ => LogLevel.Info,
            };
            Log(level, message);
        }
    }
}
