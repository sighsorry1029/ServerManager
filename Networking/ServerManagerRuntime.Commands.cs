using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using HarmonyLib;
using ServerManager.Commands;
using ServerManager.Events;
using Steamworks;
using UnityEngine;

namespace ServerManager;

// The existing peer/session authentication and save pipeline remain authoritative.
// No caller-supplied ID grants permission and no administrative operation writes a .fch.
internal static partial class ServerManagerRuntime
{
    internal const string AdminCommandRpcName = "sighsorry.ServerManager.Commands.v1";
    private static readonly Dictionary<ZRpc, AdminChannel> AdminChannels = new();
    private static readonly List<AdminReply> AdminReplies = new();
    private static readonly Dictionary<Guid, PendingAdminAction> AdminActions = new();
    private static readonly Dictionary<Guid, Terminal> LocalCommandOutputs = new();
    private static readonly Dictionary<Guid, long> LocalCommandDeadlines = new();
    private static readonly List<(Terminal Terminal, Task<ServerManagerCommandResult> Task)> ConsoleCommands = new();
    private static ClientAdminAction? _clientAdminAction;
    private static HostAdminAction? _hostAdminAction;
    private static long _adminEpoch;
    private const int AdminActionSeconds = 20;

    private sealed class AdminChannel
    {
        internal uint Sent;
        internal uint Received;
        internal long Window;
        internal int Requests;
    }
    private sealed class AdminReply
    {
        internal ZRpc Rpc = null!;
        internal Guid Id;
        internal byte[] Session = Array.Empty<byte>();
        internal Task<ServerManagerCommandResult> Task = null!;
    }
    private sealed class PendingAdminAction
    {
        internal ZRpc Rpc = null!;
        internal Guid Id;
        internal byte[] Session = Array.Empty<byte>();
        internal long Baseline;
        internal bool ReadOnly;
        internal long Deadline;
        internal CommandCaller Caller = null!;
        internal TaskCompletionSource<ServerManagerCommandResult> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ClientAdminAction
    {
        internal ClientConnection Client = null!;
        internal Player Player = null!;
        internal PlayerProfile Profile = null!;
        internal CharacterClientState Character = null!;
        internal Guid Id;
        internal ulong Capture;
        internal bool AwaitTeleport;
        internal int AppliedFrame;
        internal long Deadline;
        internal ServerManagerCommandResult Result = null!;
    }
    private sealed class HostAdminAction
    {
        internal Player Player = null!;
        internal ZNet Network = null!;
        internal CommandCaller Caller = null!;
        internal ServerManagerCommandResult Result = null!;
        internal bool AwaitTeleport;
        internal int AppliedFrame;
        internal long Deadline;
        internal TaskCompletionSource<ServerManagerCommandResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsAdminSession(ZRpc rpc, out ConnectionSessionSnapshot session,
        out ServerPeerIdentity identity, bool requireAdmin)
    {
        session = null!;
        identity = null!;
        ZNet? server = ZNet.instance;
        return _initialized && !_shuttingDown && server != null && server.IsServer() &&
            _coordinator.TryGetSnapshot(rpc, out session) && session.State == ConnectionSessionState.Ready &&
            session.PeerInfoAuthenticated && !PendingDisconnects.ContainsKey(rpc) &&
            !ServerFinalSaveDrains.ContainsKey(rpc) &&
            (!ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState state) ||
             !state.TerminalActionApplied && state.PendingKickTimestamp == 0) &&
            TryResolveActiveDetectionPeer(server, rpc, out identity, out _) &&
            (!requireAdmin || IsCurrentServerAdmin(server, identity));
    }

    private static AdminChannel GetAdminChannel(ZRpc rpc)
    {
        if (!AdminChannels.TryGetValue(rpc, out AdminChannel? channel))
        {
            channel = new AdminChannel();
            AdminChannels.Add(rpc, channel);
        }
        return channel;
    }

    private static bool SendAdminPacket(ZRpc rpc, AdminPacket packet)
    {
        ZNet? network = ZNet.instance;
        if (network == null) return false;
        if (network.IsServer())
        {
            if (!IsAdminSession(rpc, out ConnectionSessionSnapshot session, out _, false)) return false;
            packet.SessionId = session.SessionId;
            packet.Nonce = session.Nonce;
        }
        else
        {
            ClientConnection? client = _client;
            if (client == null || client.Failed || !client.ReadyAcknowledgementSent ||
                !ReferenceEquals(client.Rpc, rpc) || client.SessionId == null || client.Nonce == null) return false;
            packet.SessionId = client.SessionId;
            packet.Nonce = client.Nonce;
        }
        AdminChannel channel = GetAdminChannel(rpc);
        if (channel.Sent >= uint.MaxValue - 1) return false;
        packet.Sequence = channel.Sent + 1;
        ZPackage package = AdminCommandCodec.Encode(packet);
        if (!RawProtocolRpcTransport.Send(rpc, AdminCommandRpcName, package, AdminCommandCodec.MaximumPacketBytes)) return false;
        channel.Sent = packet.Sequence;
        return true;
    }

