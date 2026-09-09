using System;
using System.Collections.Generic;

namespace ServerManager;

internal enum ClientCharacterSaveReason
{
    Vanilla = 1,
    InventoryDirty = 2,
    GracefulExit = 3,
    PeriodicFull = 4
}

internal sealed class ClientCharacterSaveDispatch
{
    internal ClientCharacterSaveDispatch(
        ulong captureId,
        byte[] payloadBytes,
        long baseRevision,
        long revision,
        ClientCharacterSaveReason reason)
    {
        CaptureId = captureId;
        PayloadBytes = payloadBytes ??
            throw new ArgumentNullException(nameof(payloadBytes));
        BaseRevision = baseRevision;
        Revision = revision;
        Reason = reason;
    }

    internal ulong CaptureId { get; }

    internal byte[] PayloadBytes { get; }

    internal long BaseRevision { get; }

    internal long Revision { get; }

    internal ClientCharacterSaveReason Reason { get; }
}

/// <summary>
/// Keeps at most one character save on the wire and one latest pending
/// update. Pending updates receive a revision only after the previous update
/// has been accepted into the server's live overlay. Full-profile and
/// inventory-only requests therefore share one ordered revision stream.
/// </summary>
internal sealed class ClientCharacterSavePipeline
{
    internal const int MaximumStartsPerWindow = 13;
    internal const int MaximumRoutineStartsPerWindow = 12;
    internal const int MaximumRoutineFullStartsPerWindow = 1;

    private readonly long _pacingWindowTicks;
    private readonly long _acknowledgementTimeoutTicks;
    private readonly Queue<long> _recentStartTimestamps = new();
    private readonly Queue<long> _recentFullStartTimestamps = new();

    private PendingSave? _pending;
    private InFlightSave? _inFlight;
    private ulong _nextCaptureId = 1;
    private ulong _lastAcknowledgedCaptureId;
    private bool _closed;

