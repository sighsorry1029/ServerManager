#nullable enable

using System;

namespace ServerManager.PlayerLogging
{
    public enum PlayerTelemetryDiagnosticKind
    {
        Started = 1,
        Stopped = 2,
        InvalidRecord = 3,
        EventQueueFull = 4,
        ByteBudgetFull = 5,
        WriteFailure = 6,
        ShutdownTimeout = 7,
        WorkerFailure = 8
    }

    /// <summary>
    /// A deliberately identifier-free diagnostic. It is safe for callers to
    /// forward this to the global server log without disclosing player names,
    /// account identifiers, message contents, or generated file paths.
    /// </summary>
    public sealed class PlayerTelemetryDiagnostic
    {
        internal PlayerTelemetryDiagnostic(
            PlayerTelemetryDiagnosticKind kind,
            string message,
            long occurrenceCount)
        {
            Kind = kind;
            Message = message ?? string.Empty;
            OccurrenceCount = occurrenceCount;
        }

        public PlayerTelemetryDiagnosticKind Kind { get; }

        public string Message { get; }

        public long OccurrenceCount { get; }
    }

    /// <summary>
    /// Frozen at Initialize time by <see cref="PlayerTelemetryLogWriter"/>.
    /// RootDirectory should normally be ServerManager/logs. The writer creates
    /// one child directory per authenticated Steam64 account, then one
    /// character-name/player-ID/local-date file family within that directory.
    /// </summary>
    public sealed class PlayerTelemetryLogOptions
    {
        public PlayerTelemetryLogOptions(string rootDirectory)
        {
            RootDirectory = rootDirectory ??
                            throw new ArgumentNullException(nameof(rootDirectory));
        }

        public string RootDirectory { get; set; }

        public int MaximumQueuedEvents { get; set; } = 4096;

        public long MaximumQueuedBytes { get; set; } = 64L * 1024L * 1024L;

        public int MaximumRecordBytes { get; set; } = 128 * 1024;

        public int MaximumBatchEvents { get; set; } = 256;

        public int MaximumBatchBytes { get; set; } = 1024 * 1024;

        public TimeSpan BatchWindow { get; set; } = TimeSpan.FromMilliseconds(100);

        public long MaximumFileBytes { get; set; } = 32L * 1024L * 1024L;

        /// <summary>
        /// Total character/date and same-day segment files retained per Steam account.
        /// </summary>
        public int MaximumFilesPerPlayer { get; set; } = 30;

        public TimeSpan ShutdownDrainTimeout { get; set; } =
            TimeSpan.FromSeconds(10);
    }

    public sealed class PlayerTelemetryLogStatistics
    {
        internal PlayerTelemetryLogStatistics(
            long accepted,
            long rejected,
            long queueDrops,
            long byteBudgetDrops,
            long writeFailures,
            int queuedEvents,
            long queuedBytes)
        {
            Accepted = accepted;
            Rejected = rejected;
            QueueDrops = queueDrops;
            ByteBudgetDrops = byteBudgetDrops;
            WriteFailures = writeFailures;
            QueuedEvents = queuedEvents;
            QueuedBytes = queuedBytes;
        }

        public long Accepted { get; }

        public long Rejected { get; }

        public long QueueDrops { get; }

        public long ByteBudgetDrops { get; }

        public long WriteFailures { get; }

        public int QueuedEvents { get; }

        public long QueuedBytes { get; }
    }
}
