using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// Production queue admission, execution, dedicated guards and operation result
// description are inserted by the runner. Game/native/storage boundaries are
// inert; this does not claim to run Unity, Steam, or a native world save worker.
public static class MaintenanceSaveSmoke
{
    private static int _checks;
    private static void Check(bool condition, string reason)
    { ++_checks; if (!condition) throw new Exception(reason); }
    public static void Verify()
    {
        foreach (float timer in new[] { 0f, 30f, 60f, 61f, 120f })
        {
            Reset(); Game.instance.m_saveTimer = timer; ZNet.DistributionPlatform = null;
            ServerManagerCommandResult result = Maintenance();
            Check(result.Success && result.Code == "save_requested" && result.OperationId == "new-save" &&
                ZNet.instance.DirectSaves == 1 && ZNet.instance.LegacySaves == 0 &&
                !ZNet.instance.LastSync && ZNet.instance.LastOtherProfiles && !ZNet.instance.LastWait,
                "Dedicated maintenance bypasses only the fragile RPC entry and preserves save flags.");
            Check(ServerEventRuntime._latestSave.State == ServerManagerSaveState.InProgress,
                "Dispatch never claims worker/checkpoint success.");
        }
        Reset();
        try { ZNet.instance.SaveWorldAndPlayerProfiles(); throw new Exception("Expected legacy null-platform failure."); }
        catch (NullReferenceException) { ++_checks; }
        Reset(); ZNet.DistributionPlatform = new object();
        Check(Ordinary().Success && ZNet.instance.LegacySaves == 1 && ZNet.instance.DirectSaves == 0,
            "Ordinary/API save keeps vanilla RPC semantics.");
        Reset(); ZNet.instance.Dedicated = false; ZNet.DistributionPlatform = new object();
        Check(Maintenance().Success && ZNet.instance.LegacySaves == 1 && ZNet.instance.DirectSaves == 0,
            "Listen-host maintenance keeps vanilla save/coroutine entry.");

        Refusal("save_busy", () => ZNet.instance.Saving = true);
        Refusal("save_blocked", () => ZNet.m_loadError = true);
        Refusal("save_blocked", () => SaveSystem.Blocked = true);
        Refusal("save_blocked", () => ZoneSystem.instance.Blocked = true);
        Refusal("save_blocked", () => DungeonDB.instance.Blocked = true);
        Refusal("server_not_ready", () => ServerEventRuntime._worldReady = false);
        Refusal("server_not_ready", () => ServerEventRuntime._serverStarted = false);
        Refusal("server_not_ready", () => ServerEventRuntime._shutdownStarted = true);
        Refusal("server_not_ready", () => ZNet.instance.enabled = false);
        Refusal("server_not_ready", () => Game.instance = null);
        Refusal("server_not_ready", () => ZoneSystem.instance = null);
        Refusal("server_not_ready", () => DungeonDB.instance = null);
        Refusal("save_storage_unsupported", () => ZNet.World.m_fileSource = FileHelpers.FileSource.Cloud);
        Refusal("save_storage_unknown", () => ZNet.World.Paths.Clear());
        Refusal("save_storage_unknown", () => FileHelpers.WorldBytes = ulong.MaxValue);
        Refusal("save_disk_full", () => FileHelpers.WorldBytes = 1UL << 61);
        Refusal("unauthorized", () => ServerEventRuntime.Authorized = false);
        Refusal("world_changed", () => ZNet.World = new World());
        Refusal("world_changed", () => ServerEventRuntime.CommandWorldEpoch++);
        Refusal("world_changed", () => ZNet.instance = new ZNet());
        Reset();
        using (var cancellation = new CancellationTokenSource())
        {
            Task<ServerManagerCommandResult> queued = ServerEventRuntime.EnqueueMaintenanceSave(ZNet.instance,
                cancellation.Token, () => true);
            cancellation.Cancel(); ServerEventRuntime.Drain();
            Check(queued.Result.Code == "cancelled" && ZNet.instance.DirectSaves == 0, "Queued cancellation precedes all work.");
        }
        Reset();
        FileHelpers.OnInspect = () => ServerEventRuntime.Authorized = false;
        Check(Maintenance().Code == "unauthorized" && ZNet.instance.DirectSaves == 0,
            "Authorization is rechecked after disk inspection.");
        Reset(); ZNet.instance.PublishOperation = false;
        Check(Maintenance().Code == "save_not_started", "Ignored saves cannot become synthetic successes.");
        Reset(); ZNet.instance.OperationFails = true;
        Check(Maintenance().Code == "save_blocked" && ServerEventRuntime._latestSave.OperationId == "new-save",
            "Observed worker refusal retains its own operation ID.");
        Reset(); ZNet.instance.ThrowAfterOperation = true;
        try { Maintenance(); throw new Exception("Expected dispatch exception."); }
        catch (IOException) { ++_checks; }
        Check(ServerEventRuntime._latestSave.OperationId == "new-save" &&
            ServerEventRuntime._latestSave.State == ServerManagerSaveState.Failed,
            "Dispatch failure marks only its newly observed operation failed.");
        Reset(); ZNet.instance.PublishOperation = false; ZNet.instance.ThrowAfterOperation = true;
        try { Maintenance(); throw new Exception("Expected dispatch exception."); }
        catch (IOException) { ++_checks; }
        Check(ServerEventRuntime._latestSave.OperationId == "old-save" &&
            ServerEventRuntime._latestSave.State == ServerManagerSaveState.CheckpointCompleted,
            "Failure before dispatch must not corrupt an unrelated prior success.");
        Console.WriteLine("MaintenanceSaveSmoke: PASS (" + _checks + " source-linked assertions; native Unity/Steam save workers not executed).");
    }
    private static void Reset()
    {
        ZNet.instance = new ZNet(); ZNet.World = new World(); ZNet.m_loadError = false; ZNet.DistributionPlatform = null;
        Game.instance = new Game(); ZoneSystem.instance = new ZoneSystem(); DungeonDB.instance = new DungeonDB();
        SaveSystem.Blocked = false; FileHelpers.WorldBytes = 0; FileHelpers.OnInspect = null;
        ServerEventRuntime._serverStarted = true; ServerEventRuntime._worldReady = true; ServerEventRuntime._shutdownStarted = false;
        ServerEventRuntime._initialized = true; ServerEventRuntime._acceptCommands = true; ServerEventRuntime.Authorized = true;
        ServerEventRuntime._latestSave = new ServerManagerSaveOperationSnapshot { OperationId = "old-save", State = ServerManagerSaveState.CheckpointCompleted };
    }
    private static ServerManagerCommandResult Maintenance()
    {
        Task<ServerManagerCommandResult> task = ServerEventRuntime.EnqueueMaintenanceSave(ZNet.instance,
            CancellationToken.None, () => ServerEventRuntime.Authorized);
        ServerEventRuntime.Drain(); return task.Result;
    }
    private static ServerManagerCommandResult Ordinary()
    {
        Task<ServerManagerCommandResult> task = ServerEventRuntime.EnqueueCommand(ServerEventCommandKind.Save, "", "");
        ServerEventRuntime.Drain(); return task.Result;
    }
    private static void Refusal(string code, Action changeAfterQueue)
    {
        Reset(); ZNet original = ZNet.instance;
        Task<ServerManagerCommandResult> task = ServerEventRuntime.EnqueueMaintenanceSave(original,
            CancellationToken.None, () => ServerEventRuntime.Authorized);
        changeAfterQueue(); ServerEventRuntime.Drain();
        Check(!task.Result.Success && task.Result.Code == code && original.DirectSaves == 0 && original.LegacySaves == 0,
            "Guard " + code + " must refuse before native save.");
    }
}

