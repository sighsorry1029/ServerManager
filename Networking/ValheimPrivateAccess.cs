#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Steamworks;

namespace ServerManager;

/// <summary>
/// Keeps the few unavoidable Valheim-private runtime accesses behind cached
/// reflection. Production code must not emit direct member references from the
/// build output. The real game assemblies retain these members' original
/// visibility, so direct IL references would fail at runtime.
/// </summary>
internal static class ValheimPrivateAccess
{
    private const BindingFlags InstanceFields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFields =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags InstanceMethods =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? ZNetOpenServerField =
        typeof(ZNet).GetField("m_openServer", StaticFields);
    private static readonly FieldInfo? ZNetConnectionStatusField =
        typeof(ZNet).GetField("m_connectionStatus", StaticFields);
    private static readonly FieldInfo? ZNetBannedListField =
        typeof(ZNet).GetField("m_bannedList", InstanceFields);
    private static readonly FieldInfo? ZNetAdminListField =
        typeof(ZNet).GetField("m_adminList", InstanceFields);
    // Optional: dedicated builds advertise through SteamGameServer instead.
    private static readonly FieldInfo? SteamServerLobbyField =
        typeof(ZSteamMatchmaking).GetField("m_myLobby", InstanceFields);
    private static readonly MethodInfo? ZNetSendPeerInfoMethod =
        typeof(ZNet).GetMethod(
            "SendPeerInfo",
            InstanceMethods,
            null,
            new[] { typeof(ZRpc), typeof(string) },
            null);
    private static readonly MethodInfo? ZNetInternalKickMethod =
        typeof(ZNet).GetMethod(
            "InternalKick",
            InstanceMethods,
            null,
            new[] { typeof(ZNetPeer) },
            null);

    private static readonly FieldInfo? ZRpcSocketField =
        typeof(ZRpc).GetField("m_socket", InstanceFields);
    private static readonly FieldInfo? ZRpcFunctionsField =
        typeof(ZRpc).GetField("m_functions", InstanceFields);
    private static readonly FieldInfo? ZRpcDebugField =
        typeof(ZRpc).GetField("m_DEBUG", StaticFields);
    private static readonly FieldInfo? ZRpcSentPackagesField =
        typeof(ZRpc).GetField("m_sentPackages", InstanceFields);
    private static readonly FieldInfo? ZRpcSentDataField =
        typeof(ZRpc).GetField("m_sentData", InstanceFields);

    private static readonly FieldInfo? GamePlayerProfileField =
        typeof(Game).GetField("m_playerProfile", InstanceFields);
    private static readonly FieldInfo? PlayerProfilePlayerDataField =
        typeof(PlayerProfile).GetField("m_playerData", InstanceFields);
    private static readonly FieldInfo? PlayerProfileWorldDataField =
        typeof(PlayerProfile).GetField("m_worldData", InstanceFields);

    private static readonly MethodInfo? CharacterCheckDeathMethod =
        typeof(Character).GetMethod(
            "CheckDeath",
            InstanceMethods,
            null,
            Type.EmptyTypes,
            null);
    private static readonly MethodInfo? ZdoManFlushClientObjectsMethod =
        typeof(ZDOMan).GetMethod(
            "FlushClientObjects",
            InstanceMethods,
            null,
            Type.EmptyTypes,
            null);
    private static readonly MethodInfo? ZdoManSendDestroyedMethod =
        typeof(ZDOMan).GetMethod(
            "SendDestroyed",
            InstanceMethods,
            null,
            Type.EmptyTypes,
            null);
    private static readonly FieldInfo? ZdoManDestroySendListField =
        typeof(ZDOMan).GetField("m_destroySendList", InstanceFields);

    private static readonly FieldInfo? TerminalCommandsField =
        typeof(Terminal).GetField("commands", StaticFields);
    private static readonly Type? RuntimeVersionType =
        typeof(ZNet).Assembly.GetType("Version", false);
    private static readonly FieldInfo? FirstNetworkVersionField =
        RuntimeVersionType?.GetField(
            "FirstVersionWithNetworkVersion",
            StaticFields);

