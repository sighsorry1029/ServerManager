using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace ServerManager;

[HarmonyPatch(
    typeof(Terminal),
    nameof(Terminal.TryRunCommand),
    new[] { typeof(string), typeof(bool), typeof(bool) })]
internal static class CheatCommandGuard
{
    private const int MaximumQueuedSignals = 8;

    private static readonly object QueueGate = new();
    private static readonly Queue<ClientDetectionSignal> Signals = new();

    private static volatile bool _monitor;
    private static volatile bool _block;
    private static volatile bool _allowAdminBypass;
    private static long _adminEntitlementExpiryTimestamp;
    private static uint _policyGeneration = 1;

    internal static void Configure(
        bool monitor,
        bool block,
        bool allowAdminBypass)
    {
        _monitor = monitor;
        _block = block;
        _allowAdminBypass = allowAdminBypass;
        _adminEntitlementExpiryTimestamp = 0;
        _policyGeneration = 1;

        if (!monitor)
        {
            ClearSignals();
        }
    }

    internal static void UpdatePolicyGeneration(uint generation)
    {
        lock (QueueGate)
        {
            if (generation <= _policyGeneration)
                throw new InvalidOperationException("The command policy generation is invalid.");
            _policyGeneration = generation;
        }
    }

    internal static void Reset()
    {
        _monitor = false;
        _block = false;
        _allowAdminBypass = false;
        _adminEntitlementExpiryTimestamp = 0;
        ClearSignals();
    }

    internal static bool TryDequeue(out ClientDetectionSignal signal)
    {
        lock (QueueGate)
        {
            if (Signals.Count == 0)
            {
                signal = default;
                return false;
            }

            signal = Signals.Dequeue();
            return true;
        }
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(string __0)
    {
        if (!_monitor)
        {
            return true;
        }

        ZNet? network = ZNet.instance;
        if (network == null || network.IsServer())
        {
            return true;
        }

        if (!TryResolveCheatCommand(__0, out string detail))
        {
            return true;
        }

        bool reportQueued = TryEnqueue(detail);
        bool hasActiveAdminEntitlement =
            reportQueued &&
            _allowAdminBypass &&
            HasActiveAdminEntitlement();
        return ShouldAllowExecution(
            _block,
            reportQueued,
            _allowAdminBypass,
            hasActiveAdminEntitlement);
    }

    private static bool ShouldAllowExecution(
        bool block,
        bool reportQueued,
        bool allowAdminBypass,
        bool hasActiveAdminEntitlement)
    {
        return !block ||
               reportQueued &&
               allowAdminBypass &&
               hasActiveAdminEntitlement;
    }

    internal static void ApplyAdminEntitlement(
        bool granted,
        int validForMilliseconds)
    {
        if (!granted || !_allowAdminBypass)
        {
            _adminEntitlementExpiryTimestamp = 0;
            return;
        }

        if (validForMilliseconds <
            AdminCommandEntitlementCodec.MinimumGrantMilliseconds ||
            validForMilliseconds >
            AdminCommandEntitlementCodec.MaximumGrantMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validForMilliseconds));
        }

        long durationTicks = checked(
            ((long)validForMilliseconds * Stopwatch.Frequency + 999L) /
            1000L);
        _adminEntitlementExpiryTimestamp = checked(
            Stopwatch.GetTimestamp() + Math.Max(1L, durationTicks));
    }

    internal static bool HasActiveAdminEntitlement()
    {
        if (!_allowAdminBypass)
        {
            return false;
        }

        long expiry = _adminEntitlementExpiryTimestamp;
        if (expiry <= Stopwatch.GetTimestamp())
        {
            _adminEntitlementExpiryTimestamp = 0;
            return false;
        }

        return true;
    }

    private static bool TryResolveCheatCommand(
        string text,
        out string detail)
    {
        detail = string.Empty;

        if (text == null)
        {
            return false;
        }

        Dictionary<string, Terminal.ConsoleCommand> commands;
        try
        {
            commands = ValheimPrivateAccess.GetTerminalCommands();
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }

        // Match Terminal.TryRunCommand: split only on the literal space,
        // preserve empty entries, and look up the lower-cased first token.
        string[] parts = text.Split(
            new[] { ' ' },
            StringSplitOptions.None);
        if (parts.Length == 0)
        {
            return false;
        }

        string commandKey = parts[0].ToLower();
        if (!commands.TryGetValue(
                commandKey,
                out Terminal.ConsoleCommand command) ||
            command == null ||
            !command.IsCheat)
        {
            return false;
        }

        detail = SanitizeDetail(commandKey);
        return true;
    }

    private static string SanitizeDetail(string commandKey) =>
        DetectionReportCodec.IsValidDetail(commandKey) ? commandKey : string.Empty;

    private static bool TryEnqueue(string detail)
    {
        lock (QueueGate)
        {
            if (Signals.Count >= MaximumQueuedSignals)
            {
                return false;
            }

            Signals.Enqueue(
                new ClientDetectionSignal(
                    DetectionEvidence.CheatCommand,
                    detail,
                    _policyGeneration));
            return true;
        }
    }

    private static void ClearSignals()
    {
        lock (QueueGate)
        {
            Signals.Clear();
        }
    }
}
