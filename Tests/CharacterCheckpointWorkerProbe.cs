using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Executes the compiled two-phase implementation against temporary .fch files.
// The real repository account lock deterministically stalls disk work without
// introducing test hooks into production or loading a running game.
public static class CharacterCheckpointWorkerProbe
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Verify(object service, object identity, string key,
        object durable, object semantic)
    {
        Type serviceType = service.GetType();
        Assembly assembly = serviceType.Assembly;
        semantic = Create(semantic.GetType(), true, 25f, 50f, 0f,
            Array.CreateInstance(assembly.GetType("ServerManager.CharacterSemanticSkillState", true), 0),
            Array.CreateInstance(assembly.GetType("ServerManager.CharacterSemanticItemState", true), 0),
            new string[0]);
        object repository = Field(service, "_repository");
        object layout = Field(repository, "_layout");
        string path = (string)Invoke(layout, "GetProfilePath", key);
        object accountLock = repository.GetType().GetMethod("GetAccountLock", StaticFlags)
            .Invoke(null, new object[] { Get(identity, "AccountId") });
        Type liveType = assembly.GetType("ServerManager.CharacterSnapshotService+CharacterLiveSnapshot", true);
        object target = Next(durable, identity);
        object newer = Next(target, identity);
        object live = Create(liveType, identity, durable, target, semantic, 101L);
        Invoke(service, "AddLiveSnapshotLocked", key, live);
        object batch = Invoke(service, "BeginCheckpoint");
        object entry = First(Get(batch, "Entries"));
        object handle = Get(batch, "Handle");
        VerifyAdmissionGates(service, identity, key, live, durable, target,
            semantic, liveType, handle, entry);
        object write = null;
        Monitor.Enter(accountLock);
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            write = Invoke(service, "BeginCheckpointCommit", handle, entry);
            Require(timer.ElapsedMilliseconds < 2000, "Preparing a checkpoint waited for repository I/O.");
            Thread.Sleep(50);
            Require(!(bool)Get(write, "IsCompleted"), "The account-lock barrier did not stall the worker.");
            object[] completionArguments = { write, null };
            Require(!(bool)InvokeArgs(service, "TryCompleteCheckpointCommit", completionArguments),
                "Polling an unfinished disk operation claimed completion.");
            ExpectStorageFailure(() => Invoke(service, "BeginCheckpointCommit", handle, entry));
            ExpectStorageFailure(() => Invoke(service, "GetAdminCharacters"));
            VerifySettingsRemainUnchanged(service, repository);
            // Both frozen-generation creation and newer accepted RAM replacement
            // remain available while the file worker is blocked.
            timer.Restart();
            object concurrent = Invoke(service, "BeginCheckpoint");
            Invoke(service, "DiscardCheckpoint", concurrent);
            Invoke(service, "SetLiveSnapshotLocked", key,
                Create(liveType, identity, durable, newer, semantic, 101L));
            Require(timer.ElapsedMilliseconds < 2000, "A RAM checkpoint/admission waited for the file worker.");
            // Established reconnect uses the retained latest shadow, not disk.
            timer.Restart();
            object opened = Invoke(service, "OpenOrCreateLocalHostSession", identity);
            Require((long)Get(Get(opened, "Snapshot"), "Revision") == (long)Get(newer, "Revision"),
                "Reconnect did not prefer the newest RAM shadow during a disk write.");
            object localSession = Field(service, "_localHostSession");
            Invoke(service, "CloseLocalHostSession", Get(localSession, "SessionId"));
            Require(timer.ElapsedMilliseconds < 2000, "Retained-shadow reconnect waited for repository I/O.");
        }
        finally { Monitor.Exit(accountLock); if (write != null) Wait(write); }
        Complete(service, write);
        IDictionary liveDictionary = (IDictionary)Field(service, "_liveSnapshots");
        object after = liveDictionary[key];
        Require((long)Get(Get(after, "DurableEnvelope"), "Revision") == (long)Get(target, "Revision") &&
            (long)Get(Get(after, "LatestEnvelope"), "Revision") == (long)Get(newer, "Revision"),
            "Worker completion overwrote a post-cutoff RAM revision.");

        // A tampered disk base must fail without freeing the exact retry entry.
        byte[] goodDisk = File.ReadAllBytes(path);
        byte[] badDisk = (byte[])goodDisk.Clone();
        badDisk[badDisk.Length - 1] ^= 0x01;
        File.WriteAllBytes(path, badDisk);
        object retryBatch = Invoke(service, "BeginCheckpoint");
        object retryEntry = First(Get(retryBatch, "Entries"));
        object retryHandle = Get(retryBatch, "Handle");
        object failed = Invoke(service, "BeginCheckpointCommit", retryHandle, retryEntry);
        Wait(failed);
        ExpectStorageFailure(() => InvokeArgs(service, "TryCompleteCheckpointCommit", new object[] { failed, null }));
        Require(!(bool)Get(service, "HasPendingCheckpointWrite"), "A failed worker leaked its single-write reservation.");
        Require((long)Get(Get(liveDictionary[key], "DurableEnvelope"), "Revision") == (long)Get(target, "Revision"),
            "A failed write advanced RAM durability.");
        File.WriteAllBytes(path, goodDisk);
        Complete(service, Invoke(service, "BeginCheckpointCommit", retryHandle, retryEntry));
        Require(!liveDictionary.Contains(key), "An offline, caught-up shadow survived successful retry.");

        // Disposing does not wait for a blocked write or release its process
        // writer lease early. No completion may mutate the retired service.
        object finalTarget = Next(newer, identity);
        Invoke(service, "AddLiveSnapshotLocked", key,
            Create(liveType, identity, newer, finalTarget, semantic, 101L));
        object finalBatch = Invoke(service, "BeginCheckpoint");
        object finalWrite = null;
        object provider = Field(service, "_storageKeyProvider");
        Monitor.Enter(accountLock);
        try
        {
            finalWrite = Invoke(service, "BeginCheckpointCommit", Get(finalBatch, "Handle"), First(Get(finalBatch, "Entries")));
            Stopwatch timer = Stopwatch.StartNew();
            ((IDisposable)service).Dispose();
            Require(timer.ElapsedMilliseconds < 2000, "Dispose waited for a stalled disk operation.");
            Require(!(bool)Field(provider, "_disposed"), "Dispose released the directory writer lease before I/O stopped.");
            ExpectStorageFailure(() => Create(provider.GetType(), layout));
        }
        finally { Monitor.Exit(accountLock); if (finalWrite != null) Wait(finalWrite); }
        Wait(finalWrite);
        Stopwatch deadline = Stopwatch.StartNew();
        while (!(bool)Field(provider, "_disposed") && deadline.ElapsedMilliseconds < 10000) Thread.Sleep(1);
        Require((bool)Field(provider, "_disposed"), "A retired write leaked its directory writer lease after completion.");
        Require(liveDictionary.Count == 0 && (long)Field(service, "_retainedSnapshotPayloadBytes") == 0,
            "A late worker resurrected disposed RAM state.");
        using (IDisposable reopened = (IDisposable)Create(provider.GetType(), layout)) { }
        bool disposedFailure = false;
        try { InvokeArgs(service, "TryCompleteCheckpointCommit", new object[] { finalWrite, null }); }
        catch (TargetInvocationException ex) { disposedFailure = Root(ex) is ObjectDisposedException; }
        Require(disposedFailure, "A stale completion was accepted after disposal.");
    }

    private static void VerifyAdmissionGates(object service, object identity, string key,
        object originalLive, object durable, object target, object semantic,
        Type liveType, object handle, object entry)
    {
        Invoke(service, "OpenOrCreateLocalHostSession", identity);
        object session = Field(service, "_localHostSession");
        FieldInfo pending = session.GetType().GetField("_pendingInitialEnvelope", Flags);
        object captures = Field(service, "_preparedBackupCaptures");
        Guid captureId = Guid.NewGuid();
        bool captureAdded = false;
        try
        {
            Require((bool)Get(service, "CanStartCheckpointWrite"), "An established session unnecessarily blocks checkpoint dispatch.");
            pending.SetValue(session, target);
            Require(!(bool)Get(service, "CanStartCheckpointWrite"), "PendingInitialCommit did not defer checkpoint dispatch.");
            ExpectStorageFailure(() => Invoke(service, "BeginCheckpointCommit", handle, entry));
            pending.SetValue(session, null);

            object fresh = target.GetType().GetMethod("CreateWithOrigin", StaticFlags).Invoke(null, new object[] {
                Get(target, "Kind"), Get(target, "Revision"), Get(target, "BaseRevision"),
                Get(target, "SessionId"), identity, DateTime.UtcNow, Get(target, "ValheimProfileVersion"),
                Invoke(target, "GetPayloadCopy"), true });
            Invoke(service, "SetLiveSnapshotLocked", key,
                Create(liveType, identity, durable, fresh, semantic, 101L));
            Require(!(bool)Get(service, "CanStartCheckpointWrite"), "An active fresh profile did not reserve its first-full promotion boundary.");
            ExpectStorageFailure(() => Invoke(service, "BeginCheckpointCommit", handle, entry));
            Invoke(service, "SetLiveSnapshotLocked", key, originalLive);

            Type captureType = service.GetType().GetNestedType("BackupCapturePreparation", BindingFlags.NonPublic);
            object preparation = Create(captureType, key, originalLive, target, null);
            Require((bool)Invoke(captures, "TryAdd", captureId, preparation), "Could not establish the backup preparation fixture.");
            captureAdded = true;
            Require(!(bool)Get(service, "CanStartCheckpointWrite"), "A prepared backup capture did not defer checkpoint dispatch.");
            ExpectStorageFailure(() => Invoke(service, "BeginCheckpointCommit", handle, entry));
        }
        finally
        {
            pending.SetValue(session, null);
            Invoke(service, "SetLiveSnapshotLocked", key, originalLive);
            if (captureAdded)
                captures.GetType().GetMethod("TryRemove", Flags, null, new Type[] {
                    typeof(Guid), captures.GetType().GetGenericArguments()[1].MakeByRefType() }, null)
                    .Invoke(captures, new object[] { captureId, null });
            Invoke(service, "CloseLocalHostSession", Get(session, "SessionId"));
        }
        Require((bool)Get(service, "CanStartCheckpointWrite"), "Completed admission gates did not release checkpoint dispatch.");
        Require(!(bool)Get(service, "HasPendingCheckpointWrite"), "A denied admission-gate attempt dispatched a file worker.");
    }

    private static void VerifySettingsRemainUnchanged(object service, object repository)
    {
        object oldSettings = Field(service, "_serverSettings");
        object options = Field(service, "_options");
        object oldMaximum = Get(options, "MaxCharactersPerAccount");
        object oldBackups = Get(options, "MaxBackups");
        object storedValidator = Field(service, "_semanticValidator");
        object incomingValidator = Field(repository, "_revisionValidator");
        object oldStoredEvaluator = Field(storedValidator, "_evaluator");
        object oldIncomingEvaluator = Field(incomingValidator, "_evaluator");
        int newMaximum = Convert.ToInt32(oldMaximum) == 7 ? 8 : 7;
        int newBackups = Convert.ToInt32(oldBackups) == 4 ? 5 : 4;
        object newSettings = oldSettings.GetType().GetMethod("Parse", StaticFlags).Invoke(null,
            new object[] { "serverSettings:\n  maxCharactersPerAccount: " + newMaximum +
                "\n  backupsPerProfile: " + newBackups + "\n" });
        Require(!oldMaximum.Equals(Get(newSettings, "MaxCharactersPerAccount")) &&
            !oldBackups.Equals(Get(newSettings, "BackupsPerProfile")),
            "The settings reload fixture did not change both storage limits.");
        ExpectStorageFailure(() => Invoke(service, "ApplyServerSettings", newSettings));
        Require(ReferenceEquals(oldSettings, Field(service, "_serverSettings")) &&
            oldMaximum.Equals(Get(options, "MaxCharactersPerAccount")) && oldBackups.Equals(Get(options, "MaxBackups")) &&
            ReferenceEquals(oldStoredEvaluator, Field(storedValidator, "_evaluator")) &&
            ReferenceEquals(oldIncomingEvaluator, Field(incomingValidator, "_evaluator")),
            "A settings reload partially changed policy while checkpoint storage was busy.");
    }

    private static void Complete(object service, object write)
    {
        Wait(write);
        Require((bool)InvokeArgs(service, "TryCompleteCheckpointCommit", new object[] { write, null }),
            "A completed worker could not be adopted.");
    }
    private static void Wait(object write)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!(bool)Get(write, "IsCompleted") && timer.ElapsedMilliseconds < 10000) Thread.Sleep(1);
        Require((bool)Get(write, "IsCompleted"), "The isolated checkpoint worker timed out.");
    }
    private static object Next(object previous, object identity)
    {
        byte[] bytes = (byte[])Invoke(previous, "GetPayloadCopy");
        bytes[bytes.Length - 1] ^= 0x01;
        return previous.GetType().GetMethod("Create", StaticFlags).Invoke(null, new object[] {
            Get(previous, "Kind"), (long)Get(previous, "Revision") + 1, Get(previous, "Revision"),
            Guid.NewGuid(), identity, DateTime.UtcNow, Get(previous, "ValheimProfileVersion"), bytes });
    }
    private static object First(object entries) { foreach (object entry in (IEnumerable)entries) return entry; throw new Exception("Empty checkpoint."); }
    private static object Get(object target, string name) { return target.GetType().GetProperty(name, Flags).GetValue(target, null); }
    private static object Field(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
    private static object Invoke(object target, string name, params object[] args) { return InvokeArgs(target, name, args); }
    private static object InvokeArgs(object target, string name, object[] args) { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
    private static object Create(Type type, params object[] args)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors(Flags))
            if (constructor.GetParameters().Length == args.Length) return constructor.Invoke(args);
        throw new Exception("Missing constructor: " + type.FullName);
    }
    private static Exception Root(Exception ex) { while (ex.InnerException != null) ex = ex.InnerException; return ex; }
    private static void ExpectStorageFailure(Action action)
    {
        try { action(); }
        catch (TargetInvocationException ex)
        {
            for (Exception cause = ex; cause != null; cause = cause.InnerException)
                if (cause.GetType().Name == "CharacterStorageException") return;
            throw;
        }
        throw new Exception("Expected fail-fast CharacterStorageException.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
