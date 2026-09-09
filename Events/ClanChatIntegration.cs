using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace ServerManager.Events;

/// <summary>
/// Optional, server-side subscription to Clan's accepted-chat event. No Clan
/// assembly reference or client-reported clan identity is needed for logging.
/// </summary>
internal static class ClanChatIntegration
{
    private static readonly Action<string, long, string, string, string, string>
        ChatAccepted = OnServerChatAccepted;
    private static EventInfo? _chatEvent;

    internal static void Start()
    {
        if (_chatEvent != null ||
            !Chainloader.PluginInfos.TryGetValue("sighsorry.Clan", out var plugin) ||
            plugin.Instance == null)
        {
            return;
        }

        try
        {
            Type? api = plugin.Instance.GetType().Assembly.GetType("Clan.ClanApi");
            EventInfo? chatEvent = api?.GetEvent(
                "ServerChatAccepted",
                BindingFlags.Public | BindingFlags.Static);
            if (chatEvent?.EventHandlerType != ChatAccepted.GetType() ||
                chatEvent.GetAddMethod() == null ||
                chatEvent.GetRemoveMethod() == null)
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Clan chat logging is unavailable: install Clan with " +
                    "the ServerChatAccepted API (Clan 1.0.3 or newer).");
                return;
            }

            chatEvent.AddEventHandler(null, ChatAccepted);
            _chatEvent = chatEvent;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Warn("subscription", exception);
        }
    }

    internal static void Stop()
    {
        EventInfo? chatEvent = _chatEvent;
        _chatEvent = null;
        if (chatEvent == null)
        {
            return;
        }

        try
        {
            chatEvent.RemoveEventHandler(null, ChatAccepted);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Warn("unsubscription", exception);
        }
    }

    private static void OnServerChatAccepted(
        string platformId,
        long characterPlayerId,
        string playerName,
        string clanId,
        string clanName,
        string message)
    {
        try
        {
            ServerEventRuntime.RecordClanChat(
                platformId,
                characterPlayerId,
                playerName,
                clanId,
                clanName,
                message);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Warn("recording", exception);
        }
    }

    private static void Warn(string operation, Exception exception)
    {
        try
        {
            // Do not copy private chat text or exception messages to the
            // global log. A logging failure must not prevent clan delivery.
            ServerManagerPlugin.Log.LogWarning(
                "Clan chat log " + operation + " failed: " +
                exception.GetType().Name);
        }
        catch (Exception loggingException) when (
            !IntegrityCanonical.IsFatal(loggingException))
        {
        }
    }
}