    internal static CSteamID GetSteamServerLobby(ZSteamMatchmaking matchmaking) =>
        SteamServerLobbyField?.GetValue(matchmaking) is CSteamID lobby ? lobby : CSteamID.Nil;

    internal static void ValidateRequiredMembers()
    {
        _ = RequireField(ZNetOpenServerField, typeof(ZNet), "m_openServer");
        _ = RequireField(
            ZNetConnectionStatusField,
            typeof(ZNet),
            "m_connectionStatus");
        _ = RequireField(ZNetBannedListField, typeof(ZNet), "m_bannedList");
        _ = RequireField(ZNetAdminListField, typeof(ZNet), "m_adminList");
        _ = RequireMethod(ZNetSendPeerInfoMethod, typeof(ZNet), "SendPeerInfo");
        _ = RequireMethod(ZNetInternalKickMethod, typeof(ZNet), "InternalKick");
        _ = RequireField(ZRpcSocketField, typeof(ZRpc), "m_socket");
        FieldInfo rpcFunctions = RequireField(
            ZRpcFunctionsField,
            typeof(ZRpc),
            "m_functions");
        if (!typeof(IDictionary).IsAssignableFrom(rpcFunctions.FieldType))
        {
            throw new NotSupportedException(
                "Valheim ZRpc.m_functions no longer implements IDictionary.");
        }

        _ = RequireField(ZRpcDebugField, typeof(ZRpc), "m_DEBUG");
        _ = RequireField(ZRpcSentPackagesField, typeof(ZRpc), "m_sentPackages");
        _ = RequireField(ZRpcSentDataField, typeof(ZRpc), "m_sentData");
        _ = RequireField(GamePlayerProfileField, typeof(Game), "m_playerProfile");
        _ = RequireField(
            PlayerProfilePlayerDataField,
            typeof(PlayerProfile),
            "m_playerData");
        _ = RequireField(
            PlayerProfileWorldDataField,
            typeof(PlayerProfile),
            "m_worldData");
        _ = RequireMethod(
            CharacterCheckDeathMethod,
            typeof(Character),
            "CheckDeath");
        _ = RequireMethod(
            ZdoManFlushClientObjectsMethod,
            typeof(ZDOMan),
            "FlushClientObjects");
        _ = RequireMethod(
            ZdoManSendDestroyedMethod,
            typeof(ZDOMan),
            "SendDestroyed");
        _ = RequireField(
            ZdoManDestroySendListField,
            typeof(ZDOMan),
            "m_destroySendList");
        _ = RequireField(TerminalCommandsField, typeof(Terminal), "commands");
        if (RuntimeVersionType == null)
        {
            throw new TypeLoadException(
                "Valheim's runtime Version schema type is unavailable.");
        }

        _ = RequireField(
            FirstNetworkVersionField,
            RuntimeVersionType,
            "FirstVersionWithNetworkVersion");
    }

    internal static void SetOpenServer(bool value)
    {
        WriteRequired(ZNetOpenServerField, null, value, typeof(ZNet), "m_openServer");
    }

    internal static bool GetOpenServer()
    {
        return RequireField(ZNetOpenServerField, typeof(ZNet), "m_openServer")
            .GetValue(null) is bool value
            ? value
            : throw new InvalidOperationException("ZNet.m_openServer is not a Boolean.");
    }

    internal static void SetConnectionStatus(ZNet.ConnectionStatus value)
    {
        WriteRequired(
            ZNetConnectionStatusField,
            null,
            value,
            typeof(ZNet),
            "m_connectionStatus");
    }

    internal static SyncedList GetBannedList(ZNet server)
    {
        return ReadRequired<SyncedList>(
            ZNetBannedListField,
            server,
            typeof(ZNet),
            "m_bannedList");
    }

