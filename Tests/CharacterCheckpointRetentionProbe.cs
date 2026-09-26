using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

// No game execution or publicized assembly is needed. Reflection lets the
// existing isolated checkpoint smoke fixture exercise the actual compiled types.
public static class CharacterCheckpointRetentionProbe
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance |
        BindingFlags.Public | BindingFlags.NonPublic;

    private sealed class RetainedFailure
    {
        internal object Pending;
        internal WeakReference CompletedPayload;
        internal WeakReference CompletedEnvelope;
        internal WeakReference FailedPayload;
        internal WeakReference Batch;
    }

    public static void Verify(object service, object identityA, object identityB,
        string keyA, string keyB, object durableA, object mismatchedDurableB,
        object semantic)
    {
        RetainedFailure retained = CreateMixedCheckpoint(service, identityA,
            identityB, keyA, keyB, durableA, mismatchedDurableB, semantic);
        Collect();
        Require(!retained.Batch.IsAlive,
            "A failed per-character retry still retains the aggregate batch.");
        Require(!retained.CompletedPayload.IsAlive &&
            !retained.CompletedEnvelope.IsAlive,
            "A failed peer in the same cutoff pins an already completed payload.");
        Require(retained.FailedPayload.IsAlive,
            "The failed character lost its exact retry payload.");
        object handle = Get(retained.Pending, "Checkpoint");
        Require(handle.GetType().Name == "CharacterCheckpointHandle",
            "A pending character must hold the payload-free handle, not a batch.");
        ReleaseFailed(service, keyB, retained);
        Collect();
        Require(!retained.FailedPayload.IsAlive,
            "Discarding the final retry and live shadow leaked its payload.");
        Require((long)service.GetType().GetField("_retainedSnapshotPayloadBytes",
            InstanceFlags).GetValue(service) == 0,
            "The mixed-checkpoint regression fixture leaked byte accounting.");
        GC.KeepAlive(service);
        GC.KeepAlive(handle);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RetainedFailure CreateMixedCheckpoint(object service,
        object identityA, object identityB, string keyA, string keyB,
        object durableA, object mismatchedDurableB, object semantic)
    {
        Assembly assembly = service.GetType().Assembly;
        Type liveType = assembly.GetType(
            "ServerManager.CharacterSnapshotService+CharacterLiveSnapshot", true);
        object snapshotA = NewSnapshot(durableA, identityA);
        object snapshotB = NewSnapshot(mismatchedDurableB, identityB);
        Invoke(service, "AddLiveSnapshotLocked", keyA,
            Create(liveType, identityA, durableA, snapshotA, semantic, 101L));
        Invoke(service, "AddLiveSnapshotLocked", keyB,
            Create(liveType, identityB, mismatchedDurableB, snapshotB, semantic, 102L));
        object batch = Invoke(service, "BeginCheckpoint");
        object entryA = null;
        object entryB = null;
        foreach (object entry in (IEnumerable)Get(batch, "Entries"))
        {
            if ((string)Get(entry, "StorageKey") == keyA) entryA = entry;
            if ((string)Get(entry, "StorageKey") == keyB) entryB = entry;
        }
        Require(entryA != null && entryB != null && (int)Get(batch, "Count") == 2,
            "The retention fixture did not create the expected mixed cutoff.");
        Type pendingType = assembly.GetType(
            "ServerManager.ServerManagerRuntime+PendingCharacterCheckpointEntry", true);
        object pendingB = Create(pendingType, "retention-smoke", service, batch, entryB);
        object handle = Get(pendingB, "Checkpoint");
        Require(Get(handle, "OwnerId").Equals(Get(batch, "OwnerId")) &&
            Get(handle, "CheckpointId").Equals(Get(batch, "CheckpointId")),
            "The payload-free handle does not identify its captured batch.");
        bool failed = false;
        try
        {
            Invoke(service, "CommitCheckpointEntry", handle, entryB);
        }
        catch (TargetInvocationException exception)
        {
            Exception cause = exception;
            while (cause.InnerException != null) cause = cause.InnerException;
            if (cause.GetType().Name != "CharacterStorageException") throw;
            failed = true;
        }
        Require(failed, "The deliberately mismatched character did not fail persistence.");
        Invoke(service, "CommitCheckpointEntry", Get(batch, "Handle"), entryA);
        return new RetainedFailure
        {
            Pending = pendingB,
            CompletedPayload = new WeakReference(Get(snapshotA, "PayloadUnsafe")),
            CompletedEnvelope = new WeakReference(snapshotA),
            FailedPayload = new WeakReference(Get(snapshotB, "PayloadUnsafe")),
            Batch = new WeakReference(batch)
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReleaseFailed(object service, string key,
        RetainedFailure retained)
    {
        object pending = retained.Pending;
        Invoke(service, "DiscardCheckpointEntry", Get(pending, "Checkpoint"),
            Get(pending, "Entry"));
        Invoke(service, "RemoveLiveSnapshotLocked", key);
        retained.Pending = null;
    }

    private static object NewSnapshot(object durable, object identity)
    {
        Type envelopeType = durable.GetType();
        byte[] payload = (byte[])Invoke(durable, "GetPayloadCopy");
        return envelopeType.GetMethod("Create", BindingFlags.Static |
            BindingFlags.Public).Invoke(null, new object[]
            {
                Get(durable, "Kind"), (long)Get(durable, "Revision") + 1,
                Get(durable, "Revision"), Guid.NewGuid(), identity, DateTime.UtcNow,
                Get(durable, "ValheimProfileVersion"), payload
            });
    }

    private static object Create(Type type, params object[] arguments)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors(InstanceFlags))
            if (constructor.GetParameters().Length == arguments.Length)
                return constructor.Invoke(arguments);
        throw new InvalidOperationException("Missing constructor for " + type.FullName);
    }

    private static object Get(object target, string name)
    {
        return target.GetType().GetProperty(name, InstanceFlags).GetValue(target, null);
    }

    private static object Invoke(object target, string name, params object[] arguments)
    {
        return target.GetType().GetMethod(name, InstanceFlags).Invoke(target, arguments);
    }

    private static void Collect()
    {
        for (int attempt = 0; attempt != 3; ++attempt)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