    private static void OnAdminCommandPacket(ZRpc rpc, ZPackage package)
    {
        if (!_initialized || _shuttingDown) return;
        if (!AdminCommandCodec.TryDecode(package, out AdminPacket packet))
        {
            OnRawTransportError(rpc, new ProtocolRejection(ProtocolRejectCode.InvalidTransition, "Malformed administrative packet."));
            return;
        }
        ZNet? network = ZNet.instance;
        if (network == null) return;
        byte[] sessionId;
        byte[] nonce;
        ServerPeerIdentity? identity = null;
        if (network.IsServer())
        {
            if (!IsAdminSession(rpc, out ConnectionSessionSnapshot session, out identity, false)) return;
            sessionId = session.SessionId;
            nonce = session.Nonce;
            if (packet.Kind != AdminPacketKind.Request && packet.Kind != AdminPacketKind.PlayerResult)
            {
                OnRawTransportError(rpc, new ProtocolRejection(ProtocolRejectCode.InvalidTransition, "Clients cannot issue server player actions."));
                return;
            }
        }
        else
        {
            ClientConnection? client = _client;
            if (client == null || client.Failed || !client.ReadyAcknowledgementSent ||
                !ReferenceEquals(client.Rpc, rpc) || client.SessionId == null || client.Nonce == null) return;
            sessionId = client.SessionId;
            nonce = client.Nonce;
            if (packet.Kind != AdminPacketKind.Result && packet.Kind != AdminPacketKind.PlayerAction &&
                packet.Kind != AdminPacketKind.Shout)
            {
                FailClient(rpc, "Invalid server administrative packet direction.");
                return;
            }
        }
        AdminChannel channel = GetAdminChannel(rpc);
        if (!ProtocolByteUtil.FixedTimeEquals(sessionId, packet.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(nonce, packet.Nonce) || packet.Sequence != channel.Received + 1)
        {
            OnRawTransportError(rpc, new ProtocolRejection(ProtocolRejectCode.InvalidTransition, "Stale or replayed administrative packet."));
            return;
        }
        channel.Received = packet.Sequence;
        switch (packet.Kind)
        {
            case AdminPacketKind.Request:
                if (!IsCurrentServerAdmin(network, identity!))
                {
                    SendAdminResult(rpc, packet.RequestId, AdminPacketKind.Result,
                        ServerCommands.Failure("unauthorized", "Server admin permission is required."));
                    return;
                }
                long now = Stopwatch.GetTimestamp();
                if (now - channel.Window >= 10 * Stopwatch.Frequency) { channel.Window = now; channel.Requests = 0; }
                if (++channel.Requests > 4 || AdminReplies.Count >= 64 || AdminReplies.Count(item => ReferenceEquals(item.Rpc, rpc)) >= 2)
                {
                    SendAdminResult(rpc, packet.RequestId, AdminPacketKind.Result, ServerCommands.Failure("busy", "Too many pending commands."));
                    return;
                }
                byte[] expectedSession = (byte[])sessionId.Clone();
                CommandCaller caller = new("ingame", identity!.HostId, identity.PlayerName,
                    () => IsAdminSession(rpc, out ConnectionSessionSnapshot current, out _, true) &&
                        ProtocolByteUtil.FixedTimeEquals(expectedSession, current.SessionId), CancellationToken.None);
                AdminReplies.Add(new AdminReply { Rpc = rpc, Id = packet.RequestId, Session = expectedSession,
                    Task = ServerCommands.ExecuteAsync(packet.Arguments[0], caller) });
                break;
            case AdminPacketKind.Result:
                if (LocalCommandOutputs.TryGetValue(packet.RequestId, out Terminal? terminal))
                {
                    LocalCommandOutputs.Remove(packet.RequestId);
                    LocalCommandDeadlines.Remove(packet.RequestId);
                    PrintAdminResult(terminal, ParseAdminResult(packet));
                }
                break;
            case AdminPacketKind.PlayerAction:
                ReceivePlayerAdminAction(packet);
                break;
            case AdminPacketKind.PlayerResult:
                CompletePlayerAdminAction(rpc, packet);
                break;
            case AdminPacketKind.Shout:
                ShowServerShout(packet.Arguments[0], packet.Arguments[1]);
                break;
        }
    }

    private static void SendAdminResult(ZRpc rpc, Guid id, AdminPacketKind kind,
        ServerManagerCommandResult result, long revision = 0)
    {
        string[] body = { result.Success ? "1" : "0", result.Code,
            result.Message.Length <= 1800 ? result.Message : result.Message.Substring(0, 1775) + "\n[output truncated]" };
        if (kind == AdminPacketKind.Result) body = body.Concat(new[] { result.OperationId }).ToArray();
        SendAdminPacket(rpc, new AdminPacket { Kind = kind, RequestId = id, Revision = Math.Max(0, revision),
            Arguments = body });
    }
    private static ServerManagerCommandResult ParseAdminResult(AdminPacket packet) =>
        new(packet.Arguments[0] == "1", packet.Arguments[1], packet.Arguments[2],
            packet.Kind == AdminPacketKind.Result ? packet.Arguments[3] : string.Empty, null!);

    internal static void SubmitTerminalCommand(Terminal terminal, string line)
    {
        if (terminal == null || !_initialized || _shuttingDown || ZNet.instance == null) return;
        if (ZNet.instance.IsServer())
        {
            if (ConsoleCommands.Count >= 16) { terminal.AddString("ServerManager command queue is full."); return; }
            ZNet network = ZNet.instance;
            CommandCaller caller = new("console", "local", "Server console",
                () => ReferenceEquals(network, ZNet.instance) && network.IsServer() && !_shuttingDown,
                CancellationToken.None);
            ConsoleCommands.Add((terminal, ServerCommands.ExecuteAsync(line, caller)));
        }
        else
        {
            ClientConnection? client = _client;
            if (client == null || client.Failed || !client.ReadyAcknowledgementSent || _deferredClientExit != null)
            { terminal.AddString("ServerManager: no ready server connection."); return; }
            if (line.Length > 2048 || LocalCommandOutputs.Count >= 2)
            { terminal.AddString("ServerManager: command is too long or another request is pending."); return; }
            Guid id = Guid.NewGuid();
            LocalCommandOutputs.Add(id, terminal);
            LocalCommandDeadlines.Add(id, Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency);
            if (!SendAdminPacket(client.Rpc, new AdminPacket { Kind = AdminPacketKind.Request, RequestId = id, Arguments = new[] { line } }))
            {
                LocalCommandOutputs.Remove(id);
                LocalCommandDeadlines.Remove(id);
                terminal.AddString("ServerManager: command could not be sent.");
            }
        }
    }

    private static void PrintAdminResult(Terminal terminal, ServerManagerCommandResult result)
    {
        if (terminal != null) terminal.AddString("ServerManager [" + result.Code + "] " + result.Message +
            (result.OperationId.Length == 0 ? "" : "\nOperation: " + result.OperationId));
    }

    private static void TickAdminCommands()
    {
        long now = Stopwatch.GetTimestamp();
        long epoch = ServerEventRuntime.CommandWorldEpoch;
        if (_adminEpoch != epoch)
        {
            _adminEpoch = epoch;
            foreach (PendingAdminAction action in AdminActions.Values)
                action.Completion.TrySetResult(ServerCommands.Failure("world_changed", "The server world changed; no action was retried."));
            AdminActions.Clear();
        }
        ServerCommands.Tick();
        for (int index = ConsoleCommands.Count - 1; index >= 0; --index)
        {
            var pending = ConsoleCommands[index];
            if (!pending.Task.IsCompleted) continue;
            ConsoleCommands.RemoveAt(index);
            PrintAdminResult(pending.Terminal, CompletedAdminTask(pending.Task));
        }
        for (int index = AdminReplies.Count - 1; index >= 0; --index)
        {
            AdminReply reply = AdminReplies[index];
            if (!reply.Task.IsCompleted) continue;
            AdminReplies.RemoveAt(index);
            if (IsAdminSession(reply.Rpc, out ConnectionSessionSnapshot session, out _, false) &&
                ProtocolByteUtil.FixedTimeEquals(reply.Session, session.SessionId))
                SendAdminResult(reply.Rpc, reply.Id, AdminPacketKind.Result, CompletedAdminTask(reply.Task));
        }
        foreach (PendingAdminAction action in AdminActions.Values.ToArray())
        {
            if (now <= action.Deadline && !action.Caller.Cancellation.IsCancellationRequested && action.Caller.IsAuthorized()) continue;
            AdminActions.Remove(action.Id);
            action.Completion.TrySetResult(ServerCommands.Failure("action_unconfirmed",
                "The action was dispatched but completion is unconfirmed. Inspect the target before retrying; disk save was not claimed."));
        }
        foreach (var pair in LocalCommandDeadlines.ToArray())
        {
            if (now <= pair.Value) continue;
            LocalCommandDeadlines.Remove(pair.Key);
            if (LocalCommandOutputs.TryGetValue(pair.Key, out Terminal? output))
            {
                LocalCommandOutputs.Remove(pair.Key);
                output.AddString("ServerManager: command result timed out. Do not blindly repeat a mutation.");
            }
        }
        TickClientAdminAction(now);
        TickHostAdminAction(now);
    }

    private static ServerManagerCommandResult CompletedAdminTask(Task<ServerManagerCommandResult> task) =>
        task.Status == TaskStatus.RanToCompletion ? task.Result : ServerCommands.Failure("command_failed", "Command completion failed.");

    private static void RemoveAdminPeer(ZRpc rpc)
    {
        AdminChannels.Remove(rpc);
        AdminReplies.RemoveAll(reply => ReferenceEquals(reply.Rpc, rpc));
        foreach (PendingAdminAction action in AdminActions.Values.Where(item => ReferenceEquals(item.Rpc, rpc)).ToArray())
        {
            AdminActions.Remove(action.Id);
            action.Completion.TrySetResult(ServerCommands.Failure("target_disconnected", "Target disconnected; action was not retried."));
        }
        if (_clientAdminAction != null && ReferenceEquals(_clientAdminAction.Client.Rpc, rpc)) _clientAdminAction = null;
        if (_client != null && ReferenceEquals(_client.Rpc, rpc))
        {
            foreach (Terminal output in LocalCommandOutputs.Values) output?.AddString("ServerManager: disconnected before command completion.");
            LocalCommandOutputs.Clear();
            LocalCommandDeadlines.Clear();
        }
    }

    private static void ShutdownAdminCommands()
    {
        ServerCommands.Shutdown();
        foreach (PendingAdminAction action in AdminActions.Values)
            action.Completion.TrySetResult(ServerCommands.Failure("shutdown", "The server command session ended."));
        AdminActions.Clear();
        AdminReplies.Clear();
        AdminChannels.Clear();
        ConsoleCommands.Clear();
        LocalCommandOutputs.Clear();
        LocalCommandDeadlines.Clear();
        _clientAdminAction = null;
        if (_hostAdminAction != null)
            _hostAdminAction.Completion.TrySetResult(ServerCommands.Failure("shutdown", "Host action completion was not confirmed."));
        _hostAdminAction = null;
    }

    private static void ReceivePlayerAdminAction(AdminPacket packet)
    {
        ClientConnection client = _client!;
        if (_clientAdminAction != null || _deferredClientExit != null || _clientGameplayQuiescence != null ||
            !client.ServerCharacterActive || client.CharacterState == null || client.ManagedProfile == null ||
            Player.m_localPlayer == null || client.SavePipeline.Closed)
        {
            SendAdminResult(client.Rpc, packet.RequestId, AdminPacketKind.PlayerResult,
                ServerCommands.Failure("target_busy", "The player cannot accept an administrative action now."));
            return;
        }
        try
        {
            ServerManagerCommandResult applied = CharacterAdminActions.Apply(Player.m_localPlayer, packet.Arguments);
            if (!applied.Success || IsReadOnlyPlayerAction(packet.Arguments))
            {
                SendAdminResult(client.Rpc, packet.RequestId, AdminPacketKind.PlayerResult, applied, client.CharacterState.Revision);
                return;
            }
            _clientAdminAction = new ClientAdminAction { Client = client, Id = packet.RequestId, Result = applied,
                Player = Player.m_localPlayer, Profile = client.ManagedProfile, Character = client.CharacterState,
                AwaitTeleport = packet.Arguments[0] == "teleport", AppliedFrame = Time.frameCount,
                Deadline = Stopwatch.GetTimestamp() + AdminActionSeconds * Stopwatch.Frequency };
            TickClientAdminAction(Stopwatch.GetTimestamp());
        }
        catch (Exception error) when (!IntegrityCanonical.IsFatal(error))
        {
            SendAdminResult(client.Rpc, packet.RequestId, AdminPacketKind.PlayerResult,
                ServerCommands.Failure("action_unconfirmed", "Action/capture failed; inspect target before retrying."));
        }
    }

    private static bool IsReadOnlyPlayerAction(string[] args) => args.Length >= 2 && args[0] == "skill" && args[1] == "get";

    private static void TickClientAdminAction(long now)
    {
        ClientAdminAction? action = _clientAdminAction;
        if (action == null) return;
        ClientConnection client = action.Client;
        if (!ReferenceEquals(client, _client) || client.Failed || _deferredClientExit != null ||
            !ReferenceEquals(Player.m_localPlayer, action.Player) || !ReferenceEquals(client.ManagedProfile, action.Profile) ||
            !ReferenceEquals(client.CharacterState, action.Character) || now > action.Deadline || client.SavePipeline.Closed)
        {
            _clientAdminAction = null;
            SendAdminResult(client.Rpc, action.Id, AdminPacketKind.PlayerResult,
                ServerCommands.Failure("action_unconfirmed", "Action may have applied, but its RAM snapshot was not confirmed. Do not automatically retry."));
            return;
        }
        try
        {
            if (Time.frameCount <= action.AppliedFrame) return;
            if (action.AwaitTeleport && Player.m_localPlayer.IsTeleporting()) return;
            if (action.Capture == 0)
            {
                if (!client.InventoryOverlapRetry.CanAttempt(now)) return;
                EnsureCharacterCodecs();
                client.ManagedProfile!.SaveLogoutPoint();
                byte[] profile;
                try
                {
                    profile = _profileCodec!.CaptureProfileToBytes(client.ManagedProfile!, Player.m_localPlayer);
                }
                catch (CharacterProtocolException error) when (error.IsInventoryOverlap)
                {
                    // The action already ran. Retry capture only, within both
                    // the shared overlap grace and this action's own deadline.
                    if (!TryDeferClientInventoryOverlap(client, error)) throw;
                    return;
                }
                action.Capture = OfferClientSave(client, profile, ClientCharacterSaveReason.Vanilla);
            }
            if (!client.SavePipeline.HasAcknowledgedCapture(action.Capture)) return;
            _clientAdminAction = null;
            SendAdminResult(client.Rpc, action.Id, AdminPacketKind.PlayerResult,
                ServerCommands.Success("ram_accepted", action.Result.Message + " Latest character snapshot accepted in server RAM; disk persistence awaits world checkpoint."),
                client.CharacterState!.Revision);
        }
        catch (Exception error) when (!IntegrityCanonical.IsFatal(error))
        {
            _clientAdminAction = null;
            SendAdminResult(client.Rpc, action.Id, AdminPacketKind.PlayerResult,
                ServerCommands.Failure("action_unconfirmed", "Action may have applied but capture failed; inspect target before retrying."));
        }
    }

    private static void TickHostAdminAction(long now)
    {
        HostAdminAction? action = _hostAdminAction;
        if (action == null) return;
        if (now > action.Deadline || !ReferenceEquals(ZNet.instance, action.Network) ||
            !ReferenceEquals(Player.m_localPlayer, action.Player) || !LocalHostCharacterRuntime.IsActive ||
            action.Caller.Cancellation.IsCancellationRequested || !action.Caller.IsAuthorized())
        {
            _hostAdminAction = null;
            action.Completion.TrySetResult(ServerCommands.Failure("action_unconfirmed", "Host action may have applied but completion was not confirmed; inspect before retrying."));
            return;
        }
        if (Time.frameCount <= action.AppliedFrame || action.AwaitTeleport && action.Player.IsTeleporting()) return;
        _hostAdminAction = null;
        bool accepted = LocalHostCharacterRuntime.CaptureForWorldCheckpoint();
        action.Completion.TrySetResult(accepted
            ? ServerCommands.Success("ram_accepted", action.Result.Message + " Host RAM accepted; disk persistence awaits world checkpoint.")
            : ServerCommands.Failure("ram_unconfirmed", "Host action applied but RAM capture failed; do not automatically repeat."));
    }

    private static void CompletePlayerAdminAction(ZRpc rpc, AdminPacket packet)
    {
        if (!AdminActions.TryGetValue(packet.RequestId, out PendingAdminAction? pending) || !ReferenceEquals(rpc, pending.Rpc) ||
            !ProtocolByteUtil.FixedTimeEquals(packet.SessionId, pending.Session)) return;
        AdminActions.Remove(packet.RequestId);
        if (Stopwatch.GetTimestamp() > pending.Deadline || pending.Caller.Cancellation.IsCancellationRequested ||
            !pending.Caller.IsAuthorized() || !IsAdminSession(rpc, out ConnectionSessionSnapshot active, out _, false) ||
            !ProtocolByteUtil.FixedTimeEquals(active.SessionId, pending.Session))
        {
            pending.Completion.TrySetResult(ServerCommands.Failure("action_unconfirmed", "Action result arrived after its authorization/session expired; no retry was attempted."));
            return;
        }
        ServerManagerCommandResult result = ParseAdminResult(packet);
        if (result.Success && !pending.ReadOnly &&
            (_serverCharacterService == null || !_serverCharacterService.TryGetServerSession(rpc, out CharacterSession? session) ||
             session == null || packet.Revision <= pending.Baseline || session.CurrentRevision < packet.Revision))
            result = ServerCommands.Failure("ram_unconfirmed", "Client reported application but the server has not accepted the corresponding revision.");
        pending.Completion.TrySetResult(result);
    }

    internal static Task<ServerManagerCommandResult> ExecuteManagedAdminCommandAsync(string[] args, CommandCaller caller)
    {
        Task<ServerManagerCommandResult> Done(ServerManagerCommandResult result) => Task.FromResult(result);
        if (caller.Cancellation.IsCancellationRequested || !caller.IsAuthorized() || !_initialized || _shuttingDown)
            return Done(ServerCommands.Failure("unauthorized", "Command authorization expired."));
        if (args[0] == "chat") return Done(ExecuteAdminShout(args, caller));
        if (args[0] == "discord") return Done(Discord.DiscordRuntime.ExecuteAdminOperation(args[1]));
        if (args[0] == "mods")
        {
            if (args.Length == 2 && args[1] == "reload")
            {
                EnsureIntegrityService().Reload();
                return Done(ServerCommands.Success("reload_requested", "Reference DLL rescan scheduled. Existing valid policy is retained if scanning fails; watch audit/logs for the outcome."));
            }
            string mods = string.Join("\n", Chainloader.PluginInfos.Values.OrderBy(item => item.Metadata.GUID)
                .Take(60).Select(item => item.Metadata.GUID + " " + item.Metadata.Version));
            return Done(ServerCommands.Success("mods", "Loaded SERVER plugins (not client attestations):\n" + mods));
        }
        if (args[0] == "character") return Done(QueryAdminCharacters(args));
        int targetIndex = args[0] == "item" || args[0] == "skill" ? 2 : 1;
        string target = args[targetIndex];
        if (!TryFindAdminTarget(target, out ZRpc? targetRpc, out Player? host, out string error))
        {
            if (args[0] == "skill" && error == "target_not_found") return Done(ExecuteOfflineAdminSkill(args));
            return Done(ServerCommands.Failure(error, "No unique ready online target. Use Steam64/name or an exact unique name/ID."));
        }
        if (args[0] == "players")
        {
            Vector3 pos = host != null ? host.transform.position : GetAdminPeerPosition(targetRpc!);
            return Done(ServerCommands.Success("player_info", target + " online at " + FormatAdminPosition(pos)));
        }
        string[] action;
        if (args[0] == "teleport")
        {
            if (args[2] == "to")
            {
                if (!TryFindAdminTarget(args[3], out ZRpc? destinationRpc, out Player? destinationHost, out error))
                    return Done(ServerCommands.Failure(error, "Destination player is not unique/ready."));
                Vector3 position = destinationHost != null ? destinationHost.transform.position :
                    GetAdminPeerPosition(destinationRpc!);
                action = new[] { "teleport", position.x.ToString("R", CultureInfo.InvariantCulture),
                    position.y.ToString("R", CultureInfo.InvariantCulture), position.z.ToString("R", CultureInfo.InvariantCulture) };
            }
            else action = new[] { "teleport", args[2], args[3], args[4] };
        }
        else if (args[0] == "item")
        {
            action = new[] { "item", "give", args[3], args[4], args.Length >= 6 ? args[5] : "1" };
            if (args.Length == 7)
            {
                if (!EnsureItemDataPresetsReady() || _itemDataPresets?.Current == null)
                    return Done(ServerCommands.Failure("itemdata_not_ready", "Itemdata.yml has not loaded successfully yet; no items were added."));
                if (!_itemDataPresets.Current.TryGetPayload(args[6], out string payload))
                    return Done(ServerCommands.Failure("itemdata_not_found", "Unknown Itemdata.yml preset ID; no items were added."));
                // Resolve once on the authorized server. Clients receive only the
                // bounded data snapshot, never read a local YAML or resolve an ID.
                action = action.Concat(new[] { payload }).ToArray();
            }
        }
        else if (args[0] == "skill") action = new[] { "skill", args[1] }.Concat(args.Skip(3)).ToArray();
        else action = new[] { args[0], args[2] };
        bool targetPolicyAdmin = host != null
            ? IsCharacterPolicyAdmin(new CharacterIdentity(CharacterSteamIdentity.AccountPrefix +
                SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture), host.GetPlayerName()))
            : _serverCharacterService != null && _serverCharacterService.TryGetServerSession(targetRpc!, out CharacterSession? policySession) &&
                policySession != null && IsCharacterPolicyAdmin(policySession.Identity);
        if (action[0] == "item" && (_incomingSaveSemanticPolicy == null ||
            _incomingSaveSemanticPolicy.IsForbiddenItemPrefab(action[2]) && !targetPolicyAdmin))
            return Done(ServerCommands.Failure("forbidden_item", "The target character's forbidden-item policy rejects this prefab."));
        if (host != null)
        {
            if (!LocalHostCharacterRuntime.IsActive || _hostAdminAction != null) return Done(ServerCommands.Failure("target_busy", "Managed host character is not ready or an action is pending."));
            ServerManagerCommandResult result = CharacterAdminActions.Apply(host, action);
            if (result.Success && !IsReadOnlyPlayerAction(action))
            {
                _hostAdminAction = new HostAdminAction { Player = host, Network = ZNet.instance, Caller = caller,
                    Result = result, AppliedFrame = Time.frameCount, AwaitTeleport = action[0] == "teleport",
                    Deadline = Stopwatch.GetTimestamp() + AdminActionSeconds * Stopwatch.Frequency };
                return _hostAdminAction.Completion.Task;
            }
            return Done(result);
        }
        if (!IsAdminSession(targetRpc!, out ConnectionSessionSnapshot connection, out _, false) ||
            _serverCharacterService == null || !_serverCharacterService.TryGetServerSession(targetRpc!, out CharacterSession? character) || character == null)
            return Done(ServerCommands.Failure("target_busy", "Target character is not ready."));
        if (AdminActions.Count >= 32 || AdminActions.Values.Any(value => ReferenceEquals(value.Rpc, targetRpc)))
            return Done(ServerCommands.Failure("target_busy", "Another administrative action is awaiting this player's acknowledgement."));
        PendingAdminAction pending = new() { Rpc = targetRpc!, Id = Guid.NewGuid(), Session = connection.SessionId,
            Baseline = character.CurrentRevision, Caller = caller, ReadOnly = IsReadOnlyPlayerAction(action),
            Deadline = Stopwatch.GetTimestamp() + AdminActionSeconds * Stopwatch.Frequency };
        AdminActions.Add(pending.Id, pending);
        if (!SendAdminPacket(targetRpc!, new AdminPacket { Kind = AdminPacketKind.PlayerAction, RequestId = pending.Id, Arguments = action }))
        {
            AdminActions.Remove(pending.Id);
            pending.Completion.TrySetResult(ServerCommands.Failure("send_failed", "Action was not sent."));
        }
        return pending.Completion.Task;
    }

