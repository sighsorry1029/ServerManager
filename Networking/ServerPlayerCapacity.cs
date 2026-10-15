using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Steamworks;

namespace ServerManager;

// Optional, server-session-owned patches. Never discovered by Plugin.PatchAll:
// returning 10 from an installed transpiler is not an opt-out for other mods.
internal static class ServerPlayerCapacity
{
    internal const string HarmonyOwner = ServerManagerPlugin.ModGuid + ".PlayerCapacity";
    private static Harmony? _harmony;
    internal static int? RequestedAtStart { get; private set; }
    internal static int EffectiveLimit { get; private set; } = 10;
    internal static bool IsOverrideActive => EffectiveLimit > 10;
    internal static int GetLimit() => EffectiveLimit;

    internal static string? FindExternalOwner(IEnumerable<string> loadedPluginGuids)
    {
        foreach (string guid in loadedPluginGuids)
        {
            if (guid == "Azumatt.MaxPlayerCount" ||
                guid == "MidnightsFX.NetworkPerformanceSystem" ||
                guid == "org.bepinex.plugins.valheim_plus") return guid;
        }
        return null;
    }

    // Called after the first valid settings and storage preparation, immediately
    // before vanilla OpenServer. A client or ordinary singleplayer never calls it.
    internal static void Start(int requested, IEnumerable<string> loadedPluginGuids)
    {
        if (RequestedAtStart.HasValue) return;
        if (requested < 10 || requested > 250) throw new ArgumentOutOfRangeException(nameof(requested));
        string? externalOwner = FindExternalOwner(loadedPluginGuids);
        if (requested == 10 || externalOwner != null)
        {
            RequestedAtStart = requested;
            ServerManagerPlugin.Log.LogInfo(externalOwner == null
                ? "Server maxPlayers override is off (10); vanilla or another mod controls the player limit."
                : "Server maxPlayers override is off; an external player-limit provider was detected: " + externalOwner +
                  ". Configure capacity in that mod. ServerManager authentication and character management remain active.");
            return;
        }

        EffectiveLimit = requested;
        _harmony = new Harmony(HarmonyOwner);
        try
        {
            MethodInfo peerInfo = AccessTools.Method(typeof(ZNet), "RPC_PeerInfo",
                new[] { typeof(ZRpc), typeof(ZPackage) }) ?? throw new MissingMethodException("ZNet.RPC_PeerInfo");
            _harmony.Patch(peerInfo, transpiler: Hook(nameof(AdmissionTranspiler)));
            MethodInfo steamLimit = AccessTools.Method(typeof(SteamGameServer), nameof(SteamGameServer.SetMaxPlayerCount),
                new[] { typeof(int) }) ?? throw new MissingMethodException("SteamGameServer.SetMaxPlayerCount");
            _harmony.Patch(steamLimit, prefix: Hook(nameof(SteamAdvertisementPrefix)));
            // Dedicated builds may omit this listen-server-only callback.
            MethodInfo? lobbyCreated = AccessTools.Method(typeof(ZSteamMatchmaking), "OnLobbyCreated",
                new[] { typeof(LobbyCreated_t), typeof(bool) });
            if (lobbyCreated != null)
                _harmony.Patch(lobbyCreated, postfix: Hook(nameof(LobbyCreatedPostfix)));
            RequestedAtStart = requested;
            ServerManagerPlugin.Log.LogInfo("Server maxPlayers=" + requested +
                "; includes the listen host. Changes apply after restarting the server/world session.");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Roll back only this optional feature. If rollback fails, propagate
            // instead of claiming OFF with a partially installed patch set.
            Reset();
            RequestedAtStart = requested;
            ServerManagerPlugin.Log.LogWarning("Server maxPlayers override could not be installed and was disabled (" +
                exception.GetType().Name + "). Check overlapping player-limit mods and restart. " +
                "Authentication and character management remain active.");
        }
    }

    private static HarmonyMethod Hook(string name) => new(
        AccessTools.Method(typeof(ServerPlayerCapacity), name) ?? throw new MissingMethodException(name));

    internal static void NotifyReload(int requested)
    {
        if (RequestedAtStart.HasValue && requested != RequestedAtStart.Value)
            ServerManagerPlugin.Log.LogInfo("Server maxPlayers=" + requested +
                " is pending; restart the server/world session to apply it. The current capacity provider and limit are unchanged.");
    }

    internal static void Reset()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        RequestedAtStart = null;
        EffectiveLimit = 10;
    }

    private static IEnumerable<CodeInstruction> AdmissionTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new(instructions);
        MethodInfo countPlayers = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetNrOfPlayers));
        MethodInfo configuredLimit = AccessTools.Method(typeof(ServerPlayerCapacity), nameof(GetLimit));
        int limitIndex = -1;
        for (int index = 0; index + 2 < code.Count; ++index)
        {
            if (!code[index].Calls(countPlayers) || !code[index + 1].LoadsConstant(10L) ||
                (code[index + 2].opcode != OpCodes.Blt && code[index + 2].opcode != OpCodes.Blt_S)) continue;
            if (limitIndex >= 0) throw new InvalidOperationException("Multiple vanilla player-limit checks.");
            limitIndex = index + 1;
        }
        if (limitIndex < 0) throw new InvalidOperationException("Vanilla player-limit check was changed by the game or another mod.");
        // Preserve vanilla counting, Error(9), authentication, labels and blocks.
        code[limitIndex].opcode = OpCodes.Call;
        code[limitIndex].operand = configuredLimit;
        return code;
    }

    private static void SteamAdvertisementPrefix(ref int __0)
    {
        if (IsOverrideActive && ZNet.instance != null && ZNet.instance.IsServer() &&
            ZNet.m_onlineBackend == OnlineBackendType.Steamworks) __0 = GetLimit();
    }

    private static void LobbyCreatedPostfix() => RefreshAdvertisement();

    internal static void RefreshAdvertisement()
    {
        ZNet? server = ZNet.instance;
        if (!IsOverrideActive || server == null || !server.IsServer() ||
            ZNet.m_onlineBackend != OnlineBackendType.Steamworks) return;
        try
        {
            if (server.IsDedicated())
            {
                // Listen hosts initialize SteamAPI, not the GameServer API.
                if (GameServer.GetHSteamPipe() != (HSteamPipe)0)
                    SteamGameServer.SetMaxPlayerCount(GetLimit());
                return;
            }
            if (!SteamManager.Initialized || ZSteamMatchmaking.instance == null) return;
            CSteamID lobby = ValheimPrivateAccess.GetSteamServerLobby(ZSteamMatchmaking.instance);
            if (lobby != CSteamID.Nil && !SteamMatchmaking.SetLobbyMemberLimit(lobby, GetLimit()))
                ServerManagerPlugin.Log.LogWarning("Steam could not update the advertised player limit. The configured admission limit remains active.");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning("Steam player-limit advertisement could not be updated: " + exception.Message);
        }
    }
}