    internal static SyncedList GetAdminList(ZNet server)
    {
        return ReadRequired<SyncedList>(
            ZNetAdminListField,
            server,
            typeof(ZNet),
            "m_adminList");
    }

    internal static ZNetPeer? FindPeer(ZNet server, ZRpc rpc)
    {
        if (server == null || rpc == null)
        {
            return null;
        }

        List<ZNetPeer> peers = server.GetPeers();
        for (int index = 0; index < peers.Count; ++index)
        {
            ZNetPeer peer = peers[index];
            if (peer != null && ReferenceEquals(peer.m_rpc, rpc))
            {
                return peer;
            }
        }

        return null;
    }

    internal static void SendPeerInfo(ZNet server, ZRpc rpc, string password)
    {
        InvokeRequired(
            ZNetSendPeerInfoMethod,
            server,
            new object?[] { rpc, password },
            typeof(ZNet),
            "SendPeerInfo");
    }

    internal static void InternalKick(ZNet server, ZNetPeer peer)
    {
        InvokeRequired(
            ZNetInternalKickMethod,
            server,
            new object?[] { peer },
            typeof(ZNet),
            "InternalKick");
    }

    internal static void SetSocket(ZRpc rpc, ISocket socket)
    {
        WriteRequired(ZRpcSocketField, rpc, socket, typeof(ZRpc), "m_socket");
    }

    internal static bool ContainsRpcMethod(ZRpc rpc, int methodHash)
    {
        return GetRpcMethods(rpc).Contains(methodHash);
    }

    internal static object GetRpcMethod(ZRpc rpc, int methodHash)
    {
        IDictionary methods = GetRpcMethods(rpc);
        if (!methods.Contains(methodHash))
        {
            throw new InvalidOperationException(
                "The requested Valheim RPC method is not registered.");
        }

        return methods[methodHash] ?? throw new InvalidOperationException(
            "The requested Valheim RPC method is null.");
    }

    internal static void SetRpcMethod(ZRpc rpc, int methodHash, object method)
    {
        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        GetRpcMethods(rpc)[methodHash] = method;
    }

    internal static bool IsRpcDebugEnabled()
    {
        return ReadRequired<bool>(
            ZRpcDebugField,
            null,
            typeof(ZRpc),
            "m_DEBUG");
    }

    internal static void AddSentPackageStatistics(ZRpc rpc, int byteCount)
    {
        int packages = ReadRequired<int>(
            ZRpcSentPackagesField,
            rpc,
            typeof(ZRpc),
            "m_sentPackages");
        int bytes = ReadRequired<int>(
            ZRpcSentDataField,
            rpc,
            typeof(ZRpc),
            "m_sentData");
        WriteRequired(
            ZRpcSentPackagesField,
            rpc,
            unchecked(packages + 1),
            typeof(ZRpc),
            "m_sentPackages");
        WriteRequired(
            ZRpcSentDataField,
            rpc,
            unchecked(bytes + byteCount),
            typeof(ZRpc),
            "m_sentData");
    }

    internal static PlayerProfile? GetGamePlayerProfile(Game game)
    {
        return game?.GetPlayerProfile();
    }

    internal static void SetGamePlayerProfile(Game game, PlayerProfile profile)
    {
        WriteRequired(
            GamePlayerProfileField,
            game,
            profile,
            typeof(Game),
            "m_playerProfile");
    }

    internal static byte[]? GetPlayerData(PlayerProfile profile)
    {
        FieldInfo field = RequireField(
            PlayerProfilePlayerDataField,
            typeof(PlayerProfile),
            "m_playerData");
        object? value = field.GetValue(profile);
        if (value == null || value is byte[])
        {
            return (byte[]?)value;
        }

        throw new InvalidOperationException(
            "Valheim PlayerProfile.m_playerData has an incompatible runtime type.");
    }

