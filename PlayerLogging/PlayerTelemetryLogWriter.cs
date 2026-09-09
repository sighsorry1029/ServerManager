#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ServerManager.PlayerLogging
{
    /// <summary>
    /// Bounded, asynchronous, per-player plain-text audit writer. TryWrite
    /// validates and formats one human-readable line; directory and file
    /// operations are confined to the background worker.
    /// </summary>
    public sealed class PlayerTelemetryLogWriter : IDisposable
    {
        private const ulong IndividualSteamId64Base = 76561197960265728UL;
        private const ulong MinimumIndividualSteamId64 =
            IndividualSteamId64Base + 1UL;
        private const ulong MaximumIndividualSteamId64 =
            IndividualSteamId64Base + uint.MaxValue;
        private const string LogFileExtension = ".log";
        private static readonly UTF8Encoding Utf8WithoutBom =
            new UTF8Encoding(false, true);

        private readonly object _lifecycleGate = new object();
        private BlockingCollection<QueuedRecord>? _queue;
        private FrozenOptions? _options;
        private Action<PlayerTelemetryDiagnostic>? _diagnostics;
        private Thread? _worker;
        private int _state;
        private long _queuedBytes;
        private long _accepted;
        private long _rejected;
        private long _queueDrops;
        private long _byteBudgetDrops;
        private long _writeFailures;

        private enum WriterState
        {
            Created = 0,
            Initialized = 1,
            Running = 2,
            Stopping = 3,
            Stopped = 4,
            Disposed = 5
        }

        public void Initialize(
            PlayerTelemetryLogOptions options,
            Action<PlayerTelemetryDiagnostic>? diagnostics = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            FrozenOptions frozen = FrozenOptions.Create(options);
            lock (_lifecycleGate)
            {
                if ((WriterState)_state != WriterState.Created)
                {
                    throw new InvalidOperationException(
                        "The player log writer can only be initialized once.");
                }

                _options = frozen;
                _diagnostics = diagnostics;
                _queue = new BlockingCollection<QueuedRecord>(
                    new ConcurrentQueue<QueuedRecord>(),
                    frozen.MaximumQueuedEvents);
                Volatile.Write(ref _state, (int)WriterState.Initialized);
            }
        }

        /// <summary>
        /// Starts the worker. No directory or file I/O is performed by this
        /// method; storage is opened lazily by the worker after the first record.
        /// A stopped writer is terminal and cannot be restarted.
        /// </summary>
        public void Start()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                if ((WriterState)_state != WriterState.Initialized)
                {
                    throw new InvalidOperationException(
                        "Initialize must be called exactly once before Start.");
                }

                Thread worker = new Thread(WriteLoop)
                {
                    IsBackground = true,
                    Name = "ServerManager player log"
                };
                _worker = worker;
                Volatile.Write(ref _state, (int)WriterState.Running);
                try
                {
                    worker.Start();
                }
                catch
                {
                    _worker = null;
                    Volatile.Write(ref _state, (int)WriterState.Initialized);
                    throw;
                }
            }

            Report(
                PlayerTelemetryDiagnosticKind.Started,
                "Player log writer started.",
                1);
        }

        public bool TryWrite(
            string playerDirectoryKey,
            string characterName,
            long playerId,
            string message)
        {
            return TryWrite(
                playerDirectoryKey,
                characterName,
                playerId,
                DateTime.UtcNow,
                message);
        }

        public bool TryWrite(
            string playerDirectoryKey,
            string characterName,
            long playerId,
            DateTime occurredAtUtc,
            string message)
        {
            if ((WriterState)Volatile.Read(ref _state) != WriterState.Running)
            {
                return false;
            }

            FrozenOptions? options = _options;
            BlockingCollection<QueuedRecord>? queue = _queue;
            if (options == null || queue == null)
            {
                return false;
            }

            QueuedRecord? record;
            string rejection;
            if (!TryCreateRecord(
                    playerDirectoryKey,
                    characterName,
                    playerId,
                    occurredAtUtc,
                    message,
                    options,
                    out record,
                    out rejection))
            {
                long rejected = Interlocked.Increment(ref _rejected);
                ReportSparsely(
                    PlayerTelemetryDiagnosticKind.InvalidRecord,
                    rejection,
                    rejected);
                return false;
            }

            return TryEnqueueRecord(record!, options, queue);
        }

        internal bool TryWriteBlock(
            string playerDirectoryKey,
            string characterName,
            long playerId,
            string header,
            IReadOnlyList<string> continuationLines)
        {
            return TryWriteBlock(
                playerDirectoryKey,
                characterName,
                playerId,
                DateTime.UtcNow,
                header,
                continuationLines);
        }

        internal bool TryWriteBlock(
            string playerDirectoryKey,
            string characterName,
            long playerId,
            DateTime occurredAtUtc,
            string header,
            IReadOnlyList<string> continuationLines)
        {
            if ((WriterState)Volatile.Read(ref _state) != WriterState.Running)
            {
                return false;
            }

            FrozenOptions? options = _options;
            BlockingCollection<QueuedRecord>? queue = _queue;
            if (options == null || queue == null)
            {
                return false;
            }

            QueuedRecord? record;
            string rejection;
            if (!TryCreateBlockRecord(
                    playerDirectoryKey,
                    characterName,
                    playerId,
                    occurredAtUtc,
                    header,
                    continuationLines,
                    options,
                    out record,
                    out rejection))
            {
                long rejected = Interlocked.Increment(ref _rejected);
                ReportSparsely(
                    PlayerTelemetryDiagnosticKind.InvalidRecord,
                    rejection,
                    rejected);
                return false;
            }

            return TryEnqueueRecord(record!, options, queue);
        }

        private bool TryEnqueueRecord(
            QueuedRecord record,
            FrozenOptions options,
            BlockingCollection<QueuedRecord> queue)
        {
            if (!TryReserveBytes(record.SerializedBytes, options.MaximumQueuedBytes))
            {
                long dropped = Interlocked.Increment(ref _byteBudgetDrops);
                ReportSparsely(
                    PlayerTelemetryDiagnosticKind.ByteBudgetFull,
                    "Player log byte budget is full; records were dropped.",
                    dropped);
                return false;
            }

            bool added;
            try
            {
                added = queue.TryAdd(record);
            }
            catch (InvalidOperationException)
            {
                added = false;
            }

            if (!added)
            {
                Interlocked.Add(ref _queuedBytes, -record.SerializedBytes);
                long dropped = Interlocked.Increment(ref _queueDrops);
                ReportSparsely(
                    PlayerTelemetryDiagnosticKind.EventQueueFull,
                    "Player log queue is full or stopping; records were dropped.",
                    dropped);
                return false;
            }

            Interlocked.Increment(ref _accepted);
            return true;
        }

        public PlayerTelemetryLogStatistics GetStatistics()
        {
            BlockingCollection<QueuedRecord>? queue = _queue;
            int queuedEvents = queue == null ? 0 : queue.Count;
            return new PlayerTelemetryLogStatistics(
                Interlocked.Read(ref _accepted),
                Interlocked.Read(ref _rejected),
                Interlocked.Read(ref _queueDrops),
                Interlocked.Read(ref _byteBudgetDrops),
                Interlocked.Read(ref _writeFailures),
                queuedEvents,
                Math.Max(0, Interlocked.Read(ref _queuedBytes)));
        }

        public bool Stop()
        {
            FrozenOptions? options = _options;
            TimeSpan timeout = options == null
                ? TimeSpan.Zero
                : options.ShutdownDrainTimeout;
            return Stop(timeout);
        }

        /// <summary>
        /// Stops accepting records and waits for the background worker to drain
        /// the queue. A false result means the worker is still draining; no I/O
        /// is transferred to the calling thread.
        /// </summary>
        public bool Stop(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            BlockingCollection<QueuedRecord>? queue;
            Thread? worker;
            lock (_lifecycleGate)
            {
                WriterState state = (WriterState)_state;
                if (state == WriterState.Created || state == WriterState.Disposed)
                {
                    return true;
                }

                queue = _queue;
                worker = _worker;
                if (state == WriterState.Initialized)
                {
                    queue?.CompleteAdding();
                    Volatile.Write(ref _state, (int)WriterState.Stopped);
                    return true;
                }

                if (state == WriterState.Stopped &&
                    (worker == null || !worker.IsAlive))
                {
                    return true;
                }

                if (state == WriterState.Running)
                {
                    Volatile.Write(ref _state, (int)WriterState.Stopping);
                    queue?.CompleteAdding();
                }
            }

            if (worker == null || worker == Thread.CurrentThread)
            {
                return worker == null;
            }

            bool drained = timeout == Timeout.InfiniteTimeSpan
                ? JoinWithoutTimeout(worker)
                : worker.Join(timeout);
            if (!drained)
            {
                Report(
                    PlayerTelemetryDiagnosticKind.ShutdownTimeout,
                    "Timed out while the player log worker was draining.",
                    1);
            }

            return drained;
        }

        public void Dispose()
        {
            WriterState state = (WriterState)Volatile.Read(ref _state);
            if (state == WriterState.Disposed)
            {
                return;
            }

            Thread? worker = _worker;
            if (worker == Thread.CurrentThread)
            {
                throw new InvalidOperationException(
                    "The player log writer cannot be disposed by its " +
                    "own worker or diagnostic callback.");
            }

            // Stop(timeout) deliberately leaves the writer in Stopping state
            // when its deadline expires. Disposal is the terminal operation,
            // so it must wait for that same worker instead of marking live I/O
            // as disposed or attempting to create a replacement worker.
            if (!Stop(Timeout.InfiniteTimeSpan))
            {
                throw new InvalidOperationException(
                    "The player log worker could not be drained.");
            }

            lock (_lifecycleGate)
            {
                Volatile.Write(ref _state, (int)WriterState.Disposed);
                _diagnostics = null;
            }
        }

        private static bool JoinWithoutTimeout(Thread worker)
        {
            worker.Join();
            return true;
        }

        private void WriteLoop()
        {
            try
            {
                BlockingCollection<QueuedRecord>? queue = _queue;
                FrozenOptions? options = _options;
                if (queue == null || options == null)
                {
                    return;
                }

                List<QueuedRecord> batch =
                    new List<QueuedRecord>(options.MaximumBatchEvents);
                QueuedRecord? carried = null;
                while (carried != null || !queue.IsCompleted)
                {
                    QueuedRecord? first;
                    if (carried != null)
                    {
                        first = carried;
                        carried = null;
                    }
                    else
                    {
                        if (!queue.TryTake(out first, 250))
                        {
                            continue;
                        }

                        ReleaseQueuedBytes(first);
                    }

                    if (batch.Count != 0)
                    {
                        throw new InvalidOperationException(
                            "The player log batch was not cleared.");
                    }

                    batch.Add(first);
                    long batchBytes = first.SerializedBytes;

                    if (batch.Count < options.MaximumBatchEvents &&
                        batchBytes < options.MaximumBatchBytes &&
                        options.BatchWindowMilliseconds > 0)
                    {
                        QueuedRecord? next;
                        if (queue.TryTake(
                                out next,
                                options.BatchWindowMilliseconds))
                        {
                            ReleaseQueuedBytes(next);
                            if (next.SerializedBytes <=
                                options.MaximumBatchBytes - batchBytes)
                            {
                                batch.Add(next);
                                batchBytes += next.SerializedBytes;
                            }
                            else
                            {
                                carried = next;
                            }
                        }
                    }

                    QueuedRecord? queued;
                    while (carried == null &&
                           batch.Count < options.MaximumBatchEvents &&
                           queue.TryTake(out queued, 0))
                    {
                        ReleaseQueuedBytes(queued);
                        if (queued.SerializedBytes <=
                            options.MaximumBatchBytes - batchBytes)
                        {
                            batch.Add(queued);
                            batchBytes += queued.SerializedBytes;
                        }
                        else
                        {
                            carried = queued;
                        }
                    }

                    WriteBatch(batch, options);
                    batch.Clear();
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                Report(
                    PlayerTelemetryDiagnosticKind.WorkerFailure,
                    "Player log worker stopped after " +
                    exception.GetType().Name + ".",
                    1);
            }
            finally
            {
                BlockingCollection<QueuedRecord>? queue = _queue;
                lock (_lifecycleGate)
                {
                    if ((WriterState)_state == WriterState.Running)
                    {
                        Volatile.Write(ref _state, (int)WriterState.Stopping);
                        queue?.CompleteAdding();
                    }
                }

                if (queue != null)
                {
                    QueuedRecord? abandoned;
                    while (queue.TryTake(out abandoned))
                    {
                        ReleaseQueuedBytes(abandoned);
                    }
                }

                Report(
                    PlayerTelemetryDiagnosticKind.Stopped,
                    "Player log writer stopped.",
                    1);

                lock (_lifecycleGate)
                {
                    if ((WriterState)_state != WriterState.Disposed)
                    {
                        Volatile.Write(ref _state, (int)WriterState.Stopped);
                    }
                }
            }
        }

        private void WriteBatch(
            List<QueuedRecord> batch,
            FrozenOptions options)
        {
            Dictionary<string, List<QueuedRecord>> grouped =
                new Dictionary<string, List<QueuedRecord>>(StringComparer.Ordinal);
            for (int index = 0; index < batch.Count; ++index)
            {
                QueuedRecord record = batch[index];
                List<QueuedRecord>? records;
                if (!grouped.TryGetValue(record.PlayerDirectoryKey, out records))
                {
                    records = new List<QueuedRecord>();
                    grouped.Add(record.PlayerDirectoryKey, records);
                }

                records.Add(record);
            }

            foreach (KeyValuePair<string, List<QueuedRecord>> pair in grouped)
            {
                try
                {
                    WritePlayerBatch(pair.Key, pair.Value, options);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    long failures = Interlocked.Increment(ref _writeFailures);
                    ReportSparsely(
                        PlayerTelemetryDiagnosticKind.WriteFailure,
                        "Player log write failed with " +
                        exception.GetType().Name + ".",
                        failures);
                }
            }
        }

        private static void WritePlayerBatch(
            string playerDirectoryKey,
            List<QueuedRecord> records,
            FrozenOptions options)
        {
            string root = EnsureRegularDirectory(
                options.RootDirectory,
                "player log root");
            string playerDirectory = GetContainedPlayerDirectory(
                root,
                playerDirectoryKey);
            EnsureRegularDirectory(playerDirectory, "player log directory");

            SortedDictionary<string, List<QueuedRecord>> recordsByFileName =
                new SortedDictionary<string, List<QueuedRecord>>(
                    StringComparer.Ordinal);
            for (int recordIndex = 0; recordIndex < records.Count; ++recordIndex)
            {
                QueuedRecord record = records[recordIndex];
                List<QueuedRecord>? dateRecords;
                if (!recordsByFileName.TryGetValue(
                        record.CharacterDateFileName,
                        out dateRecords))
                {
                    dateRecords = new List<QueuedRecord>();
                    recordsByFileName.Add(
                        record.CharacterDateFileName,
                        dateRecords);
                }

                dateRecords.Add(record);
            }

            StringComparer pathComparer = Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            HashSet<string> protectedActivePaths =
                new HashSet<string>(pathComparer);
            foreach (KeyValuePair<string, List<QueuedRecord>> pair in
                     recordsByFileName)
            {
                string activePath = WritePlayerDateBatch(
                    playerDirectory,
                    pair.Key,
                    pair.Value,
                    options);
                protectedActivePaths.Add(activePath);
            }

            EnforcePlayerFileRetention(
                playerDirectory,
                options.MaximumFilesPerPlayer,
                protectedActivePaths);
        }

        private static string WritePlayerDateBatch(
            string playerDirectory,
            string localDateFileName,
            List<QueuedRecord> records,
            FrozenOptions options)
        {
            string activePath = GetContainedFilePath(
                playerDirectory,
                localDateFileName);
            StringBuilder pending = new StringBuilder();
            long pendingBytes = 0;
            long activeBytes = GetRegularFileLength(activePath);
            EnsureTrailingNewline(activePath, ref activeBytes);
            for (int index = 0; index < records.Count; ++index)
            {
                QueuedRecord record = records[index];
                string line = record.SerializedLine;
                int lineBytes = record.SerializedBytes;

                if (pendingBytes > 0 &&
                    activeBytes + pendingBytes + lineBytes > options.MaximumFileBytes)
                {
                    Append(activePath, pending.ToString());
                    activeBytes += pendingBytes;
                    pending.Clear();
                    pendingBytes = 0;
                }

                if (activeBytes > 0 &&
                    activeBytes + pendingBytes + lineBytes > options.MaximumFileBytes)
                {
                    if (pendingBytes > 0)
                    {
                        Append(activePath, pending.ToString());
                        pending.Clear();
                        pendingBytes = 0;
                    }

                    Rotate(activePath);
                    activeBytes = 0;
                }

                pending.Append(line);
                pendingBytes += lineBytes;
            }

            if (pendingBytes > 0)
            {
                Append(activePath, pending.ToString());
            }

            return activePath;
        }

        private static void EnsureTrailingNewline(
            string activePath,
            ref long activeBytes)
        {
            if (activeBytes <= 0)
            {
                return;
            }

            EnsureRegularFileIfPresent(activePath);
            int finalByte;
            using (FileStream stream = new FileStream(
                       activePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       1,
                       FileOptions.RandomAccess))
            {
                stream.Seek(-1, SeekOrigin.End);
                finalByte = stream.ReadByte();
            }

            if (finalByte == '\n')
            {
                return;
            }

            Append(activePath, "\n");
            activeBytes = checked(activeBytes + 1L);
        }

        private static void Append(string path, string content)
        {
            EnsureRegularFileIfPresent(path);
            using (FileStream stream = new FileStream(
                       path,
                       FileMode.Append,
                       FileAccess.Write,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            using (StreamWriter writer = new StreamWriter(
                       stream,
                       Utf8WithoutBom,
                       64 * 1024))
            {
                writer.Write(content);
            }
        }

        private static void Rotate(string activePath)
        {
            EnsureRegularFileIfPresent(activePath);
            if (!File.Exists(activePath))
            {
                return;
            }

            File.Move(activePath, GetNextRotatedPath(activePath));
        }

        private static string GetNextRotatedPath(string activePath)
        {
            string? directory = Path.GetDirectoryName(activePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    "The player log rotation directory is unavailable.");
            }

            string activeName = Path.GetFileName(activePath);
            string prefix = activeName + ".";
            long maximumGeneration = 0;
            foreach (string candidate in Directory.EnumerateFiles(
                         directory,
                         prefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(candidate);
                if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string suffix = fileName.Substring(prefix.Length);
                long generation;
                if (!long.TryParse(
                        suffix,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out generation) ||
                    generation < 1)
                {
                    continue;
                }

                string canonical = GetRotatedPath(activePath, generation);
                StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!string.Equals(
                        Path.GetFullPath(candidate),
                        Path.GetFullPath(canonical),
                        comparison))
                {
                    continue;
                }

                EnsureRegularFileIfPresent(candidate);
                maximumGeneration = Math.Max(maximumGeneration, generation);
            }

            if (maximumGeneration == long.MaxValue)
            {
                throw new IOException(
                    "The player log rotation generation was exhausted.");
            }

            return GetRotatedPath(activePath, maximumGeneration + 1L);
        }

        private static string GetRotatedPath(string activePath, long generation)
        {
            return activePath + "." + generation.ToString(
                "D2",
                CultureInfo.InvariantCulture);
        }

        private static void EnforcePlayerFileRetention(
            string playerDirectory,
            int maximumFiles,
            HashSet<string> protectedActivePaths)
        {
            List<TelemetryFile> files = new List<TelemetryFile>();
            foreach (string candidate in Directory.EnumerateFiles(
                         playerDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(candidate);
                DateTime localDate;
                long generation;
                if (!TryParseTelemetryFileName(
                        fileName,
                        out localDate,
                        out generation))
                {
                    continue;
                }

                string canonical = GetContainedFilePath(
                    playerDirectory,
                    fileName);
                StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!string.Equals(
                        Path.GetFullPath(candidate),
                        canonical,
                        comparison))
                {
                    continue;
                }

                EnsureRegularFileIfPresent(canonical);
                files.Add(new TelemetryFile(canonical, localDate, generation));
            }

            if (files.Count <= maximumFiles)
            {
                return;
            }

            // Old dates are discarded first. Within one date, monotonically
            // increasing segment numbers are oldest first and the active file
            // is always newest.
            files.Sort((left, right) =>
            {
                int result = left.LocalDate.CompareTo(right.LocalDate);
                if (result != 0)
                {
                    return result;
                }

                if (left.Generation == 0 || right.Generation == 0)
                {
                    result = left.Generation == right.Generation
                        ? 0
                        : left.Generation == 0 ? 1 : -1;
                }
                else
                {
                    result = left.Generation.CompareTo(right.Generation);
                }

                return result != 0
                    ? result
                    : string.CompareOrdinal(left.Path, right.Path);
            });

            int remainingFiles = files.Count;
            for (int index = 0;
                 index < files.Count && remainingFiles > maximumFiles;
                 ++index)
            {
                if (protectedActivePaths.Contains(files[index].Path))
                {
                    continue;
                }

                DeleteRegularFileIfPresent(files[index].Path);
                --remainingFiles;
            }

            // Protection is a first-choice retention preference, not an
            // exemption from the configured hard cap. If one batch spans more
            // active server-local dates than the cap allows, discard the
            // oldest of those protected dates after every unprotected candidate.
            for (int index = 0;
                 index < files.Count && remainingFiles > maximumFiles;
                 ++index)
            {
                if (!protectedActivePaths.Contains(files[index].Path) ||
                    !File.Exists(files[index].Path))
                {
                    continue;
                }

                DeleteRegularFileIfPresent(files[index].Path);
                --remainingFiles;
            }
        }

        private static bool TryParseTelemetryFileName(
            string fileName,
            out DateTime localDate,
            out long generation)
        {
            localDate = default;
            generation = 0;
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            int extensionIndex = fileName.LastIndexOf(
                LogFileExtension,
                StringComparison.Ordinal);
            if (extensionIndex <= 0)
            {
                return false;
            }

            int activeNameLength = checked(
                extensionIndex + LogFileExtension.Length);
            string activeName = fileName.Substring(0, activeNameLength);
            if (fileName.Length != activeNameLength)
            {
                if (fileName.Length <= activeNameLength + 1 ||
                    fileName[activeNameLength] != '.' ||
                    !long.TryParse(
                        fileName.Substring(activeNameLength + 1),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out generation) ||
                    generation < 1 ||
                    !string.Equals(
                        fileName,
                        activeName + "." + generation.ToString(
                            "D2",
                            CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            string baseName = activeName.Substring(
                0,
                activeName.Length - LogFileExtension.Length);
            const int dateCharacters = 10;
            int dateSeparatorIndex = baseName.Length - dateCharacters - 1;
            if (dateSeparatorIndex <= 0 ||
                baseName[dateSeparatorIndex] != '_' ||
                !DateTime.TryParseExact(
                    baseName.Substring(dateSeparatorIndex + 1),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out localDate) ||
                !string.Equals(
                    baseName.Substring(dateSeparatorIndex + 1),
                    localDate.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                return false;
            }

            string characterFileStem = baseName.Substring(
                0,
                dateSeparatorIndex);
            int playerIdSeparatorIndex = characterFileStem.LastIndexOf('_');
            if (playerIdSeparatorIndex <= 0 ||
                !long.TryParse(
                    characterFileStem.Substring(playerIdSeparatorIndex + 1),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out long playerId) ||
                playerId == 0 ||
                !string.Equals(
                    characterFileStem.Substring(playerIdSeparatorIndex + 1),
                    playerId.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) ||
                !IsCanonicalCharacterFileNameComponent(
                    characterFileStem.Substring(0, playerIdSeparatorIndex)))
            {
                return false;
            }

            return true;
        }

        private static bool TryCreateCharacterFileStem(
            string characterName,
            long playerId,
            out string characterFileStem)
        {
            characterFileStem = string.Empty;
            if (string.IsNullOrWhiteSpace(characterName) || playerId == 0)
            {
                return false;
            }

            string normalized;
            try
            {
                normalized = characterName.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                return false;
            }

            const int maximumNameCharacters = 64;
            StringBuilder safe = new StringBuilder(
                Math.Min(normalized.Length, maximumNameCharacters));
            for (int index = 0;
                 index < normalized.Length && safe.Length < maximumNameCharacters;
                 ++index)
            {
                char character = normalized[index];
                bool invalid = char.IsControl(character) ||
                               char.IsSurrogate(character) ||
                               character == '<' || character == '>' ||
                               character == ':' || character == '"' ||
                               character == '/' || character == '\\' ||
                               character == '|' || character == '?' ||
                               character == '*' || character == '~';
                safe.Append(invalid ? '_' : character);
            }

            while (safe.Length > 0 &&
                   (safe[safe.Length - 1] == ' ' ||
                    safe[safe.Length - 1] == '.'))
            {
                --safe.Length;
            }

            if (safe.Length == 0 ||
                string.Equals(safe.ToString(), ".", StringComparison.Ordinal) ||
                string.Equals(safe.ToString(), "..", StringComparison.Ordinal))
            {
                return false;
            }

            characterFileStem = safe.ToString() + "_" +
                                playerId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool IsCanonicalCharacterFileNameComponent(string value)
        {
            // Use the writer's filename rules for retention too. Names that
            // still need sanitizing (including old '~hash' names) are not ours.
            return TryCreateCharacterFileStem(value, 1, out string stem) &&
                   string.Equals(stem, value + "_1", StringComparison.Ordinal);
        }

        private static string Serialize(
            DateTimeOffset serverLocalTime,
            string message)
        {
            return "[" + serverLocalTime.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture) + "] " + message + "\n";
        }

        private static string SerializeBlock(
            DateTimeOffset serverLocalTime,
            string header,
            IReadOnlyList<string> continuationLines)
        {
            StringBuilder serialized = new StringBuilder();
            serialized.Append('[')
                .Append(serverLocalTime.ToString(
                    "HH:mm:ss",
                    CultureInfo.InvariantCulture))
                .Append("] ")
                .Append(header)
                .Append('\n');
            for (int index = 0; index < continuationLines.Count; ++index)
            {
                serialized.Append(continuationLines[index]).Append('\n');
            }

            return serialized.ToString();
        }

        private bool TryCreateRecord(
            string playerDirectoryKey,
            string characterName,
            long playerId,
            DateTime occurredAtUtc,
            string message,
            FrozenOptions options,
            out QueuedRecord? record,
            out string rejection)
        {
            record = null;
            if (!IsValidIndividualSteam64(playerDirectoryKey))
            {
                rejection =
                    "A player log record had an invalid Steam64 directory key.";
                return false;
            }

            string characterFileStem;
            if (!TryCreateCharacterFileStem(
                    characterName,
                    playerId,
                    out characterFileStem))
            {
                rejection =
                    "A player log record had an invalid character identity.";
                return false;
            }

            if (occurredAtUtc == default ||
                occurredAtUtc.Kind == DateTimeKind.Unspecified)
            {
                rejection = "A player log record had an invalid timestamp.";
                return false;
            }

            if (message == null)
            {
                rejection = "A player log record had a null message.";
                return false;
            }

            DateTime normalizedOccurredAtUtc;
            DateTimeOffset serverLocalTime;
            string normalizedMessage;
            string serialized;
            int serializedBytes;
            try
            {
                normalizedOccurredAtUtc = occurredAtUtc.Kind == DateTimeKind.Utc
                    ? occurredAtUtc
                    : occurredAtUtc.ToUniversalTime();
                serverLocalTime = TimeZoneInfo.ConvertTime(
                    new DateTimeOffset(normalizedOccurredAtUtc),
                    options.ServerTimeZone);

                int prefixBytes = Utf8WithoutBom.GetByteCount(
                    "[" + serverLocalTime.ToString(
                        "HH:mm:ss",
                        CultureInfo.InvariantCulture) + "] \n");
                int maximumMessageBytes = options.MaximumRecordBytes - prefixBytes;
                if (maximumMessageBytes < 1)
                {
                    rejection = "A player log record could not fit its timestamp.";
                    return false;
                }

                int normalizedBytes;
                normalizedMessage = NormalizeAndTruncate(
                    message,
                    maximumMessageBytes,
                    out normalizedBytes);
                if (string.IsNullOrWhiteSpace(normalizedMessage))
                {
                    rejection = "A player log record requires a nonblank message.";
                    return false;
                }

                serialized = Serialize(serverLocalTime, normalizedMessage);
                serializedBytes = checked(prefixBytes + normalizedBytes);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                rejection = "A player log record could not be formatted.";
                return false;
            }

            if (serializedBytes > options.MaximumRecordBytes)
            {
                rejection = "A player log record exceeded the payload cap.";
                return false;
            }

            record = new QueuedRecord(
                playerDirectoryKey,
                characterFileStem + "_" + serverLocalTime.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture) + LogFileExtension,
                serialized,
                serializedBytes);
            rejection = string.Empty;
            return true;
        }

        private bool TryCreateBlockRecord(
            string playerDirectoryKey,
            string characterName,
            long playerId,
            DateTime occurredAtUtc,
            string header,
            IReadOnlyList<string> continuationLines,
            FrozenOptions options,
            out QueuedRecord? record,
            out string rejection)
        {
            record = null;
            if (!IsValidIndividualSteam64(playerDirectoryKey))
            {
                rejection =
                    "A player log block had an invalid Steam64 directory key.";
                return false;
            }

            string characterFileStem;
            if (!TryCreateCharacterFileStem(
                    characterName,
                    playerId,
                    out characterFileStem))
            {
                rejection =
                    "A player log block had an invalid character identity.";
                return false;
            }

            if (occurredAtUtc == default ||
                occurredAtUtc.Kind == DateTimeKind.Unspecified)
            {
                rejection = "A player log block had an invalid timestamp.";
                return false;
            }

            if (header == null || continuationLines == null ||
                continuationLines.Count == 0)
            {
                rejection = "A player log block requires a header and content.";
                return false;
            }

            try
            {
                DateTime normalizedOccurredAtUtc =
                    occurredAtUtc.Kind == DateTimeKind.Utc
                        ? occurredAtUtc
                        : occurredAtUtc.ToUniversalTime();
                DateTimeOffset serverLocalTime = TimeZoneInfo.ConvertTime(
                    new DateTimeOffset(normalizedOccurredAtUtc),
                    options.ServerTimeZone);

                string normalizedHeader = NormalizeWithoutTruncation(header);
                if (string.IsNullOrWhiteSpace(normalizedHeader))
                {
                    rejection = "A player log block requires a nonblank header.";
                    return false;
                }

                List<string> normalizedLines =
                    new List<string>(continuationLines.Count);
                for (int index = 0; index < continuationLines.Count; ++index)
                {
                    string? line = continuationLines[index];
                    if (line == null)
                    {
                        rejection =
                            "A player log block contained a null continuation.";
                        return false;
                    }

                    string normalized = NormalizeWithoutTruncation(line);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        rejection =
                            "A player log block contained a blank continuation.";
                        return false;
                    }

                    normalizedLines.Add(normalized);
                }

                string serialized = SerializeBlock(
                    serverLocalTime,
                    normalizedHeader,
                    normalizedLines);
                int serializedBytes = Utf8WithoutBom.GetByteCount(serialized);
                if (serializedBytes > options.MaximumQueuedBytes)
                {
                    rejection =
                        "A player log block exceeded the bounded queue capacity.";
                    return false;
                }

                record = new QueuedRecord(
                    playerDirectoryKey,
                    characterFileStem + "_" + serverLocalTime.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture) + LogFileExtension,
                    serialized,
                    serializedBytes);
                rejection = string.Empty;
                return true;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                rejection = "A player log block could not be formatted.";
                return false;
            }
        }

        private bool TryReserveBytes(long bytes, long maximum)
        {
            while (true)
            {
                long current = Interlocked.Read(ref _queuedBytes);
                if (bytes > maximum - current)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref _queuedBytes,
                        current + bytes,
                        current) == current)
                {
                    return true;
                }
            }
        }

        private void ReleaseQueuedBytes(QueuedRecord record)
        {
            long remaining = Interlocked.Add(
                ref _queuedBytes,
                -record.SerializedBytes);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _queuedBytes, 0);
            }
        }

        private static string NormalizeAndTruncate(
            string value,
            int maximumUtf8Bytes,
            out int normalizedUtf8Bytes)
        {
            int initialCapacity = Math.Min(value.Length, maximumUtf8Bytes);
            StringBuilder normalized = new StringBuilder(initialCapacity);
            int bytes = 0;
            int index = 0;
            while (index < value.Length)
            {
                char character = value[index];
                char second = '\0';
                int consumedCharacters = 1;
                int scalarBytes;

                if (character == '\r')
                {
                    character = ' ';
                    if (index + 1 < value.Length && value[index + 1] == '\n')
                    {
                        consumedCharacters = 2;
                    }

                    scalarBytes = 1;
                }
                else if (char.IsControl(character))
                {
                    character = ' ';
                    scalarBytes = 1;
                }
                else if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < value.Length &&
                        char.IsLowSurrogate(value[index + 1]))
                    {
                        second = value[index + 1];
                        consumedCharacters = 2;
                        scalarBytes = 4;
                    }
                    else
                    {
                        character = '\ufffd';
                        scalarBytes = 3;
                    }
                }
                else if (char.IsLowSurrogate(character))
                {
                    character = '\ufffd';
                    scalarBytes = 3;
                }
                else if (character <= 0x7f)
                {
                    scalarBytes = 1;
                }
                else if (character <= 0x7ff)
                {
                    scalarBytes = 2;
                }
                else
                {
                    scalarBytes = 3;
                }

                if (bytes + scalarBytes > maximumUtf8Bytes)
                {
                    break;
                }

                bytes += scalarBytes;
                normalized.Append(character);
                if (consumedCharacters == 2 && second != '\0')
                {
                    normalized.Append(second);
                }

                index += consumedCharacters;
            }

            normalizedUtf8Bytes = bytes;
            return normalized.ToString();
        }

        private static string NormalizeWithoutTruncation(string value)
        {
            int ignored;
            return NormalizeAndTruncate(value, int.MaxValue, out ignored);
        }

        internal static bool IsValidIndividualSteam64(string value)
        {
            if (value == null || value.Length != 17)
            {
                return false;
            }

            for (int index = 0; index < value.Length; ++index)
            {
                char character = value[index];
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }

            ulong steamId;
            return ulong.TryParse(
                       value,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out steamId) &&
                   steamId >= MinimumIndividualSteamId64 &&
                   steamId <= MaximumIndividualSteamId64;
        }

        private static string EnsureRegularDirectory(string path, string description)
        {
            string fullPath = NormalizeDirectory(path);
            if (File.Exists(fullPath))
            {
                throw new IOException(description + " is a file.");
            }

            Directory.CreateDirectory(fullPath);
            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(description + " is not a regular directory.");
            }

            return fullPath;
        }

        private static string GetContainedPlayerDirectory(
            string root,
            string playerDirectoryKey)
        {
            if (!IsValidIndividualSteam64(playerDirectoryKey))
            {
                throw new InvalidDataException(
                    "Invalid Steam64 player directory key.");
            }

            string path = Path.GetFullPath(Path.Combine(root, playerDirectoryKey));
            EnsureContained(path, root);
            return path;
        }

        private static string GetContainedFilePath(string root, string fileName)
        {
            string path = Path.GetFullPath(Path.Combine(root, fileName));
            EnsureContained(path, root);
            return path;
        }

        private static void EnsureContained(string path, string root)
        {
            string normalizedRoot = NormalizeDirectory(root);
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!path.StartsWith(prefix, comparison))
            {
                throw new IOException("A generated player log path escaped its root.");
            }
        }

        private static string NormalizeDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            int length = fullPath.Length;
            while (length > root.Length &&
                   (fullPath[length - 1] == Path.DirectorySeparatorChar ||
                    fullPath[length - 1] == Path.AltDirectorySeparatorChar))
            {
                --length;
            }

            return length == fullPath.Length
                ? fullPath
                : fullPath.Substring(0, length);
        }

        private static long GetRegularFileLength(string path)
        {
            EnsureRegularFileIfPresent(path);
            FileInfo file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }

        private static void EnsureRegularFileIfPresent(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A player log path is not a regular file.");
            }
        }

        private static void DeleteRegularFileIfPresent(string path)
        {
            EnsureRegularFileIfPresent(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private void ReportSparsely(
            PlayerTelemetryDiagnosticKind kind,
            string message,
            long occurrenceCount)
        {
            if (occurrenceCount == 1 || occurrenceCount % 100 == 0)
            {
                Report(kind, message, occurrenceCount);
            }
        }

        private void Report(
            PlayerTelemetryDiagnosticKind kind,
            string message,
            long occurrenceCount)
        {
            Action<PlayerTelemetryDiagnostic>? callback = _diagnostics;
            if (callback == null)
            {
                return;
            }

            try
            {
                callback(new PlayerTelemetryDiagnostic(
                    kind,
                    message,
                    occurrenceCount));
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // A diagnostic sink must never stop record capture or escape
                // onto the caller's gameplay path.
            }
        }

        private void ThrowIfDisposed()
        {
            if ((WriterState)_state == WriterState.Disposed)
            {
                throw new ObjectDisposedException(nameof(PlayerTelemetryLogWriter));
            }
        }

        private static bool IsFatal(Exception exception)
        {
            return exception is OutOfMemoryException ||
                   exception is StackOverflowException ||
                   exception is AccessViolationException ||
                   exception is AppDomainUnloadedException ||
                   exception is ThreadAbortException;
        }

        private sealed class QueuedRecord
        {
            internal QueuedRecord(
                string playerDirectoryKey,
                string characterDateFileName,
                string serializedLine,
                int serializedBytes)
            {
                PlayerDirectoryKey = playerDirectoryKey;
                CharacterDateFileName = characterDateFileName;
                SerializedLine = serializedLine;
                SerializedBytes = serializedBytes;
            }

            internal string PlayerDirectoryKey { get; }

            internal string CharacterDateFileName { get; }

            internal string SerializedLine { get; }

            internal int SerializedBytes { get; }
        }

        private sealed class TelemetryFile
        {
            internal TelemetryFile(
                string path,
                DateTime localDate,
                long generation)
            {
                Path = path;
                LocalDate = localDate;
                Generation = generation;
            }

            internal string Path { get; }

            internal DateTime LocalDate { get; }

            internal long Generation { get; }
        }

        private sealed class FrozenOptions
        {
            private FrozenOptions(PlayerTelemetryLogOptions source)
            {
                RootDirectory = NormalizeDirectory(source.RootDirectory);
                MaximumQueuedEvents = source.MaximumQueuedEvents;
                MaximumQueuedBytes = source.MaximumQueuedBytes;
                MaximumRecordBytes = source.MaximumRecordBytes;
                MaximumBatchEvents = source.MaximumBatchEvents;
                MaximumBatchBytes = source.MaximumBatchBytes;
                BatchWindowMilliseconds = checked((int)source.BatchWindow.TotalMilliseconds);
                MaximumFileBytes = source.MaximumFileBytes;
                MaximumFilesPerPlayer = source.MaximumFilesPerPlayer;
                ShutdownDrainTimeout = source.ShutdownDrainTimeout;
                ServerTimeZone = TimeZoneInfo.Local;
            }

            internal string RootDirectory { get; }
            internal int MaximumQueuedEvents { get; }
            internal long MaximumQueuedBytes { get; }
            internal int MaximumRecordBytes { get; }
            internal int MaximumBatchEvents { get; }
            internal int MaximumBatchBytes { get; }
            internal int BatchWindowMilliseconds { get; }
            internal long MaximumFileBytes { get; }
            internal int MaximumFilesPerPlayer { get; }
            internal TimeSpan ShutdownDrainTimeout { get; }
            internal TimeZoneInfo ServerTimeZone { get; }

            internal static FrozenOptions Create(PlayerTelemetryLogOptions source)
            {
                if (string.IsNullOrWhiteSpace(source.RootDirectory))
                {
                    throw new ArgumentException(
                        "A player log root directory is required.",
                        nameof(source));
                }

                if (source.MaximumQueuedEvents < 1 ||
                    source.MaximumQueuedEvents > 1_000_000)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.MaximumQueuedEvents));
                }

                if (source.MaximumQueuedBytes < 1024 ||
                    source.MaximumQueuedBytes > 1024L * 1024L * 1024L)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.MaximumQueuedBytes));
                }

                if (source.MaximumRecordBytes < 256 ||
                    source.MaximumRecordBytes > 4 * 1024 * 1024 ||
                    source.MaximumRecordBytes > source.MaximumQueuedBytes)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.MaximumRecordBytes));
                }

                if (source.MaximumBatchEvents < 1 ||
                    source.MaximumBatchEvents > source.MaximumQueuedEvents)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.MaximumBatchEvents));
                }

                if (source.MaximumBatchBytes < source.MaximumRecordBytes ||
                    source.MaximumBatchBytes > source.MaximumQueuedBytes)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.MaximumBatchBytes));
                }

                if (source.BatchWindow < TimeSpan.Zero ||
                    source.BatchWindow > TimeSpan.FromSeconds(1))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.BatchWindow));
                }

                if (source.MaximumFileBytes < source.MaximumRecordBytes ||
                    source.MaximumFileBytes > 16L * 1024L * 1024L * 1024L)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.MaximumFileBytes));
                }

                if (source.MaximumFilesPerPlayer < 1 ||
                    source.MaximumFilesPerPlayer > 1000)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.MaximumFilesPerPlayer));
                }

                if (source.ShutdownDrainTimeout < TimeSpan.Zero &&
                    source.ShutdownDrainTimeout != Timeout.InfiniteTimeSpan)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.ShutdownDrainTimeout));
                }

                if (source.ShutdownDrainTimeout != Timeout.InfiniteTimeSpan &&
                    source.ShutdownDrainTimeout > TimeSpan.FromMinutes(5))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(source.ShutdownDrainTimeout));
                }

                return new FrozenOptions(source);
            }
        }
    }
}