    private static bool TryFindAdminTarget(string selector, out ZRpc? rpc, out Player? host, out string error)
    {
        rpc = null; host = null; error = "target_not_found";
        int found = 0;
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer.m_rpc == null || !IsAdminSession(peer.m_rpc, out _, out ServerPeerIdentity identity, false)) continue;
            long playerId = 0;
            if (_serverCharacterService?.TryGetServerSession(peer.m_rpc, out CharacterSession? session) == true && session != null)
                playerId = session.PlayerId;
            if (!MatchesAdminTarget(selector, identity.HostId, identity.PlayerName, playerId)) continue;
            rpc = peer.m_rpc; ++found;
        }
        Player? local = Player.m_localPlayer;
        if (!ZNet.instance.IsDedicated() && LocalHostCharacterRuntime.IsActive && local != null &&
            MatchesAdminTarget(selector, SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture), local.GetPlayerName(), local.GetPlayerID()))
        { host = local; ++found; }
        if (found != 1) { rpc = null; host = null; error = found > 1 ? "ambiguous_target" : "target_not_found"; return false; }
        return true;
    }

    private static bool MatchesAdminTarget(string selector, string account, string name, long playerId)
    {
        string steam = CharacterSteamIdentity.TryParseCanonicalAccountId(account, out ulong steamId)
            ? steamId.ToString(CultureInfo.InvariantCulture)
            : account.StartsWith("Steam_", StringComparison.Ordinal) ? account.Substring(6) : account;
        int separator = selector.IndexOf('/');
        if (separator >= 0)
        {
            string accountSelector = selector.Substring(0, separator);
            return TryAdminSteamSelector(accountSelector, out string selectedSteam) && selectedSteam == steam &&
                string.Equals(selector.Substring(separator + 1), name, StringComparison.OrdinalIgnoreCase);
        }
        if (selector.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) ||
            selector.StartsWith(CharacterSteamIdentity.AccountPrefix, StringComparison.OrdinalIgnoreCase) ||
            selector.Length == 17 && selector.All(char.IsDigit))
            return TryAdminSteamSelector(selector, out string selectedSteam) && selectedSteam == steam;
        if (long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out long selectedPlayer))
            return playerId != 0 && selectedPlayer == playerId;
        return string.Equals(selector, name, StringComparison.OrdinalIgnoreCase);
    }
    private static bool TryAdminSteamSelector(string value, out string steam)
    {
        steam = "";
        string bare = value.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) ? value.Substring(6) : value;
        if (!bare.StartsWith(CharacterSteamIdentity.AccountPrefix, StringComparison.Ordinal)) bare = CharacterSteamIdentity.AccountPrefix + bare;
        if (!CharacterSteamIdentity.TryParseCanonicalAccountId(bare, out ulong parsed)) return false;
        steam = parsed.ToString(CultureInfo.InvariantCulture);
        return true;
    }
    private static Vector3 GetAdminPeerPosition(ZRpc rpc)
    {
        if (!IsAdminSession(rpc, out _, out ServerPeerIdentity identity, false))
            throw new InvalidOperationException("The target session ended.");
        return identity.Peer.m_refPos;
    }
    private static string FormatAdminPosition(Vector3 position) => string.Format(CultureInfo.InvariantCulture,
        "{0:0}, {1:0}, {2:0}", position.x, position.y, position.z);

    private static ServerManagerCommandResult QueryAdminCharacters(string[] args)
    {
        if (_serverCharacterService == null) return ServerCommands.Failure("unavailable", "Character service is unavailable.");
        CharacterAdminRecord[] records = _serverCharacterService.GetAdminCharacters();
        if (args[1] != "list") records = records.Where(record => MatchesAdminTarget(args[2], record.AccountId, record.CharacterName, record.PlayerId)).Take(2).ToArray();
        if (args[1] != "list" && records.Length != 1)
            return ServerCommands.Failure(records.Length > 1 ? "ambiguous_target" : "target_not_found", "Use a unique Steam64/name character selector.");
        if (args[1] == "backups")
            return _serverCharacterService.GetAdminBackups(records[0].AccountId, records[0].CharacterName,
                args.Length == 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1);
        if (args[1] == "restore")
            return _serverCharacterService.RestoreAdminBackup(records[0].AccountId, records[0].CharacterName, args[3]);
        return ServerCommands.Success("characters", string.Join("\n", records.Take(30).Select(record =>
            record.AccountId + "/" + record.CharacterName + " player=" + record.PlayerId + " " + (record.IsOnline ? "online" : "offline") +
            " RAM=" + record.Revision + " disk=" + record.DurableRevision)) + (records.Length > 30 ? "\n[first 30 shown]" : ""));
    }

    private static ServerManagerCommandResult ExecuteOfflineAdminSkill(string[] args)
    {
        if (_serverCharacterService == null) return ServerCommands.Failure("unavailable", "Character service unavailable.");
        CharacterAdminRecord[] matches = _serverCharacterService.GetAdminCharacters().Where(record =>
            MatchesAdminTarget(args[2], record.AccountId, record.CharacterName, record.PlayerId)).Take(2).ToArray();
        if (matches.Length != 1) return ServerCommands.Failure(matches.Length > 1 ? "ambiguous_target" : "target_not_found", "Use Steam64/name for an exact offline character.");
        float value = 0;
        if (args.Length > 4 && !float.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return ServerCommands.Failure("invalid_argument", "Invalid skill value.");
        return _serverCharacterService.ApplyOfflineSkillAdmin(matches[0].AccountId, matches[0].CharacterName,
            args[1], args.Length > 3 ? args[3] : "all", value);
    }

    private static ServerManagerCommandResult ExecuteAdminShout(string[] args, CommandCaller caller)
    {
        // Only the in-game label is shared. The command audit keeps caller.Name/Id.
        string title = caller.Source == "discord" ? "[Discord] Admin" : "Server";
        string message = string.Join(" ", args.Skip(1));
        if (!TryBroadcastServerShout(title, message))
            return ServerCommands.Failure("send_failed", "Shout could not be broadcast. A ready server and 1-500 characters of valid text are required.");
        if (caller.Source == "discord")
            ServerEventRuntime.RecordDiscordShout(caller.Id, caller.Name, message);
        return ServerCommands.Success("message_sent", "Global shout broadcast accepted; individual client delivery is not acknowledged.");
    }
    // Literal, server-side delivery only. Public Discord chat must never enter
    // the administration parser or receive an administrative CommandCaller.
    internal static bool TryBroadcastDiscordShout(string userId, string userName, string message, bool adminChannel)
    {
        if (userId == null || userId.Length == 0 || userId.Length > 20 || userId[0] == '0' ||
            !userId.All(c => c >= '0' && c <= '9') ||
            !ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            string.IsNullOrWhiteSpace(userName) || userName.Length > 80 || userName.Any(char.IsControl))
            return false;
        if (!TryBroadcastServerShout(adminChannel ? "[Discord] Admin" : "[Discord] " + userName, message)) return false;
        // Do not replace the real author with the display-only Admin alias in logs.
        ServerEventRuntime.RecordDiscordShout(userId, userName, message);
        return true;
    }

    // Both command chat and ordinary Discord text share one global-only sink.
    // There is no target selector, normal-chat style, or reply state here.
    private static bool TryBroadcastServerShout(string title, string message)
    {
        if (!_initialized || _shuttingDown || ZNet.instance == null || !ZNet.instance.IsServer() ||
            string.IsNullOrWhiteSpace(title) || title.Length > 100 || title.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(message) || message.Length > 500 || message.Any(char.IsControl))
            return false;
        ServerManagerStatusSnapshot status = ServerEventRuntime.GetStatusSnapshot();
        if (!status.IsServer || !status.ServerStarted || !status.WorldReady || status.ShutdownStarted) return false;
        // Encode before any recipient has seen the message. Invalid UTF-16 must
        // not leave a half-sent broadcast or disturb another peer's sequence.
        try { _ = System.Text.Encoding.GetEncoding("UTF-8", System.Text.EncoderFallback.ExceptionFallback,
            System.Text.DecoderFallback.ExceptionFallback).GetBytes(title + message); }
        catch (System.Text.EncoderFallbackException) { return false; }
        foreach (ZNetPeer peer in ZNet.instance.GetPeers().ToArray())
        {
            if (peer.m_rpc == null || !IsAdminSession(peer.m_rpc, out _, out _, false)) continue;
            try
            {
                SendAdminPacket(peer.m_rpc, new AdminPacket { Kind = AdminPacketKind.Shout,
                    RequestId = Guid.NewGuid(), Arguments = new[] { title, message } });
            }
            catch (Exception error) when (!IntegrityCanonical.IsFatal(error))
            {
                // Best-effort display; one disconnect must not stop other peers.
                // Never retry an uncertain send or log the message/token.
                ServerManagerPlugin.Log.LogWarning("Server shout delivery failed for a peer (" + error.GetType().Name + ").");
            }
        }
        if (!ZNet.instance.IsDedicated())
        {
            try { ShowServerShout(title, message); }
            catch (Exception error) when (!IntegrityCanonical.IsFatal(error))
            { ServerManagerPlugin.Log.LogWarning("Server shout host display failed (" + error.GetType().Name + ")."); }
        }
        return true; // Accepted broadcast, not a display/delivery ACK from every client.
    }

    private static void ShowServerShout(string title, string message)
    {
        if (Chat.instance == null) return;
        // This overload accepts a display title rather than a forged platform user.
        var method = AccessTools.Method(typeof(Terminal), "AddString",
            new[] { typeof(string), typeof(string), typeof(Talker.Type), typeof(bool) });
        method?.Invoke(Chat.instance, new object[] { title.Replace('<', ' ').Replace('>', ' '),
            message.Replace('<', ' ').Replace('>', ' '), Talker.Type.Shout, false });
        // Native incoming chat resets this timer before AddString. Updating
        // only the text buffer would leave a hidden chat window invisible.
        // Do not activate/focus the input field or create an in-world label.
        AccessTools.Field(typeof(Chat), "m_hideTimer")?.SetValue(Chat.instance, 0f);
    }
}

