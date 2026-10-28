// This separately compiled probe keeps the entry-point name expected by the
// unchanged DiscordMonoHost. Only FreshWorld's public, uninitialized BCL API is
// called: no plugin constructor/Initialize, Unity objects, data files or network.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ServerManager.Tests
{
    public static class DiscordMonoProbe
    {
        private static int _checks;

        public static int Run()
        {
            try
            {
                Check(Type.GetType("Mono.Runtime") != null, "Probe must execute on Mono");
                string managed = Environment.GetEnvironmentVariable("SERVERMANAGER_MONO_PROBE_MANAGED");
                string expectedCorlib = Path.GetFullPath(Path.Combine(managed, "mscorlib.dll"));
                Check(string.Equals(Path.GetFullPath(typeof(object).Assembly.Location), expectedCorlib, StringComparison.OrdinalIgnoreCase),
                    "Mono corlib must come from the selected Valheim installation");
                Check(typeof(Dictionary<string, string>).Assembly == typeof(object).Assembly,
                    "Dictionary<string,string> identity must be the runtime mscorlib type");
                Console.WriteLine("Mono corlib: " + typeof(object).Assembly.Location);
                Assembly plugin = Assembly.LoadFrom(Environment.GetEnvironmentVariable("SERVERMANAGER_MONO_PROBE_DLL"));
                Console.WriteLine("FreshWorld assembly: " + plugin.GetName().FullName);
                Type api = plugin.GetType("FreshWorld.API.MaintenanceCoordination", true);
                Check(api.IsPublic && api.IsAbstract && api.IsSealed && api.Assembly == plugin,
                    "Merged FreshWorld DLL exports the public static contract type");
                PropertyInfo version = Property(api, "ContractVersion", typeof(int));
                PropertyInfo capabilities = Property(api, "Capabilities", typeof(string[]));
                Check((int)version.GetValue(null, null) == 2, "Contract version is 2");
                string[] actual = (string[])capabilities.GetValue(null, null);
                string[] required = { "atomic-lease-v1", "main-thread-v1", "lease-validation-v1" };
                Check(actual != null && required.All(capability => actual.Contains(capability, StringComparer.Ordinal)),
                    "All three lease capabilities are published");

                Type dictionary = typeof(Dictionary<string, string>);
                MethodInfo acquire = Method(api, "TryAcquire", dictionary, typeof(string), typeof(string));
                MethodInfo validate = Method(api, "ValidateLease", typeof(bool), typeof(string), typeof(string), typeof(string), typeof(string));
                MethodInfo release = Method(api, "ReleaseLease", typeof(bool), typeof(string), typeof(string), typeof(string), typeof(string));
                Check(acquire.ReturnType.Assembly == typeof(object).Assembly &&
                    acquire.GetParameters()[0].ParameterType.Assembly == typeof(object).Assembly,
                    "Public dictionary signatures retain mscorlib identity after DLL merging");

                var denied = (Dictionary<string, string>)acquire.Invoke(null,
                    new object[] { "123456789", "offline-mono-probe" });
                Check(denied["status"] == "Rejected" && denied["errorCode"] == "WrongThread" && !denied.ContainsKey("leaseToken"),
                    "Uninitialized acquisition rejects without a lease token");
                denied["status"] = "caller-edited";
                var next = (Dictionary<string, string>)acquire.Invoke(null, new object[] { "123456789", "offline-mono-probe" });
                Check(next["status"] == "Rejected" && !ReferenceEquals(next, denied), "Acquisition returns a detached result");
                Check(api.GetMethod("GetOccurrenceState") == null && api.GetMethod("TryAcquireAfterCompleted") == null,
                    "Lease-only API no longer publishes occurrence-success methods");
                object[] identity = { "123456789", "uninitialized-session", "offline-mono-probe", "not-a-lease" };
                Check(!(bool)validate.Invoke(null, identity), "Uninitialized lease validation is false");
                Check(!(bool)release.Invoke(null, identity), "Uninitialized release is false");
                Check(!(bool)release.Invoke(null, identity), "Repeated uninitialized release cannot invent ownership");
                Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "ServerManager"),
                    "The probe does not load ServerManager or invoke its runtime");
                Console.WriteLine("PASS: actual Valheim Mono FreshWorld public contract probe (" + _checks +
                    " checks; uninitialized provider only, no Unity world, game-data access or network).");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: actual Mono FreshWorld contract probe: " + error);
                return 1;
            }
        }

        private static PropertyInfo Property(Type api, string name, Type result)
        {
            PropertyInfo property = api.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            MethodInfo getter = property?.GetGetMethod(false);
            Check(property != null && property.PropertyType == result && getter != null && getter.IsStatic && getter.IsPublic &&
                getter.GetParameters().Length == 0, "Public getter contract: " + name);
            return property;
        }

        private static MethodInfo Method(Type api, string name, Type result, params Type[] parameters)
        {
            MethodInfo method = api.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                null, parameters, null);
            Check(method != null && method.IsPublic && method.IsStatic && !method.ContainsGenericParameters && method.ReturnType == result &&
                method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters), "Public method contract: " + name);
            return method;
        }

        private static void Check(bool condition, string message)
        {
            ++_checks;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
