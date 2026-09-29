using System;
using System.Reflection;

namespace ServerManager;

internal readonly struct InjectedCheatAssemblyInspection
{
    internal InjectedCheatAssemblyInspection(
        bool valheimToolerDetected,
        bool valheavenDetected,
        bool inspectionIncomplete)
    {
        ValheimToolerDetected = valheimToolerDetected;
        ValheavenDetected = valheavenDetected;
        InspectionIncomplete = inspectionIncomplete;
    }

    internal bool ValheimToolerDetected { get; }

    internal bool ValheavenDetected { get; }

    internal bool InspectionIncomplete { get; }
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
        bool inspectionIncomplete = false;

        try
        {
            string? name = assembly.GetName().Name;
            valheimToolerDetected = MatchesName(name, ValheimToolerName);
            valheavenDetected = MatchesName(name, ValheavenName) ||
                MatchesName(name, ValheavenMenuName) ||
                MatchesName(name, ValheimAdminMenuName);
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            inspectionIncomplete = true;
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
            inspectionIncomplete = true;
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            return new InjectedCheatAssemblyInspection(
                valheimToolerDetected,
                valheavenDetected,
                true);
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
                inspectionIncomplete = true;
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
            inspectionIncomplete);
    }

    private static bool MatchesName(string? value, string signature) =>
        string.Equals(value, signature, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesNamespace(string? value, string signature) =>
        MatchesName(value, signature) ||
        (value != null && value.Length > signature.Length &&
            value[signature.Length] == '.' &&
            value.StartsWith(signature, StringComparison.OrdinalIgnoreCase));
}