[HarmonyPatch(typeof(Terminal), "InitTerminal")]
internal static class ServerManagerTerminalCommands
{
    // Read the public GUI wrapper through its existing TMP base without adding
    // a gui_framework assembly reference just for optional F5 completion.
    private static readonly FieldInfo? InputField = AccessTools.Field(typeof(Terminal), "m_input");
    private static void Postfix() => EnsureRegistered();

    internal static void EnsureRegistered()
    {
        Dictionary<string, Terminal.ConsoleCommand> commands = ValheimPrivateAccess.GetTerminalCommands();
        foreach (string name in ServerCommands.FlatCommandNames)
        {
            string key = "sm:" + name;
            // Do not overwrite a command already registered by another owner.
            if (commands.ContainsKey(key)) continue;
            _ = new Terminal.ConsoleCommand(key, "ServerManager: " + ServerCommands.GetCommandSyntax(name),
                (Terminal.ConsoleEvent)(args => ServerManagerRuntime.SubmitTerminalCommand(args.Context, args.FullLine)),
                isCheat: false,
                optionsFetcher: () => GetFirstArgumentOptions(name),
                alwaysRefreshTabOptions: true);
        }
    }

    private static List<string> GetFirstArgumentOptions(string name)
    {
        try
        {
            global::Console? console = global::Console.instance;
            if (!ServerCommands.HasPlayerFirstArgument(name) || console == null ||
                InputField?.GetValue(console) is not TMPro.TMP_InputField input || !input.isFocused ||
                !IsFirstArgumentCompletion(name, input.text, input.caretPosition,
                    input.selectionAnchorPosition, input.selectionFocusPosition) || ZNet.instance == null)
                return new List<string>();
            // These are already-replicated online names. No RPC, disk access,
            // profile scan or per-frame cache is needed for Tab completion.
            return GetPlayerNameCompletions(ZNet.instance.GetPlayerList().Select(player => player.m_name));
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Optional suggestions must not break console input or gameplay.
            return new List<string>();
        }
    }

