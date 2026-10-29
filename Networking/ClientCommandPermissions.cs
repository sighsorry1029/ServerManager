using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace ServerManager;

// This is a permission boundary, not cheat evidence. It is enabled only by a
// validated ServerManager session and never reads the client's local admin list.
internal static class ClientCommandPermissions
{
    private static volatile bool _sessionActive;
    private static long _adminEntitlementExpiryTimestamp;
    private static int _sessionGeneration;

    private sealed class CompletionState
    {
        internal int Generation;
        internal bool Restricted;
    }

    private static readonly ConditionalWeakTable<Terminal, CompletionState>
        CompletionStates = new();
    private static readonly ConditionalWeakTable<Terminal, CompletionState>.CreateValueCallback
        CreateCompletionState = _ => new CompletionState();

    internal static void BeginSession()
    {
        Interlocked.Exchange(ref _adminEntitlementExpiryTimestamp, 0);
        _sessionActive = true;
        Interlocked.Increment(ref _sessionGeneration);
    }

    internal static void Reset()
    {
        _sessionActive = false;
        Interlocked.Exchange(ref _adminEntitlementExpiryTimestamp, 0);
        Interlocked.Increment(ref _sessionGeneration);
    }

    internal static void ApplyAdminStatus(bool granted, int validForMilliseconds)
    {
        if (!granted)
        {
            Interlocked.Exchange(ref _adminEntitlementExpiryTimestamp, 0);
            return;
        }

        if (validForMilliseconds < AdminCommandEntitlementCodec.MinimumGrantMilliseconds ||
            validForMilliseconds > AdminCommandEntitlementCodec.MaximumGrantMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(validForMilliseconds));
        }

        if (!_sessionActive)
        {
            return;
        }

        long durationTicks = checked(
            ((long)validForMilliseconds * Stopwatch.Frequency + 999L) / 1000L);
        Interlocked.Exchange(ref _adminEntitlementExpiryTimestamp,
            checked(Stopwatch.GetTimestamp() + Math.Max(1L, durationTicks)));
    }

    internal static bool HasActiveAdminStatus()
    {
        if (!_sessionActive)
        {
            return false;
        }

        long expiry = Interlocked.Read(ref _adminEntitlementExpiryTimestamp);
        if (expiry <= Stopwatch.GetTimestamp())
        {
            Interlocked.CompareExchange(ref _adminEntitlementExpiryTimestamp, 0, expiry);
            return false;
        }

        return true;
    }

    internal static bool IsAdminOnlyCommand(string text)
    {
        if (text == null)
        {
            return false;
        }

        // Terminal.TryRunCommand splits on literal spaces, preserves empty
        // entries and lowercases with the current culture. Do not trim, accept
        // slash prefixes, split on tabs, or use ordinal case folding instead.
        int separator = text.IndexOf(' ');
        int length = separator < 0 ? text.Length : separator;
        if (length != 10 && length != 7)
        {
            return false;
        }

        string command = (separator < 0 ? text : text.Substring(0, separator)).ToLower();
        return command == "printseeds" || command == "freefly";
    }

    private static bool IsRestricted()
    {
        if (!_sessionActive)
        {
            return false;
        }

        ZNet? network = ZNet.instance;
        return network != null && !network.IsServer() && !HasActiveAdminStatus();
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.TryRunCommand),
        new[] { typeof(string), typeof(bool), typeof(bool) })]
    private static class ExecutePatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Terminal __instance, string __0)
        {
            if (!IsRestricted() || !IsAdminOnlyCommand(__0))
            {
                return true;
            }

            // Chat.InputText supplies silentFail=true via Unity's object/bool
            // conversion. Permission refusal must still be visible in chat.
            __instance.AddString(PlayerLocalizer.Text("sm_command_admin_required"));
            return false;
        }
    }

    [HarmonyPatch(typeof(Terminal.ConsoleCommand), nameof(Terminal.ConsoleCommand.ShowCommand))]
    private static class VisibilityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Terminal.ConsoleCommand __instance, ref bool __result)
        {
            // Preserve vanilla/foreign restrictions; never make a command
            // visible or change shared IsCheat/OnlyAdmin/RemoteCommand flags.
            if (__result && IsRestricted() && IsAdminOnlyCommand(__instance.Command))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Terminal), "UpdateInput")]
    private static class CompletionCachePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            Terminal __instance,
            List<string> ___m_commandList,
            List<string> ___m_tabOptions,
            List<string> ___m_lastSearch,
            ref int ___m_tabCaretPosition,
            ref int ___m_lastSearchLength)
        {
            bool restricted = IsRestricted();
            int generation = Volatile.Read(ref _sessionGeneration);
            CompletionState state = CompletionStates.GetValue(__instance, CreateCompletionState);
            if (state.Generation == generation && state.Restricted == restricted)
            {
                return;
            }

            state.Generation = generation;
            state.Restricted = restricted;

            // Original 1.0.20 private fields, injected by Harmony. The next
            // vanilla UpdateInput pass rebuilds through ShowCommand, including
            // other mods' filters. Also reset length/caret so an unchanged input
            // cannot retain a stale search or tab cycle after revoke/expiry.
            ___m_commandList.Clear();
            ___m_tabOptions.Clear();
            ___m_lastSearch.Clear();
            ___m_tabCaretPosition = -1;
            ___m_lastSearchLength = -1;
        }
    }
}
