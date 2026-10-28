using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace ServerManager;

// Optional BCL-only contract. No FreshWorld dependency, DLL hash allowlist or
// private implementation hooks. All calls and registry checks run on Unity's thread.
internal sealed class FreshWorldScheduleBridge
{
    private const string ApiName = "FreshWorld.API.MaintenanceCoordination";
    private const string PluginId = "sighsorry.FreshWorld";
    private readonly object _plugin;
    private readonly MethodInfo _acquire, _validate, _release;
    private static readonly string[] Capabilities =
        { "atomic-lease-v1", "main-thread-v1", "lease-validation-v1" };

    private FreshWorldScheduleBridge(object plugin, Type api)
    {
        _plugin = plugin;
        _acquire = Method(api, "TryAcquire", typeof(Dictionary<string, string>), typeof(string), typeof(string));
        _validate = Method(api, "ValidateLease", typeof(bool), typeof(string), typeof(string), typeof(string), typeof(string));
        _release = Method(api, "ReleaseLease", typeof(bool), typeof(string), typeof(string), typeof(string), typeof(string));
    }

    internal static bool IsInstalled => Chainloader.PluginInfos.ContainsKey(PluginId);

    internal static bool TryCreate(out FreshWorldScheduleBridge? bridge, out string error)
    {
        bridge = null; error = "fw_not_installed";
        if (!IsInstalled) return false;
        error = "fw_unsupported_contract";
        try
        {
            // An installed provider without the API is not an absent provider.
            // Inspect only this optional plugin, once at job admission.
            var provider = Chainloader.PluginInfos[PluginId];
            if (provider.Instance == null) return false;
            Type api = provider.Instance.GetType().Assembly.GetType(ApiName, true)!;
            PropertyInfo? version = api.GetProperty("ContractVersion", BindingFlags.Public | BindingFlags.Static);
            PropertyInfo? capabilities = api.GetProperty("Capabilities", BindingFlags.Public | BindingFlags.Static);
            if (version?.PropertyType != typeof(int) || (int)version.GetValue(null, null)! != 2 ||
                capabilities?.PropertyType != typeof(string[]) || !(capabilities.GetValue(null, null) is string[] actual) ||
                !Capabilities.All(required => actual.Contains(required, StringComparer.Ordinal))) return false;
            bridge = new FreshWorldScheduleBridge(provider.Instance, api);
            error = "fw_available";
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { return false; }
    }

    private static MethodInfo Method(Type api, string name, Type result, params Type[] parameters)
    {
        MethodInfo? method = api.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
        if (method == null || method.ReturnType != result || method.ContainsGenericParameters)
            throw new InvalidOperationException("FreshWorld contract mismatch: " + name);
        return method;
    }

    internal bool Current => Chainloader.PluginInfos.TryGetValue(PluginId, out var provider) &&
        ReferenceEquals(provider.Instance, _plugin);

    internal bool TryAcquire(string world, string owner, out string session, out string token, out bool busy, out string error)
    {
        session = token = ""; busy = false;
        if (!InvokeSnapshot(_acquire, new object[] { world, owner },
                out Dictionary<string, string> result, out error)) return false;
        string status = Value(result, "status"), code = Value(result, "errorCode");
        busy = status == "Busy" && (code == "Busy" || code == "CleanupPending");
        if (status != "Acquired")
        { error = "fw_acquire_" + SafeCode(code, SafeCode(status)); return false; }
        if (Value(result, "worldId") != world || Value(result, "sessionId").Length == 0 || Value(result, "leaseToken").Length == 0)
        { error = "fw_invalid_lease"; return false; }
        session = result["sessionId"];
        token = result["leaseToken"];
        return true;
    }

    internal bool Validate(string world, string session, string owner, string token) =>
        InvokeBool(_validate, world, session, owner, token);

    internal bool Release(string world, string session, string owner, string token) =>
        InvokeBool(_release, world, session, owner, token);

    private bool InvokeBool(MethodInfo method, params object[] args)
    {
        try { return Current && method.Invoke(null, args) is bool value && value; }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { return false; }
    }

    private bool InvokeSnapshot(MethodInfo method, object[] args, out Dictionary<string, string> snapshot, out string error)
    {
        snapshot = new Dictionary<string, string>(StringComparer.Ordinal); error = "fw_contract_call_failed";
        if (!Current) { error = "fw_provider_changed"; return false; }
        try
        {
            if (!(method.Invoke(null, args) is Dictionary<string, string> result) || result.Count > 64 ||
                result.Any(pair => string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 64 || pair.Value == null || pair.Value.Length > 256)) return false;
            snapshot = new Dictionary<string, string>(result, StringComparer.Ordinal);
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { return false; }
    }

    internal static string Value(IReadOnlyDictionary<string, string> snapshot, string key) =>
        snapshot.TryGetValue(key, out string value) ? value : "";
    internal static string SafeCode(string code, string fallback = "invalid_state") =>
        code.Length > 0 && code.Length <= 80 && code.All(c => char.IsLetterOrDigit(c) || c == '_') ? code : fallback;

}
