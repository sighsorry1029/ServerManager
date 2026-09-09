using System;
using System.Reflection;

namespace ServerManager;

internal readonly struct ValheimToolerInspection
{
    internal ValheimToolerInspection(
        bool detected,
        bool inspectionIncomplete)
    {
        Detected = detected;
        InspectionIncomplete = inspectionIncomplete;
    }

    internal bool Detected { get; }

    internal bool InspectionIncomplete { get; }
}

internal static class ValheimToolerAssemblyInspector
{
    private const string ValheimToolerName = "ValheimTooler";
    private const string ValheimToolerNamespacePrefix = "ValheimTooler.";

    internal static ValheimToolerInspection Inspect(Assembly assembly)
    {
        if (assembly == null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        bool assemblyNameMatched = false;
        bool namespaceMatched = false;
        bool inspectionIncomplete = false;

        try
        {
            assemblyNameMatched = string.Equals(
                assembly.GetName().Name,
                ValheimToolerName,
                StringComparison.OrdinalIgnoreCase);
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
            return new ValheimToolerInspection(
                assemblyNameMatched,
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

            if (string.Equals(
                    typeNamespace,
                    ValheimToolerName,
                    StringComparison.OrdinalIgnoreCase) ||
                (typeNamespace?.StartsWith(
                     ValheimToolerNamespacePrefix,
                     StringComparison.OrdinalIgnoreCase) ?? false))
            {
                namespaceMatched = true;
                break;
            }
        }

        return new ValheimToolerInspection(
            assemblyNameMatched || namespaceMatched,
            inspectionIncomplete);
    }
}