    internal static bool IsFirstArgumentCompletion(string name, string text, int caret,
        int selectionAnchor, int selectionFocus)
    {
        if (text == null || text.Length > ServerCommands.MaximumLineLength || caret != text.Length ||
            selectionAnchor != selectionFocus) return false;
        string prefix = "sm:" + name + " ";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        // Vanilla replaces split[1] at the caret, not the actual argument under
        // the caret. An empty list disables that path for later/quoted args.
        for (int index = prefix.Length; index < text.Length; ++index)
            if (char.IsWhiteSpace(text[index]) || char.IsControl(text[index]) ||
                text[index] == '"' || text[index] == '\\') return false;
        return true;
    }

    internal static List<string> GetPlayerNameCompletions(IEnumerable<string> names)
    {
        return names.Take(256).Where(name => !string.IsNullOrEmpty(name) && name.Length <= 128 &&
                !name.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) ||
                    char.GetUnicodeCategory(character) == UnicodeCategory.Format ||
                    character == '"' || character == '\\' || character == '/' || character == '<' || character == '>') &&
                !name.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(CharacterSteamIdentity.AccountPrefix, StringComparison.OrdinalIgnoreCase) &&
                !(name.Length == 17 && name.All(char.IsDigit)) &&
                !long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).Select(group => group.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(128).ToList();
    }
}