    internal ClientCharacterSavePipeline(
        long pacingWindowTicks,
        long acknowledgementTimeoutTicks)
    {
        if (pacingWindowTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pacingWindowTicks));
        }

        if (acknowledgementTimeoutTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acknowledgementTimeoutTicks));
        }

        _pacingWindowTicks = pacingWindowTicks;
        _acknowledgementTimeoutTicks = acknowledgementTimeoutTicks;
    }

    internal bool Closed => _closed;

    internal bool HasInFlight => _inFlight != null;

    internal bool HasPending => _pending != null;

    internal bool HasPendingFullProfile =>
        _pending != null && IsFullProfileReason(_pending.Reason);

    internal ulong InFlightCaptureId => _inFlight?.CaptureId ?? 0;

    internal ulong PendingCaptureId => _pending?.CaptureId ?? 0;

    internal ulong Offer(
        byte[] payloadBytes,
        ClientCharacterSaveReason reason)
    {
        if (_closed)
        {
            throw new InvalidOperationException(
                "The client character save pipeline is closed.");
        }

        if (payloadBytes == null)
        {
            throw new ArgumentNullException(nameof(payloadBytes));
        }

        if (payloadBytes.Length == 0)
        {
            throw new ArgumentException(
                "A character save payload may not be empty.",
                nameof(payloadBytes));
        }

        if (!Enum.IsDefined(typeof(ClientCharacterSaveReason), reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (_nextCaptureId == 0)
        {
            throw new InvalidOperationException(
                "The client character capture sequence is exhausted.");
        }

        // A smaller inventory update must never evict a pending full profile.
        // The runtime leaves inventory dirty in this case and coalesces it
        // behind the full update after that update starts.
        if (reason == ClientCharacterSaveReason.InventoryDirty &&
            _pending != null &&
            IsFullProfileReason(_pending.Reason))
        {
            return _pending.CaptureId;
        }

        ulong captureId = _nextCaptureId;
        _nextCaptureId = captureId == ulong.MaxValue
            ? 0
            : captureId + 1;
        // The caller's capture buffer may be reused or mutated after Offer.
        // Own one immutable copy until the matching live-overlay ACK arrives.
        _pending = new PendingSave(
            captureId,
            (byte[])payloadBytes.Clone(),
            reason);
        return captureId;
    }

    internal bool TryStartNext(
        long acknowledgedRevision,
        long nowTimestamp,
        out ClientCharacterSaveDispatch? dispatch)
    {
        dispatch = null;
        if (_closed || _inFlight != null || _pending == null)
        {
            return false;
        }

        if (acknowledgedRevision < 0 || acknowledgedRevision == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The acknowledged character revision cannot be incremented.");
        }

        PrunePacingWindow(nowTimestamp);
        bool graceful = _pending.Reason ==
                        ClientCharacterSaveReason.GracefulExit;
        int startLimit = graceful
            ? MaximumStartsPerWindow
            : MaximumRoutineStartsPerWindow;
        if (_recentStartTimestamps.Count >= startLimit)
        {
            return false;
        }

        if (!graceful &&
            IsFullProfileReason(_pending.Reason) &&
            _recentFullStartTimestamps.Count >=
                MaximumRoutineFullStartsPerWindow)
        {
            return false;
        }

        PendingSave pending = _pending;
        _pending = null;
        long revision = checked(acknowledgedRevision + 1);
        long acknowledgementDeadline = AddDuration(
            nowTimestamp,
            _acknowledgementTimeoutTicks);
        _inFlight = new InFlightSave(
            pending.CaptureId,
            pending.PayloadBytes,
            acknowledgedRevision,
            revision,
            acknowledgementDeadline,
            pending.Reason);
        _recentStartTimestamps.Enqueue(nowTimestamp);
        if (IsFullProfileReason(pending.Reason))
        {
            _recentFullStartTimestamps.Enqueue(nowTimestamp);
        }

        dispatch = new ClientCharacterSaveDispatch(
            pending.CaptureId,
            pending.PayloadBytes,
            acknowledgedRevision,
            revision,
            pending.Reason);
        return true;
    }

    internal bool TryCompleteAcknowledgement(
        long baseRevision,
        long revision,
        out byte[] acknowledgedPayloadBytes,
        out ClientCharacterSaveReason acknowledgedReason,
        out string error)
    {
        acknowledgedPayloadBytes = Array.Empty<byte>();
        acknowledgedReason = default;
        if (_closed)
        {
            error = "The client character save pipeline is closed.";
            return false;
        }

        InFlightSave? inFlight = _inFlight;
        if (inFlight == null)
        {
            error = "No character save is awaiting acknowledgement.";
            return false;
        }

        if (baseRevision != inFlight.BaseRevision)
        {
            error = "The character save acknowledgement base revision is invalid.";
            return false;
        }

        if (revision != inFlight.Revision ||
            revision != checked(baseRevision + 1))
        {
            error = "The character save acknowledgement revision is invalid.";
            return false;
        }

        acknowledgedPayloadBytes = inFlight.PayloadBytes;
        acknowledgedReason = inFlight.Reason;
        _lastAcknowledgedCaptureId = inFlight.CaptureId;
        _inFlight = null;
        error = string.Empty;
        return true;
    }

    internal bool IsAcknowledgementOverdue(long nowTimestamp)
    {
        if (_closed || _inFlight == null)
        {
            return false;
        }

        return nowTimestamp > _inFlight.AcknowledgementDeadlineTimestamp;
    }

    internal bool IsDrainedThrough(ulong captureId)
    {
        return !_closed &&
               captureId != 0 &&
               _lastAcknowledgedCaptureId >= captureId &&
               _inFlight == null &&
               _pending == null;
    }

    // Administrative actions need acknowledgement of their full capture, not
    // an otherwise-idle inventory stream. Later captures may still be queued.
    internal bool HasAcknowledgedCapture(ulong captureId) =>
        !_closed && captureId != 0 && _lastAcknowledgedCaptureId >= captureId;

    internal void Close()
    {
        _closed = true;
        _pending = null;
        _inFlight = null;
        _recentStartTimestamps.Clear();
        _recentFullStartTimestamps.Clear();
    }

    private void PrunePacingWindow(long nowTimestamp)
    {
        if (nowTimestamp < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nowTimestamp));
        }

        while (_recentStartTimestamps.Count > 0)
        {
            long oldest = _recentStartTimestamps.Peek();
            if (nowTimestamp < oldest)
            {
                throw new InvalidOperationException(
                    "The monotonic save clock moved backwards.");
            }

            // The server removes starts only when start < now - window.
            // Keep the exact boundary locally as well to avoid a rejection.
            if (nowTimestamp - oldest <= _pacingWindowTicks)
            {
                break;
            }

            _recentStartTimestamps.Dequeue();
        }

        while (_recentFullStartTimestamps.Count > 0)
        {
            long oldest = _recentFullStartTimestamps.Peek();
            if (nowTimestamp < oldest)
            {
                throw new InvalidOperationException(
                    "The monotonic full-save clock moved backwards.");
            }

            if (nowTimestamp - oldest <= _pacingWindowTicks)
            {
                break;
            }

            _recentFullStartTimestamps.Dequeue();
        }
    }

    internal static bool IsFullProfileReason(
        ClientCharacterSaveReason reason)
    {
        return reason != ClientCharacterSaveReason.InventoryDirty;
    }

    private static long AddDuration(long timestamp, long duration)
    {
        if (timestamp < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        if (duration <= 0 || timestamp > long.MaxValue - duration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return timestamp + duration;
    }

    private sealed class PendingSave
    {
        internal PendingSave(
            ulong captureId,
            byte[] payloadBytes,
            ClientCharacterSaveReason reason)
        {
            CaptureId = captureId;
            PayloadBytes = payloadBytes;
            Reason = reason;
        }

        internal ulong CaptureId { get; }

        internal byte[] PayloadBytes { get; }

        internal ClientCharacterSaveReason Reason { get; }

    }

    private sealed class InFlightSave
    {
        internal InFlightSave(
            ulong captureId,
            byte[] payloadBytes,
            long baseRevision,
            long revision,
            long acknowledgementDeadlineTimestamp,
            ClientCharacterSaveReason reason)
        {
            CaptureId = captureId;
            PayloadBytes = payloadBytes ??
                throw new ArgumentNullException(nameof(payloadBytes));
            BaseRevision = baseRevision;
            Revision = revision;
            AcknowledgementDeadlineTimestamp =
                acknowledgementDeadlineTimestamp;
            Reason = reason;
        }

        internal ulong CaptureId { get; }

        internal byte[] PayloadBytes { get; }

        internal long BaseRevision { get; }

        internal long Revision { get; }

        internal long AcknowledgementDeadlineTimestamp { get; }

        internal ClientCharacterSaveReason Reason { get; }
    }
}