internal static class ServerEventRuntime
{
    internal static bool _serverStarted, _worldReady, _shutdownStarted, _initialized, _acceptCommands, Authorized;
    internal static long CommandWorldEpoch;
    internal static ServerManagerSaveOperationSnapshot _latestSave;
    private static readonly object CommandGate = new object();
    private static readonly Queue<QueuedCommand> Commands = new Queue<QueuedCommand>();
    private const int MaximumQueuedCommands = 64;
    private static readonly string _serverId = "fixture";
    internal static void Drain() { while (Commands.Count != 0) { var command = Commands.Dequeue(); command.Complete(ExecuteCommand(command)); } }
    private static int MaximumPrimary(ServerEventCommandKind kind) => 1024;
    private static string ValidateCommandText(string text, int maximum, bool allowEmpty) => text;
    private static ServerManagerCommandResult Failure(string code, string message, string id = "", Dictionary<string, string> data = null) =>
        new ServerManagerCommandResult { Success = false, Code = code, OperationId = id };
    private static ServerManagerCommandResult Success(string code, string message, string id = "", Dictionary<string, string> data = null) =>
        new ServerManagerCommandResult { Success = true, Code = code, OperationId = id };
    private static Dictionary<string, string> Fields(params string[] values) => new Dictionary<string, string>();
    private static void Publish(params object[] arguments) { }
    private static ServerManagerCommandResult ExecuteModeration(ZNet server, QueuedCommand command, Func<string, bool> kick) => Failure("not_used", "");
    private static void OnWorldSaveCheckpointFailed(string id, Exception error)
    { if (_latestSave.OperationId == id) _latestSave.State = ServerManagerSaveState.Failed; }
    // SOURCE_LINKED_QUEUE
    // SOURCE_LINKED_EXECUTION
    // SOURCE_LINKED_QUEUED_COMMAND
}
internal enum ServerEventCommandKind { Save = 1, Announce, Kick, Ban, Unban }
internal enum ServerManagerSaveState { None, InProgress, WorldDiskCompleted, Failed, CheckpointCompleted }
internal sealed class ServerManagerSaveOperationSnapshot { internal string OperationId = "", Error = ""; internal ServerManagerSaveState State; }
internal sealed class ServerManagerCommandResult { internal bool Success; internal string Code = "", OperationId = ""; }
internal sealed class ServerManagerActor { internal ServerManagerActor(string id, string name, string kind) { } }
internal static class ServerManagerEventKinds { internal const string ServerAnnouncement = "announcement"; }
internal static class ServerManagerEventReliability { internal const string Authoritative = "authoritative"; }
internal static class IntegrityCanonical { internal static bool IsFatal(Exception exception) => exception is OutOfMemoryException; }
internal static class ServerManagerPlugin { internal static readonly Logger Log = new Logger(); }
internal sealed class Logger { internal void LogWarning(string value) { } }
internal sealed class ZNet
{
    internal static ZNet instance; internal static World World; internal static bool m_loadError; internal static object DistributionPlatform;
    internal bool Dedicated = true, enabled = true, Saving, PublishOperation = true, OperationFails, ThrowAfterOperation;
    internal int DirectSaves, LegacySaves; internal bool LastSync, LastOtherProfiles, LastWait;
    internal bool IsServer() => true; internal bool IsDedicated() => Dedicated; internal bool IsSaving() => Saving;
    internal static World GetWorldIfIsHost() => World;
    internal void SaveWorldAndPlayerProfiles()
    {
        ++LegacySaves;
        // Faithful null point shown by original HardSaveBlock IL, not a claim
        // that this stub executes the native game or its Harmony patch chain.
        if (Game.instance.m_saveTimer <= 60f) _ = DistributionPlatform.ToString();
        Observe();
    }
    internal void Save(bool sync, bool saveOtherPlayerProfiles, bool waitForNextFrame)
    { ++DirectSaves; LastSync = sync; LastOtherProfiles = saveOtherPlayerProfiles; LastWait = waitForNextFrame; Observe(); }
    private void Observe()
    {
        if (PublishOperation) ServerEventRuntime._latestSave = new ServerManagerSaveOperationSnapshot
        { OperationId = "new-save", State = OperationFails ? ServerManagerSaveState.Failed : ServerManagerSaveState.InProgress };
        if (ThrowAfterOperation) throw new IOException("fixture");
    }
}
internal sealed class World
{
    internal FileHelpers.FileSource m_fileSource = FileHelpers.FileSource.Local;
    internal List<string> Paths = new List<string> { Path.GetTempPath() };
    internal List<string> GetSavePaths() => Paths;
}
internal static class FileHelpers
{
    internal enum FileSource { Auto = 1, Local = 2, Cloud = 4, Legacy = 8 }
    internal static ulong WorldBytes; internal static Action OnInspect;
    internal static ulong GetFileSizes(List<string> paths, FileSource source) { OnInspect?.Invoke(); return WorldBytes; }
}
internal sealed class Game { internal static Game instance; internal float m_saveTimer; }
internal sealed class ZoneSystem { internal static ZoneSystem instance; internal bool Blocked; internal bool SkipSaving() => Blocked; }
internal sealed class DungeonDB { internal static DungeonDB instance; internal bool Blocked; internal bool SkipSaving() => Blocked; }
internal enum SaveSystemSessionFlags { DontSaveWorld }
internal static class SaveSystem { internal static bool Blocked; internal static bool HasSessionFlag(SaveSystemSessionFlags flag) => Blocked; }
