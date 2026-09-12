using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ServerManager;

[BepInPlugin(ModGuid, ModName, ModVersion)]
[BepInIncompatibility("Azumatt.MaxPlayerCount")]
[BepInDependency("sighsorry.Clan", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("upgrade_world", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class ServerManagerPlugin : BaseUnityPlugin
{
    internal const string ModName = "ServerManager";
    internal const string ModVersion = "1.0.5";
    internal const string Author = "sighsorry";
    internal const string ModGuid = "sighsorry.ServerManager";
    internal const bool DefaultEnforceModPolicy = true;

    internal static ManualLogSource Log { get; private set; } = null!;
    internal static string ConnectionError { get; set; } = string.Empty;

    internal static ConfigEntry<string> BrandingServerAddress { get; private set; } = null!;
    internal static ConfigEntry<string> BrandingServerPassword { get; private set; } = null!;
    internal static ConfigEntry<string> BrandingButtonText { get; private set; } = null!;
    internal static ConfigEntry<string> BrandingLogoPath { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowEventNotifications { get; private set; } = null!;

    internal static string DataRoot => ServerDataRoot.ActivePath;
    internal static string CharacterRoot => Path.Combine(DataRoot, "characters");

    private readonly Harmony _harmony = new(ModGuid);
    private bool _shuttingDown;
    private bool _quitHandlerRegistered;

    private void Awake()
    {
        Log = Logger;

        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            BindConfiguration();
            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }

        bool runtimeInitialized = false;
        bool wantsToQuitHooked = false;
        bool quittingHooked = false;
        try
        {
            PlayerLocalizer.Initialize();
            ServerManagerRuntime.Initialize();
            runtimeInitialized = true;
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            ServerManagerTerminalCommands.EnsureRegistered();
            Application.wantsToQuit += OnWantsToQuit;
            wantsToQuitHooked = true;
            Application.quitting += OnQuitting;
            quittingHooked = true;
            _quitHandlerRegistered = true;
            Log.LogInfo($"{ModName} {ModVersion} initialized without a preloader patcher.");
        }
        catch (Exception exception)
        {
            Log.LogFatal($"Failed to initialize {ModName}: {exception}");
            if (quittingHooked)
            {
                Application.quitting -= OnQuitting;
            }

            if (wantsToQuitHooked)
            {
                Application.wantsToQuit -= OnWantsToQuit;
            }

            _quitHandlerRegistered = false;
            try
            {
                UnpatchOwnedPatches();
            }
            catch (Exception cleanupException) when (
                !IntegrityCanonical.IsFatal(cleanupException))
            {
                Log.LogWarning(
                    "ServerManager patch rollback failed: " +
                    cleanupException.Message);
            }

            if (runtimeInitialized)
            {
                try
                {
                    ServerManagerRuntime.Shutdown();
                }
                catch (Exception cleanupException) when (
                    !IntegrityCanonical.IsFatal(cleanupException))
                {
                    Log.LogWarning(
                        "ServerManager runtime rollback failed: " +
                        cleanupException.Message);
                }
            }

            throw;
        }
    }

    private void UnpatchOwnedPatches()
    {
        // HarmonyX can rebuild a method between individual removals. Remove
        // our finalizers first while their prefix state/__runOriginal still
        // exists, including initialization rollback after a partial PatchAll.
        foreach (MethodBase original in new List<MethodBase>(Harmony.GetAllPatchedMethods()))
        {
            Patches? patches = Harmony.GetPatchInfo(original);
            if (patches == null) continue;
            foreach (Patch patch in patches.Finalizers)
            {
                if (patch.owner != ModGuid) continue;
                _harmony.Unpatch(original, HarmonyPatchType.Finalizer, ModGuid);
                break;
            }
        }
        _harmony.UnpatchSelf();
    }

    private void Update()
    {
        if (!_shuttingDown)
        {
            ServerManagerRuntime.Tick();
        }
    }

    private void OnDestroy()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        try
        {
            if (_quitHandlerRegistered)
            {
                Application.wantsToQuit -= OnWantsToQuit;
                Application.quitting -= OnQuitting;
                _quitHandlerRegistered = false;
            }

            try
            {
                ServerManagerRuntime.Shutdown();
            }
            finally
            {
                ConnectionErrorPanelPresentation.Shutdown();
                ClientMenuBranding.Shutdown();
            }
        }
        finally
        {
            UnpatchOwnedPatches();
        }
    }

    private bool OnWantsToQuit()
    {
        return ServerManagerRuntime.BeforeApplicationQuit();
    }

    private void OnQuitting()
    {
        ServerManagerRuntime.BeforeApplicationQuitting();
    }

    private void BindConfiguration()
    {
        BrandingServerAddress = Bind(
            "1 - Client",
            "Server Address",
            string.Empty,
            "Dedicated Steamworks host or IP with an optional port, for example " +
            "example.com:2456. A missing port defaults to 2456. For a Steam local-host server, " +
            "use steam:<host Steam64 ID>, not a lobby ID or IP address. Optional preview requires " +
            "Steam friendship, a visible Valheim lobby and ServerManager on the host. Empty or invalid values leave the " +
            "vanilla Start flow unchanged. Client-local; restart after editing. " +
            "Hold Alt while clicking Start for the world/server menu; this never bypasses server security.", 5);

        BrandingServerPassword = Bind(
            "1 - Client",
            "Server Password",
            string.Empty,
            "Optional password submitted through Valheim's normal handshake. Leave empty to " +
            "show the vanilla password prompt. A configured value is stored as plaintext in " +
            "this BepInEx config file and is never synchronized by ServerManager.", 4);

        BrandingButtonText = Bind(
            "1 - Client",
            "Server Button Text",
            "Start Modded Valheim Server",
            "Literal text used for the main-menu Start button and the character Start button " +
            "while the configured server flow is active. Restart the client after changing it.", 3);

        BrandingLogoPath = Bind(
            "1 - Client",
            "Logo Path",
            "https://i.ibb.co/23XsG7tz/download.png",
            "PNG path relative to BepInEx (for example plugins/MyModpack/logo.png), or an " +
            "HTTPS URL returning PNG data. Local folders are not searched. HTTPS loads in the " +
            "background with a 30-second total timeout, an 8 MiB limit and a last-good cache " +
            "under Valheim's local save path/ServerManager/cache/logos. Failures keep the cached or vanilla logo. " +
            "Absolute file paths, escaping paths, HTTP URLs, URL credentials and fragments " +
            "are rejected. Leave empty for the vanilla logo. Restart the client after changing it.", 2);

        ShowEventNotifications = Bind(
            "1 - Client",
            "Show Event Notifications",
            true,
            "Show ServerManager announcements, deaths, PvP and boss kills at the top center. " +
            "Client-local; Config Manager changes apply immediately. " +
            "Turning this off clears visible messages without affecting server logs, Discord " +
            "webhooks or vanilla messages. Direct file edits require a client restart.", 1);
    }

    // Read by Configuration Manager through ConfigDescription.Tags; no hard dependency.
    private sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }

    private ConfigEntry<T> Bind<T>(
        string section,
        string key,
        T defaultValue,
        string description,
        int order)
    {
        return Config.Bind(section, key, defaultValue,
            new ConfigDescription(description, null, new ConfigurationManagerAttributes { Order = order }));
    }
}
