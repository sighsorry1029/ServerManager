using System;
using System.Reflection;

namespace ServerManager;

internal readonly struct InjectedCheatAssemblyInspection
{
    internal InjectedCheatAssemblyInspection(
        bool valheimToolerDetected,
        bool valheavenDetected,
        string assemblyName,
        string failureStage,
        Exception? failureException)
    {
        ValheimToolerDetected = valheimToolerDetected;
        ValheavenDetected = valheavenDetected;
        AssemblyName = assemblyName;
        FailureStage = failureStage;
        FailureException = failureException;
    }

    internal bool ValheimToolerDetected { get; }

    internal bool ValheavenDetected { get; }

    internal bool InspectionIncomplete => FailureException != null;

    internal string AssemblyName { get; }

    internal string FailureStage { get; }

    // Transient result only: retain the original exception/stack, including
    // loader exceptions, until the main-thread agent writes its bounded log.
    internal Exception? FailureException { get; }
}

internal static class InjectedCheatAssemblyInspector
{
    private const string ValheimToolerName = "ValheimTooler";
    private const string ValheavenName = "Valheaven";
    private const string ValheavenMenuName = "ValheavenModMenu";
    private const string ValheimAdminMenuName = "ValheimAdminMenu";

    internal static InjectedCheatAssemblyInspection Inspect(Assembly assembly)
    {
        if (assembly == null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        bool valheimToolerDetected = false;
        bool valheavenDetected = false;
        string assemblyName = "unknown";
        string failureStage = string.Empty;
        Exception? failureException = null;

        try
        {
            string? name = assembly.GetName().Name;
            assemblyName = string.IsNullOrEmpty(name) ? "unknown" : name!;
            valheimToolerDetected = MatchesName(name, ValheimToolerName);
            valheavenDetected = MatchesName(name, ValheavenName) ||
                MatchesName(name, ValheavenMenuName) ||
                MatchesName(name, ValheimAdminMenuName);
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            failureStage = "name";
            failureException = exception;
        }

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // Loader failures do not discard successfully materialized types.
            // The diagnostic tells the server that this inspection had blind
            // spots while still allowing a positive partial match through.
            types = exception.Types;
            if (failureException == null)
            {
                failureStage = "types";
                failureException = exception;
            }
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            return new InjectedCheatAssemblyInspection(
                valheimToolerDetected,
                valheavenDetected,
                assemblyName,
                failureException == null ? "types" : failureStage,
                failureException ?? exception);
        }

        foreach (Type? type in types)
        {
            if (type == null)
            {
                continue;
            }

            string? typeNamespace;
            try
            {
                typeNamespace = type.Namespace;
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                if (failureException == null)
                {
                    failureStage = "namespace";
                    failureException = exception;
                }
                continue;
            }

            valheimToolerDetected |= MatchesNamespace(typeNamespace, ValheimToolerName);
            valheavenDetected |= MatchesNamespace(typeNamespace, ValheavenName) ||
                MatchesNamespace(typeNamespace, ValheimAdminMenuName);
            if (valheimToolerDetected && valheavenDetected)
            {
                break;
            }
        }

        return new InjectedCheatAssemblyInspection(
            valheimToolerDetected,
            valheavenDetected,
            assemblyName,
            failureStage,
            failureException);
    }

    private static bool MatchesName(string? value, string signature) =>
        string.Equals(value, signature, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesNamespace(string? value, string signature) =>
        MatchesName(value, signature) ||
        (value != null && value.Length > signature.Length &&
            value[signature.Length] == '.' &&
            value.StartsWith(signature, StringComparison.OrdinalIgnoreCase));
}