    internal static int GetWorldDataCount(PlayerProfile profile)
    {
        object value = ReadRequired<object>(
            PlayerProfileWorldDataField,
            profile,
            typeof(PlayerProfile),
            "m_worldData");
        if (value is ICollection collection)
        {
            return collection.Count;
        }

        throw new InvalidOperationException(
            "Valheim PlayerProfile.m_worldData no longer implements ICollection.");
    }

    internal static void CheckDeath(Character character)
    {
        InvokeRequired(
            CharacterCheckDeathMethod,
            character,
            Array.Empty<object?>(),
            typeof(Character),
            "CheckDeath");
    }

    internal static void FlushClientObjects(ZDOMan manager)
    {
        InvokeRequired(
            ZdoManFlushClientObjectsMethod,
            manager,
            Array.Empty<object?>(),
            typeof(ZDOMan),
            "FlushClientObjects");
    }

    internal static void SendDestroyed(ZDOMan manager)
    {
        InvokeRequired(
            ZdoManSendDestroyedMethod,
            manager,
            Array.Empty<object?>(),
            typeof(ZDOMan),
            "SendDestroyed");
    }

    internal static int GetDestroySendCount(ZDOMan manager)
    {
        object value = ReadRequired<object>(
            ZdoManDestroySendListField,
            manager,
            typeof(ZDOMan),
            "m_destroySendList");
        if (value is ICollection collection)
        {
            return collection.Count;
        }

        throw new InvalidOperationException(
            "Valheim ZDOMan.m_destroySendList no longer implements ICollection.");
    }

    internal static Dictionary<string, Terminal.ConsoleCommand> GetTerminalCommands()
    {
        return ReadRequired<Dictionary<string, Terminal.ConsoleCommand>>(
            TerminalCommandsField,
            null,
            typeof(Terminal),
            "commands");
    }

    internal static bool UsesNetworkVersion(GameVersion gameVersion)
    {
        GameVersion first = ReadRequired<GameVersion>(
            FirstNetworkVersionField,
            null,
            RuntimeVersionType ?? typeof(ZNet),
            "FirstVersionWithNetworkVersion");
        return gameVersion >= first;
    }

    private static IDictionary GetRpcMethods(ZRpc rpc)
    {
        object value = ReadRequired<object>(
            ZRpcFunctionsField,
            rpc,
            typeof(ZRpc),
            "m_functions");
        return value as IDictionary ?? throw new InvalidOperationException(
            "Valheim ZRpc.m_functions no longer implements IDictionary.");
    }

    private static T ReadRequired<T>(
        FieldInfo? field,
        object? instance,
        Type declaringType,
        string fieldName)
    {
        FieldInfo required = field ?? throw new MissingFieldException(
            declaringType.FullName,
            fieldName);
        object? value = required.GetValue(instance);
        if (value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            declaringType.FullName + "." + fieldName +
            " has an incompatible runtime type.");
    }

    private static void WriteRequired(
        FieldInfo? field,
        object? instance,
        object? value,
        Type declaringType,
        string fieldName)
    {
        FieldInfo required = field ?? throw new MissingFieldException(
            declaringType.FullName,
            fieldName);
        required.SetValue(instance, value);
    }

    private static object? InvokeRequired(
        MethodInfo? method,
        object? instance,
        object?[] arguments,
        Type declaringType,
        string methodName)
    {
        MethodInfo required = method ?? throw new MissingMethodException(
            declaringType.FullName,
            methodName);
        try
        {
            return required.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static FieldInfo RequireField(
        FieldInfo? field,
        Type declaringType,
        string fieldName)
    {
        return field ?? throw new MissingFieldException(
            declaringType.FullName,
            fieldName);
    }

    private static MethodInfo RequireMethod(
        MethodInfo? method,
        Type declaringType,
        string methodName)
    {
        return method ?? throw new MissingMethodException(
            declaringType.FullName,
            methodName);
    }
}
