using System;
using SystemVersion = System.Version;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace ServerManager;

internal static class ClientMenuBranding
{
    internal const ushort DefaultServerPort = 2456;
    internal const int MaximumEndpointCharacters = 512;
    internal const int MaximumLogoBytes = 8 * 1024 * 1024;
    internal const int MaximumLogoDimension = 4096;
    internal const long MaximumLogoPixels = 4096L * 4096L;
    internal const int MaximumLogoUrlCharacters = 2048;
    internal const int LogoDownloadTimeoutSeconds = 30;
    internal const int MaximumLogoRedirects = 3;

    // These members are private in the assemblies Valheim loads at runtime.
    // Cache their metadata and avoid direct member-access IL.
    private static readonly FieldInfo? QueuedJoinServerField =
        AccessTools.Field(typeof(FejdStartup), "m_queuedJoinServer");
    private static readonly PropertyInfo? ServerPasswordProperty =
        AccessTools.Property(typeof(FejdStartup), "ServerPassword");
    private static readonly FieldInfo? LocalizationTextMeshStringsField =
        AccessTools.Field(typeof(Localization), "textMeshStrings");

    private sealed class LabelOverride
    {
        internal LabelOverride(TMP_Text text)
        {
            Text = text;
            OriginalText = text.text;
            OriginalEnableAutoSizing = text.enableAutoSizing;
            OriginalWrappingMode = text.textWrappingMode;
            OriginalFontSizeMin = text.fontSizeMin;
            OriginalFontSizeMax = text.fontSizeMax;

            Localization? localization = Localization.instance;
            if (localization != null &&
                TryGetLocalizationTextMeshStrings(
                    localization,
                    out Dictionary<TMP_Text, string> textMeshStrings) &&
                textMeshStrings.TryGetValue(
                    text,
                    out string localizationSource))
            {
                HadLocalizationSource = true;
                LocalizationSource = localizationSource;
            }
        }

        internal TMP_Text Text { get; }
        internal string OriginalText { get; }
        internal bool OriginalEnableAutoSizing { get; }
        internal TextWrappingModes OriginalWrappingMode { get; }
        internal float OriginalFontSizeMin { get; }
        internal float OriginalFontSizeMax { get; }
        internal bool HadLocalizationSource { get; }
        internal string? LocalizationSource { get; }
        internal bool LocalizationDetached { get; set; }
    }

    private static FejdStartup? _startup;
    private static LabelOverride? _mainButtonLabel;
    private static LabelOverride? _characterButtonLabel;
    private static bool _armed;
    private static bool _passwordAwaitingHandshake;
    private static bool _brandedJoinDispatched;
    private static string _pendingPassword = string.Empty;
    private static string? _ownedPassword;
    private static string? _previousPassword;
    private static bool _passwordWasOverridden;
    private static Image? _logoImage;
    private static Sprite? _vanillaLogo;
    private static bool _vanillaLogoPreserveAspect;
    private static Sprite? _customLogo;
    private static Texture2D? _customLogoTexture;
    private static string? _logoSource;
    private static RemoteLogoLoad? _remoteLogoLoad;
    private static string? _remoteLogoAttemptedSource;
    private static string _lastEndpointWarning = string.Empty;
    private static string _lastLogoWarning = string.Empty;
    private static string _lastQueuedJoinAccessWarning = string.Empty;
    private static string _lastServerPasswordAccessWarning = string.Empty;
    private static string _lastLocalizationCacheAccessWarning = string.Empty;
    private static MenuGuide? _menuGuide;
    private static bool _menuGuideFailed;
    private static bool _customMenuActive;

    private static bool IsCustomMenuActive()
    {
        if (!Chainloader.PluginInfos.TryGetValue("rdmods.custommainmenu", out var plugin) ||
            plugin.Instance == null) return false;
        return !plugin.Instance.Config.TryGetEntry<bool>(
            new ConfigDefinition("General", "Enabled"), out var enabled) || enabled.Value;
    }

    private static void RefreshMenuCompatibility(FejdStartup startup)
    {
        bool active = IsCustomMenuActive();
        if (active == _customMenuActive) return;
        _customMenuActive = active;
        ResetMenuGuide();
        ApplyLogo(startup);
    }

    // Consume only the frame that closes our dialog so vanilla cannot handle the
    // same Escape/cancel input behind it. Normal menu updates continue while open.
    internal static bool BeforeUiUpdate() => _menuGuide?.HandleDialogInput() != true;

    // The worker receives only a URI/token. Its result is consumed by this menu's
    // UI update; no continuation may access Unity objects or install a sprite.
    private sealed class RemoteLogoLoad
    {
        internal readonly FejdStartup Startup;
        internal readonly Image Image;
        internal readonly string Source;
        internal readonly string? CachePath;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly Task<byte[]> Download;

        internal RemoteLogoLoad(FejdStartup startup, Image image, string source, Uri url, string? cachePath)
        {
            Startup = startup;
            Image = image;
            Source = source;
            CachePath = cachePath;
            CancellationToken token = Stop.Token;
            Download = Task.Run(() => DownloadLogoAsync(url, token));
        }
    }


    private static void ResetMenuGuide()
    {
        _menuGuide?.Dispose();
        _menuGuide = null;
        _menuGuideFailed = false;
    }

    private static void UpdateMenuGuide(FejdStartup startup, bool refreshText = false)
    {
        if (!CanUseClientUi(startup))
        {
            ResetMenuGuide();
            return;
        }
        ObserveStartup(startup);
        if (_menuGuideFailed) return;
        try
        {
            _menuGuide ??= new MenuGuide(startup);
            _menuGuide.Update(refreshText);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ResetMenuGuide();
            _menuGuideFailed = true; // Retry on GUI setup, not on every frame.
            ServerManagerPlugin.Log.LogWarning("Client menu guidance was unavailable; vanilla UI restored: " + exception.Message);
        }
    }

    internal static bool ShouldShowWorldMenuHint(string? endpoint) =>
        endpoint?.Trim().StartsWith("steam:", StringComparison.OrdinalIgnoreCase) == true
            ? OptionalModLobbyQuery.TryParseHostEndpoint(endpoint, out _)
            : TryParseDedicatedEndpoint(endpoint, out _, out _, out _);

    internal static Vector2 CalculateGuideSize(float menuWidth, float menuHeight, float preferredHeight)
    {
        if (float.IsNaN(menuWidth) || float.IsInfinity(menuWidth) || menuWidth <= 0f ||
            float.IsNaN(menuHeight) || float.IsInfinity(menuHeight) || menuHeight <= 0f)
            return Vector2.zero;
        float width = Mathf.Min(440f, menuWidth * 0.27f);
        float availableHeight = menuHeight - Mathf.Min(48f, menuHeight * 0.1f);
        float desiredHeight = float.IsNaN(preferredHeight) || float.IsInfinity(preferredHeight)
            ? availableHeight : Mathf.Max(0f, preferredHeight) + 40f;
        return new Vector2(width, Mathf.Min(availableHeight, desiredHeight));
    }

    // All temporary main-menu objects and visibility snapshots share this owner.
    // No new scene/prefab, network setting or independent update component.
    private sealed class MenuGuide
    {
        private readonly FejdStartup _startup;
        private readonly List<KeyValuePair<GameObject, bool>> _hidden = new();
        private MenuTextPanel? _panel;
        private MenuTextPanel? _optionalPanel;
        private GameObject? _launcher;
        private GameObject? _dialog;
        private GameObject? _previousSelection;
        private bool _open;
        private bool _showMods;
        private Button? _modsButton;
        private Button? _guideButton;
        private Button? _closeButton;

        private void SetOpen(bool open)
        {
            _open = open;
            if (open)
            {
                _previousSelection = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                _dialog!.SetActive(true);
                _dialog.GetComponentInChildren<Button>().Select();
            }
            else
            {
                _dialog?.SetActive(false);
                if (_previousSelection != null && _previousSelection.activeInHierarchy)
                    UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(_previousSelection);
                _previousSelection = null;
            }
        }

        internal bool HandleDialogInput()
        {
            if (!_open || !CanShow(_startup)) return false;
            if (ZInput.GetKeyDown(KeyCode.Escape, true) || ZInput.GetButtonDown("JoyButtonB"))
            {
                SetOpen(false);
                return true;
            }
            return false;
        }

        private Button AddButton(Transform parent, string text, Vector2 position, UnityEngine.Events.UnityAction action)
        {
            GameObject go = new("ServerManager " + text, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(150f, 36f);
            Image image = go.GetComponent<Image>();
            image.color = new Color(0.18f, 0.14f, 0.09f, 1f);
            TMP_Text label = MenuTextPanel.CreateText(go.transform, _startup.m_connectionFailedError,
                "Label", 20f, new Color(1f, 0.8f, 0.4f));
            label.text = text;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            MenuTextPanel.Stretch(label.rectTransform, Vector2.zero, Vector2.zero);
            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            return button;
        }

        private void EnsureDialog()
        {
            if (_launcher != null) return;
            _launcher = AddButton(_startup.m_mainMenu.transform, "Server Info", new Vector2(0f, -20f),
                () => SetOpen(true)).gameObject;
            RectTransform launchRect = (RectTransform)_launcher.transform;
            launchRect.anchorMin = launchRect.anchorMax = launchRect.pivot = Vector2.one;
            launchRect.anchoredPosition = new Vector2(-24f, -24f);
            _dialog = new GameObject("ServerManager Info", typeof(RectTransform), typeof(Image),
                typeof(Canvas), typeof(GraphicRaycaster));
            _dialog.SetActive(false);
            _dialog.transform.SetParent(_startup.m_mainMenu.transform, false);
            MenuTextPanel.Stretch((RectTransform)_dialog.transform, Vector2.zero, Vector2.zero);
            _dialog.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.94f);
            Canvas canvas = _dialog.GetComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;
            Button guide = AddButton(_dialog.transform, "Guide", new Vector2(-160f, -20f), () => _showMods = false);
            _modsButton = AddButton(_dialog.transform, "Allowed Mods", new Vector2(0f, -20f), () => _showMods = true);
            Button close = AddButton(_dialog.transform, "Close", new Vector2(160f, -20f), () => SetOpen(false));
            _guideButton = guide;
            _closeButton = close;
            Button[] buttons = { guide, _modsButton, close };
            for (int i = 0; i < buttons.Length; ++i)
                buttons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                    selectOnLeft = buttons[(i + 2) % 3], selectOnRight = buttons[(i + 1) % 3] };
        }
        private OptionalModQuery? _optionalQuery;
        private OptionalModLobbyQuery? _optionalLobbyQuery;
        private bool _optionalLobbyMode;
        private string? _optionalEndpointInput;
        private bool _optionalFailed;
        private long _optionalRevision = -1;
        private string? _optionalLanguage;
        private string? _language;
        private string? _hintEndpoint;
        private bool _visible;

        internal MenuGuide(FejdStartup startup) => _startup = startup;

        private static bool Active(GameObject? value) => value != null && value.activeInHierarchy;

        internal static bool CanShow(FejdStartup startup) =>
            Active(startup.m_mainMenu) && Active(startup.m_menuList) &&
            !Active(startup.m_connectionFailedPanel) && !Active(startup.m_worldVersionPanel) &&
            !Active(startup.m_playerVersionPanel) && !Active(startup.m_newGameVersionPanel) &&
            !Active(startup.m_ndaPanel) && !Active(startup.m_loading) && !Active(startup.m_pleaseWait) &&
            !UnifiedPopup.IsVisible() && !Feedback.IsVisible();

        internal void Update(bool refreshText)
        {
            if (!CanShow(_startup))
            {
                if (_open) SetOpen(false);
                _launcher?.SetActive(false);
                SetVisible(false);
                return;
            }
            if (_customMenuActive)
            {
                EnsureDialog();
                _launcher!.SetActive(true);
                if (!_open)
                {
                    SetVisible(false);
                    return;
                }
                _dialog!.transform.SetAsLastSibling();
            }
            _panel ??= new MenuTextPanel(_startup, left: false);
            if (_customMenuActive) _panel.UseDialog(_dialog!.transform);
            string language = Localization.instance?.GetSelectedLanguage() ?? "English";
            string endpoint = ServerManagerPlugin.BrandingServerAddress?.Value ?? string.Empty;
            if (_modsButton != null)
            {
                bool hasEndpoint = !string.IsNullOrWhiteSpace(endpoint);
                if (_modsButton.interactable != hasEndpoint)
                {
                    _modsButton.interactable = hasEndpoint;
                    Navigation guideNavigation = _guideButton!.navigation;
                    guideNavigation.selectOnRight = hasEndpoint ? _modsButton : _closeButton;
                    _guideButton.navigation = guideNavigation;
                    Navigation closeNavigation = _closeButton!.navigation;
                    closeNavigation.selectOnLeft = hasEndpoint ? _modsButton : _guideButton;
                    _closeButton.navigation = closeNavigation;
                    if (!hasEndpoint && UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject == _modsButton.gameObject)
                        _guideButton.Select();
                }
                if (!_modsButton.interactable) _showMods = false;
            }
            if (refreshText || !string.Equals(_language, language, StringComparison.Ordinal) ||
                !string.Equals(_hintEndpoint, endpoint, StringComparison.Ordinal))
            {
                _language = language;
                _hintEndpoint = endpoint;
                string body = string.Join("\n\n",
                    PlayerLocalizer.Text("sm_menu_guide_character"),
                    PlayerLocalizer.Text("sm_menu_guide_mods"),
                    PlayerLocalizer.Text("sm_menu_guide_update"));
                if (ShouldShowWorldMenuHint(endpoint))
                    body += "\n\n" + PlayerLocalizer.Text("sm_menu_guide_worlds");
                _panel.SetText(PlayerLocalizer.Text("sm_menu_guide_title"), body);
            }
            SetVisible(true);
            _panel.UpdateLayout();
            UpdateOptionalList(endpoint, language, refreshText);
            if (_customMenuActive)
            {
                _panel.SetVisible(!_showMods || string.IsNullOrWhiteSpace(endpoint));
                _optionalPanel?.SetVisible(_showMods && !string.IsNullOrWhiteSpace(endpoint));
            }
            if (_customMenuActive && _showMods) _optionalPanel?.HandleGuideKeys();
            else _panel.HandleGuideKeys();
        }

        private void UpdateOptionalList(string endpoint, string language, bool refreshText)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _optionalPanel?.SetVisible(false);
                _optionalQuery?.Dispose();
                _optionalQuery = null;
                _optionalLobbyQuery?.Dispose();
                _optionalLobbyQuery = null;
                _optionalRevision = -1;
                return;
            }
            if (_optionalFailed) return;
            try
            {
                if (!string.Equals(_optionalEndpointInput, endpoint, StringComparison.Ordinal))
                {
                    _optionalEndpointInput = endpoint;
                    bool nextLobbyMode = endpoint.Trim().StartsWith("steam:", StringComparison.OrdinalIgnoreCase);
                    if (_optionalLobbyMode != nextLobbyMode)
                    {
                        _optionalQuery?.Pause();
                        _optionalLobbyQuery?.Pause();
                        _optionalLobbyMode = nextLobbyMode;
                        _optionalRevision = -1;
                    }
                }
                bool lobbyMode = _optionalLobbyMode;
                OptionalModQueryState state;
                bool stale;
                long revision;
                IReadOnlyList<OptionalModCatalogEntry> entries;
                if (lobbyMode)
                {
                    _optionalLobbyQuery ??= new OptionalModLobbyQuery();
                    _optionalLobbyQuery.Tick(endpoint);
                    state = _optionalLobbyQuery.State; stale = _optionalLobbyQuery.IsStale;
                    revision = _optionalLobbyQuery.DisplayRevision; entries = _optionalLobbyQuery.Entries;
                }
                else
                {
                    _optionalQuery ??= new OptionalModQuery();
                    _optionalQuery.Tick(endpoint);
                    state = _optionalQuery.State; stale = _optionalQuery.IsStale;
                    revision = _optionalQuery.DisplayRevision; entries = _optionalQuery.Entries;
                }
                _optionalPanel ??= new MenuTextPanel(_startup, left: true);
                if (_customMenuActive) _optionalPanel.UseDialog(_dialog!.transform);
                if (refreshText || _optionalRevision != revision ||
                    !string.Equals(_optionalLanguage, language, StringComparison.Ordinal))
                {
                    _optionalRevision = revision;
                    _optionalLanguage = language;
                    string body = FormatOptionalList(state, stale, entries, CaptureLoadedOptionalModVersions());
                    if (lobbyMode && (state == OptionalModQueryState.Unavailable || state == OptionalModQueryState.Unsupported))
                        body += "\n\n" + PlayerLocalizer.Text("sm_menu_optional_host_help");
                    _optionalPanel.SetText(PlayerLocalizer.Text("sm_menu_optional_title"),
                        body);
                }
                _optionalPanel.SetVisible(!_customMenuActive || _showMods);
                _optionalPanel.UpdateLayout();
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // A preview failure must not disable guidance, Start, or admission.
                _optionalPanel?.Dispose();
                _optionalPanel = null;
                _optionalQuery?.Dispose();
                _optionalQuery = null;
                _optionalFailed = true;
                _optionalLobbyQuery?.Dispose();
                _optionalLobbyQuery = null;
                ServerManagerPlugin.Log.LogWarning("Optional-mod menu preview unavailable: " + exception.Message);
            }
        }

        internal void Reflow() => _panel?.Reflow();

        internal void SetVisible(bool visible)
        {
            if (visible && !_visible) CaptureTargets();
            else if (!visible && _visible)
            {
                RestoreTargets();
                _optionalQuery?.Pause();
                _optionalLobbyQuery?.Pause();
            }
            _visible = visible;
            _panel?.SetVisible(visible && (!_customMenuActive || !_showMods));
            if (!visible) _optionalPanel?.SetVisible(false);
            if (visible)
                foreach (KeyValuePair<GameObject, bool> target in _hidden)
                    if (target.Key != null) target.Key.SetActive(false);
        }

        private bool IsSafeTarget(GameObject? target)
        {
            GameObject? menu = _startup.m_mainMenu;
            if (menu == null || _startup.m_menuList == null) return false;
            Transform? logo = menu.transform.Find("Logo/LOGO");
            return target != null && target != _startup.gameObject && target != menu &&
                   target != _startup.m_menuList && !menu.transform.IsChildOf(target.transform) &&
                   !_startup.m_menuList.transform.IsChildOf(target.transform) &&
                   (logo == null || !logo.IsChildOf(target.transform));
        }

        private void Hide(GameObject? target)
        {
            if (!IsSafeTarget(target)) return;
            foreach (KeyValuePair<GameObject, bool> saved in _hidden)
                if (saved.Key == target) return;
            _hidden.Add(new KeyValuePair<GameObject, bool>(target!, target!.activeSelf));
        }

        private void CaptureTargets()
        {
            // CustomMainMenu owns changelog, clutter and their visibility lifecycle.
            if (_customMenuActive) return;
            // Locate proven patch-log leaves, never an unverified shared UI root.
            foreach (ChangeLog log in _startup.GetComponentsInChildren<ChangeLog>(true))
            {
                if (log.m_scrollbar != _startup.m_patchLogScroll || log.m_textField == null) continue;
                Transform? window = log.m_scrollbar?.transform.parent;
                if (window != null && log.m_textField.transform.IsChildOf(window) &&
                    IsSafeTarget(window.gameObject) &&
                    (log.m_showPlayerLog == null || !log.m_showPlayerLog.transform.IsChildOf(window)))
                    Hide(window.gameObject);
                else
                {
                    Hide(log.m_textField.gameObject);
                    Hide(log.m_scrollbar?.gameObject);
                }
            }
            Hide(_startup.m_moddedText);
            foreach (Button button in _startup.GetComponentsInChildren<Button>(true))
                for (int index = 0; index < button.onClick.GetPersistentEventCount(); ++index)
                    if (button.onClick.GetPersistentTarget(index) == _startup &&
                        button.onClick.GetPersistentMethodName(index) == nameof(FejdStartup.OnMerchStoreButton))
                    {
                        Hide(button.gameObject);
                        break;
                    }
        }

        private void RestoreTargets()
        {
            foreach (KeyValuePair<GameObject, bool> target in _hidden)
                if (target.Key != null) target.Key.SetActive(target.Value);
            _hidden.Clear();
        }

        internal void Dispose()
        {
            if (_open) SetOpen(false);
            if (_launcher != null) UnityEngine.Object.Destroy(_launcher);
            if (_dialog != null) UnityEngine.Object.Destroy(_dialog);
            _launcher = _dialog = null;
            _optionalQuery?.Dispose();
            _optionalQuery = null;
            _optionalLobbyQuery?.Dispose();
            _optionalLobbyQuery = null;
            _optionalPanel?.Dispose();
            _optionalPanel = null;
            _panel?.Dispose();
            _panel = null;
            RestoreTargets();
            _visible = false;
        }
    }

    // Main-thread metadata only, once per text/result refresh. No folder search,
    // assembly loading, or additional hashing is needed for this advisory filter.
    private static IReadOnlyDictionary<string, SystemVersion> CaptureLoadedOptionalModVersions()
    {
        Dictionary<string, SystemVersion> loaded = new(StringComparer.Ordinal);
        try
        {
            foreach (BepInEx.PluginInfo plugin in Chainloader.PluginInfos.Values)
            {
                if (plugin?.Metadata?.Version == null || plugin.Instance == null ||
                    !IntegrityCanonical.TryNormalizeGuid(plugin.Metadata.GUID, IntegrityLimits.Default,
                        IntegrityDiagnosticCodes.ManifestInvalidGuid, out string guid, out _)) continue;
                if (loaded.ContainsKey(guid))
                {
                    // Ambiguous canonical identity: leave all preview rows visible.
                    loaded.Clear();
                    return loaded;
                }
                loaded.Add(guid, plugin.Metadata.Version);
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // An unavailable/changing registry must not hide entries or break UI.
            loaded.Clear();
        }
        return loaded;
    }

    internal static bool IsOptionalModLoaded(OptionalModCatalogEntry entry,
        IReadOnlyDictionary<string, SystemVersion>? loaded)
    {
        if (loaded == null || !loaded.TryGetValue(entry.PluginGuid, out SystemVersion installed)) return false;
        foreach (string version in entry.Versions)
            if (SystemVersion.TryParse(version, out SystemVersion allowed) && allowed.Equals(installed)) return true;
        return false;
    }

    internal static string FormatOptionalList(OptionalModQueryState state, bool stale,
        IReadOnlyList<OptionalModCatalogEntry> entries, IReadOnlyDictionary<string, SystemVersion>? loaded)
    {
        string statusKey = state switch
        {
            OptionalModQueryState.Loading => "sm_menu_optional_loading",
            OptionalModQueryState.Unsupported => "sm_menu_optional_unsupported",
            OptionalModQueryState.TooLarge => "sm_menu_optional_too_large",
            OptionalModQueryState.Unavailable => "sm_menu_optional_unavailable",
            _ => "sm_menu_optional_note"
        };
        StringBuilder text = new(PlayerLocalizer.Text(statusKey));
        if (stale) text.Append("\n\n").Append(PlayerLocalizer.Text("sm_menu_optional_stale"));
        if (state != OptionalModQueryState.Available && !stale) return text.ToString();
        if (entries.Count == 0)
            return text.Append("\n\n").Append(PlayerLocalizer.Text("sm_menu_optional_empty")).ToString();
        int shown = 0;
        foreach (OptionalModCatalogEntry entry in entries)
        {
            if (IsOptionalModLoaded(entry, loaded)) continue;
            ++shown;
            text.Append("\n\n").Append(entry.Name).Append("\n");
            text.Append(entry.Versions.Count == 0
                ? PlayerLocalizer.Text("sm_menu_optional_version_unknown")
                : string.Join(", ", entry.Versions));
        }
        if (shown == 0) text.Append("\n\n").Append(PlayerLocalizer.Text("sm_menu_optional_installed"));
        return text.ToString();
    }

    // Both menu columns share only layout/rendering; query and menu state remain
    // in their own owners. Text/layout change on new results, language or size only.
    private sealed class MenuTextPanel
    {
        private readonly FejdStartup _startup;
        private readonly bool _left;
        private RectTransform? _menu;
        private GameObject? _root;
        private ScrollRect? _scroll;
        private RectTransform? _content;
        private TMP_Text? _title;
        private TMP_Text? _body;
        private Scrollbar? _scrollbar;
        private Vector2 _lastSize;
        private bool _visible;
        private bool _needsLayout = true;
        private bool _inDialog;

        internal void UseDialog(Transform parent)
        {
            if (_inDialog) return;
            _inDialog = true;
            _root!.transform.SetParent(parent, false);
            _needsLayout = true;
        }

        internal MenuTextPanel(FejdStartup startup, bool left)
        {
            _startup = startup;
            _left = left;
            try { Create(); }
            catch { Dispose(); throw; }
        }

        internal void SetText(string title, string body)
        {
            _title!.text = title;
            _body!.text = body;
            _scroll!.verticalNormalizedPosition = 1f;
            _needsLayout = true;
        }

        internal void UpdateLayout()
        {
            if (_needsLayout || (_lastSize - _menu!.rect.size).sqrMagnitude > 0.25f)
            {
                Reflow();
                _needsLayout = false;
            }
        }

        internal void HandleGuideKeys()
        {
            if (_scroll!.vertical)
            {
                // ScrollRect already handles pointer wheel/drag on this panel.
                // These discrete inputs also make long translations readable
                // without a mouse; no menu selection or Alt flow is changed.
                if (ZInput.GetKeyDown(KeyCode.PageDown, true) || ZInput.GetButtonDown("JoyRBumper"))
                    _scroll.verticalNormalizedPosition = Mathf.Clamp01(_scroll.verticalNormalizedPosition - 0.25f);
                else if (ZInput.GetKeyDown(KeyCode.PageUp, true) || ZInput.GetButtonDown("JoyLBumper"))
                    _scroll.verticalNormalizedPosition = Mathf.Clamp01(_scroll.verticalNormalizedPosition + 0.25f);
            }
        }

        private void Create()
        {
            _menu = _startup.m_mainMenu.GetComponent<RectTransform>();
            TMP_Text? fontSource = _startup.m_connectionFailedError;
            if (_menu == null || fontSource == null)
                throw new InvalidOperationException("The vanilla menu canvas/font is unavailable.");

            _root = new GameObject(_left ? "ServerManagerOptionalMods" : "ServerManagerJoinGuide", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            _root.SetActive(false);
            _root.transform.SetParent(_menu, false);
            RectTransform panel = (RectTransform)_root.transform;
            panel.anchorMin = panel.anchorMax = panel.pivot = _left ? new Vector2(0f, 1f) : Vector2.one;
            _root.GetComponent<Image>().color = new Color(0.08f, 0.065f, 0.045f, 0.72f);

            GameObject viewport = new("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(panel, false);
            RectTransform viewRect = (RectTransform)viewport.transform;
            Stretch(viewRect, Vector2.zero, Vector2.zero);
            GameObject content = new("Content", typeof(RectTransform));
            content.transform.SetParent(viewRect, false);
            _content = (RectTransform)content.transform;
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = Vector2.one;
            _content.pivot = new Vector2(0.5f, 1f);
            _title = CreateText(_content, fontSource, "Heading", 26f, new Color(1f, 0.73f, 0.30f));
            _body = CreateText(_content, fontSource, "Tips", 20f, new Color(0.96f, 0.94f, 0.88f));

            _scroll = _root.GetComponent<ScrollRect>();
            _scroll.viewport = viewRect;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.inertia = false;
            _scroll.scrollSensitivity = 28f;
            _scrollbar = CreateScrollbar(panel);
            _scroll.verticalScrollbar = _scrollbar;
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        internal static TMP_Text CreateText(Transform parent, TMP_Text fontSource, string name, float size, Color color)
        {
            GameObject target = new(name, typeof(RectTransform));
            // TMP Awake must see the vanilla font, not try a missing default font.
            target.SetActive(false);
            target.transform.SetParent(parent, false);
            TMP_Text text = target.AddComponent<TextMeshProUGUI>();
            text.font = fontSource.font;
            text.fontSharedMaterial = fontSource.fontSharedMaterial;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableAutoSizing = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;
            text.margin = Vector4.zero;
            text.maxVisibleCharacters = text.maxVisibleWords = text.maxVisibleLines = int.MaxValue;
            text.rectTransform.anchorMin = new Vector2(0f, 1f);
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.pivot = new Vector2(0.5f, 1f);
            target.SetActive(true);
            return text;
        }

        internal static void Stretch(RectTransform rect, Vector2 minimum, Vector2 maximum)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = minimum;
            rect.offsetMax = maximum;
        }

        private static Scrollbar CreateScrollbar(RectTransform parent)
        {
            GameObject track = new("ScrollTrack", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            track.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)track.transform;
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(6f, -40f);
            rect.anchoredPosition = new Vector2(-9f, 0f);
            track.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.09f);
            GameObject handle = new("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(track.transform, false);
            Image handleImage = handle.GetComponent<Image>();
            handleImage.color = new Color(0.85f, 0.72f, 0.48f, 0.8f);
            Stretch((RectTransform)handle.transform, Vector2.zero, Vector2.zero);
            Scrollbar bar = track.GetComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.handleRect = (RectTransform)handle.transform;
            bar.targetGraphic = handleImage;
            bar.navigation = new Navigation { mode = Navigation.Mode.None };
            return bar;
        }

        internal void Reflow()
        {
            _lastSize = _menu!.rect.size;
            float oldScroll = _scroll!.verticalNormalizedPosition;
            float availableHeight = _lastSize.y;
            Vector2 size = CalculateGuideSize(_lastSize.x, availableHeight, 0f);
            if (_inDialog) size.x = Mathf.Min(800f, _lastSize.x * 0.9f);
            float leftPadding = Mathf.Min(20f, size.x * 0.1f);
            float rightPadding = Mathf.Min(28f, size.x * 0.14f);
            float width = Mathf.Max(1f, size.x - leftPadding - rightPadding);
            float titleHeight = _title!.GetPreferredValues(_title.text, width, float.PositiveInfinity).y + 4f;
            float bodyHeight = _body!.GetPreferredValues(_body.text, width, float.PositiveInfinity).y + 4f;
            float contentHeight = titleHeight + 16f + bodyHeight;
            size = CalculateGuideSize(_lastSize.x, availableHeight, contentHeight);
            RectTransform panel = (RectTransform)_root!.transform;
            panel.anchoredPosition = new Vector2(
                (_left ? 1f : -1f) * Mathf.Min(24f, _lastSize.x * 0.05f),
                -Mathf.Min(24f, _lastSize.y * 0.05f));
            if (_inDialog)
            {
                size = new Vector2(Mathf.Min(800f, _lastSize.x * 0.9f), Mathf.Max(1f, _lastSize.y - 100f));
                panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 1f);
                panel.anchoredPosition = new Vector2(0f, -76f);
            }
            panel.sizeDelta = size;
            float verticalPadding = Mathf.Min(20f, size.y * 0.1f);
            Stretch(_scroll.viewport, new Vector2(leftPadding, verticalPadding), new Vector2(-rightPadding, -verticalPadding));
            RectTransform track = (RectTransform)_scrollbar!.transform;
            track.sizeDelta = new Vector2(Mathf.Min(6f, size.x * 0.04f), -2f * verticalPadding);
            track.anchoredPosition = new Vector2(-Mathf.Min(9f, size.x * 0.05f), 0f);
            _content!.sizeDelta = new Vector2(0f, contentHeight);
            _title.rectTransform.sizeDelta = new Vector2(0f, titleHeight);
            _title.rectTransform.anchoredPosition = Vector2.zero;
            _body.rectTransform.sizeDelta = new Vector2(0f, bodyHeight);
            _body.rectTransform.anchoredPosition = new Vector2(0f, -titleHeight - 16f);
            panel.ForceUpdateRectTransforms();
            _title.ForceMeshUpdate(false, false);
            _body.ForceMeshUpdate(false, false);
            _scroll.vertical = contentHeight > Mathf.Max(1f, size.y - 2f * verticalPadding) + 0.5f;
            _scrollbar!.gameObject.SetActive(_scroll.vertical);
            _scroll.verticalNormalizedPosition = _scroll.vertical ? Mathf.Clamp01(oldScroll) : 1f;
        }

        internal void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (visible && !_visible)
            {
                _scroll!.verticalNormalizedPosition = 1f;
                _needsLayout = true;
            }
            _visible = visible;
            _root.SetActive(visible);
        }

        internal void Dispose()
        {
            _visible = false;
            if (_root != null)
            {
                _root.SetActive(false);
                UnityEngine.Object.Destroy(_root);
            }
            _root = null;
        }
    }


    internal static bool TryParseDedicatedEndpoint(
        string? value,
        out string host,
        out ushort port,
        out string rejection)
    {
        host = string.Empty;
        port = 0;
        rejection = string.Empty;

        string endpoint = value?.Trim() ?? string.Empty;
        if (endpoint.Length == 0)
        {
            rejection = "Server Address is empty.";
            return false;
        }

        if (endpoint.Length > MaximumEndpointCharacters)
        {
            rejection = "Server Address is too long.";
            return false;
        }

        foreach (char character in endpoint)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                rejection = "Server Address may not contain whitespace or control characters.";
                return false;
            }
        }

        if (endpoint.IndexOf("://", StringComparison.Ordinal) >= 0 ||
            endpoint.IndexOf('/') >= 0 ||
            endpoint.IndexOf('\\') >= 0 ||
            endpoint.IndexOf('@') >= 0 ||
            endpoint.IndexOf('?') >= 0 ||
            endpoint.IndexOf('#') >= 0)
        {
            rejection = "Server Address must be a host or IP with an optional port, not a URL.";
            return false;
        }

        string hostPart;
        ushort parsedPort = DefaultServerPort;
        if (endpoint[0] == '[')
        {
            int closingBracket = endpoint.IndexOf(']');
            if (closingBracket <= 1)
            {
                rejection = "The bracketed IPv6 host is invalid.";
                return false;
            }

            hostPart = endpoint.Substring(1, closingBracket - 1);
            string suffix = endpoint.Substring(closingBracket + 1);
            if (suffix.Length > 0)
            {
                if (suffix[0] != ':' ||
                    !TryParsePort(suffix.Substring(1), out parsedPort))
                {
                    rejection = "The server port is invalid.";
                    return false;
                }
            }

            if (!IPAddress.TryParse(hostPart, out IPAddress bracketedAddress) ||
                bracketedAddress.AddressFamily != AddressFamily.InterNetworkV6)
            {
                rejection = "The bracketed host is not a valid IPv6 address.";
                return false;
            }

            hostPart = bracketedAddress.ToString();
        }
        else
        {
            int colonCount = 0;
            foreach (char character in endpoint)
            {
                if (character == ':')
                {
                    colonCount++;
                }
            }

            if (colonCount == 0)
            {
                hostPart = endpoint;
            }
            else if (colonCount == 1)
            {
                int separator = endpoint.LastIndexOf(':');
                hostPart = endpoint.Substring(0, separator);
                if (!TryParsePort(endpoint.Substring(separator + 1), out parsedPort))
                {
                    rejection = "The server port is invalid.";
                    return false;
                }
            }
            else
            {
                if (!IPAddress.TryParse(endpoint, out IPAddress ipv6Address) ||
                    ipv6Address.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    rejection = "IPv6 addresses with an explicit port must use [address]:port.";
                    return false;
                }

                hostPart = ipv6Address.ToString();
            }
        }

        if (hostPart.Length == 0 || hostPart.Length > 253)
        {
            rejection = "The server host is empty or too long.";
            return false;
        }

        if (IPAddress.TryParse(hostPart, out IPAddress parsedAddress))
        {
            hostPart = parsedAddress.ToString();
        }
        else if (Uri.CheckHostName(hostPart) != UriHostNameType.Dns)
        {
            rejection = "The server host is not a valid DNS name or IP address.";
            return false;
        }

        host = hostPart;
        port = parsedPort;
        return true;
    }

    internal static bool TryParseLogoUrl(string? value, out Uri url, out string rejection)
    {
        url = null!;
        rejection = "Logo URL must be an HTTPS address without credentials, fragments or whitespace.";
        string requested = value ?? string.Empty;
        if (requested.Length == 0 || requested.Length > MaximumLogoUrlCharacters) return false;
        foreach (char character in requested)
            if (char.IsControl(character) || char.IsWhiteSpace(character)) return false;
        if (requested.IndexOf('\\') >= 0 ||
            !Uri.TryCreate(requested, UriKind.Absolute, out Uri parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(parsed.Host) ||
            parsed.UserInfo.Length != 0 || parsed.Fragment.Length != 0 ||
            parsed.HostNameType == UriHostNameType.Unknown) return false;
        url = parsed;
        rejection = string.Empty;
        return true;
    }

    private enum LogoDownloadFailure { HttpStatus, Network, Redirect, TooLarge, InvalidPng }

    // Only controlled categories and a numeric status cross the worker/UI
    // boundary. Transport exception messages can contain private URL queries.
    private sealed class LogoDownloadException : Exception
    {
        internal readonly LogoDownloadFailure Failure;
        internal readonly int Status;

        internal LogoDownloadException(LogoDownloadFailure failure, int status = 0)
            : base("HTTPS logo download failed.")
        {
            Failure = failure;
            Status = status;
        }
    }

    private static string LogoDownloadFailureMessage(Exception exception)
    {
        const string fallback = "; keeping the cached or vanilla logo.";
        if (exception is TimeoutException)
            return "The HTTPS menu logo download timed out after " +
                LogoDownloadTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds" + fallback;
        if (exception is OperationCanceledException)
            return "The HTTPS menu logo download was cancelled" + fallback;
        if (exception is LogoDownloadException failure)
        {
            switch (failure.Failure)
            {
                case LogoDownloadFailure.HttpStatus:
                    return "The HTTPS menu logo server returned an HTTP error" +
                        (failure.Status >= 100 && failure.Status <= 599
                            ? " (HTTP " + failure.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : string.Empty) + fallback;
                case LogoDownloadFailure.Network:
                    return "The HTTPS menu logo download failed (network/TLS transport error)" + fallback;
                case LogoDownloadFailure.Redirect:
                    return "The HTTPS menu logo redirect was invalid or exceeded " +
                        MaximumLogoRedirects.ToString(System.Globalization.CultureInfo.InvariantCulture) + " redirects" + fallback;
                case LogoDownloadFailure.TooLarge:
                    return "The HTTPS menu logo response exceeds the 8 MiB size limit" + fallback;
                case LogoDownloadFailure.InvalidPng:
                    return "The HTTPS menu logo response failed PNG validation" + fallback;
            }
        }
        return "The HTTPS menu logo download failed" + fallback;
    }

    internal static async Task<byte[]> DownloadLogoAsync(
        Uri url, CancellationToken cancellation, HttpMessageHandler? handler = null)
    {
        if (url == null || !url.IsAbsoluteUri ||
            !TryParseLogoUrl(url.AbsoluteUri, out Uri destination, out _))
            throw new InvalidDataException("Invalid HTTPS logo address.");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(LogoDownloadTimeoutSeconds));
        CancellationToken token = deadline.Token;
        try
        {
            handler ??= new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false,
                Credentials = null,
                AutomaticDecompression = DecompressionMethods.None,
                MaxResponseHeadersLength = 16
            };
            using HttpClient client = new(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
            for (int redirects = 0; ; ++redirects)
            {
                token.ThrowIfCancellationRequested();
                using HttpRequestMessage request = new(HttpMethod.Get, destination);
                request.Headers.Accept.ParseAdd("image/png");
                using HttpResponseMessage response = await AwaitLogoTask(
                    client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
                    token, late => late.Dispose()).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                {
                    Uri? location = response.Headers.Location;
                    if (redirects >= MaximumLogoRedirects || location == null ||
                        !Uri.TryCreate(destination, location, out Uri next) ||
                        !TryParseLogoUrl(next.AbsoluteUri, out destination, out _))
                        throw new LogoDownloadException(LogoDownloadFailure.Redirect);
                    continue;
                }
                if (status != 200) throw new LogoDownloadException(LogoDownloadFailure.HttpStatus, status);
                if (response.Content == null) throw new LogoDownloadException(LogoDownloadFailure.InvalidPng);
                if (response.Content.Headers.ContentLength > MaximumLogoBytes)
                    throw new LogoDownloadException(LogoDownloadFailure.TooLarge);
                using Stream stream = await AwaitLogoTask(response.Content.ReadAsStreamAsync(),
                    token, late => late.Dispose()).ConfigureAwait(false);
                using MemoryStream output = new();
                byte[] buffer = new byte[16 * 1024];
                while (true)
                {
                    int count = await AwaitLogoTask(stream.ReadAsync(buffer, 0, buffer.Length, token), token)
                        .ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (count == 0) break;
                    if (output.Length + count > MaximumLogoBytes)
                        throw new LogoDownloadException(LogoDownloadFailure.TooLarge);
                    output.Write(buffer, 0, count);
                }
                byte[] data = output.ToArray();
                if (!TryValidatePng(data, out _, out _, out _))
                    throw new LogoDownloadException(LogoDownloadFailure.InvalidPng);
                return data;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("HTTPS logo download deadline exceeded.");
        }
        catch (OperationCanceledException) { throw; }
        catch (LogoDownloadException) { throw; }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            throw new LogoDownloadException(LogoDownloadFailure.Network);
        }
    }

    // Some Mono handlers do not promptly honor cancellation during DNS/body IO.
    // Bound our wait as well, observe abandoned faults and dispose late results.
    private static async Task<T> AwaitLogoTask<T>(Task<T> task, CancellationToken token, Action<T>? abandoned = null)
    {
        if (task.IsCompleted) return await task.ConfigureAwait(false);
        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult(true));
        if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
        {
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted) { _ = completed.Exception; return; }
                if (completed.Status == TaskStatus.RanToCompletion && abandoned != null)
                    try { abandoned(completed.Result); } catch (Exception) { }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new OperationCanceledException(token);
        }
        return await task.ConfigureAwait(false);
    }

    internal static string GetLogoCachePath(string cacheRoot, Uri url)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || url == null || !url.IsAbsoluteUri ||
            !TryParseLogoUrl(url.AbsoluteUri, out Uri canonical, out _))
            throw new InvalidDataException("The logo cache location is unavailable.");
        using SHA256 hash = SHA256.Create();
        string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical.AbsoluteUri)))
            .Replace("-", string.Empty).ToLowerInvariant();
        return Path.Combine(Path.GetFullPath(cacheRoot), "logos", key + ".png");
    }

    internal static byte[] ReadLogoBytes(string path)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = input.Length;
        if (length > MaximumLogoBytes)
            throw new InvalidDataException("The logo exceeds the byte limit.");
        byte[] data = new byte[checked((int)length)];
        int offset = 0;
        while (offset < data.Length)
        {
            int count = input.Read(data, offset, data.Length - offset);
            if (count == 0) throw new EndOfStreamException("The logo file is incomplete.");
            offset += count;
        }
        if (input.ReadByte() != -1 || !TryValidatePng(data, out _, out _, out _))
            throw new InvalidDataException("The logo file is not a valid bounded PNG.");
        return data;
    }

    // Called by the UI only after Unity successfully decoded the downloaded PNG.
    // A failed replacement must never remove the previous working cache.
    internal static void WriteLogoCache(string path, byte[] data)
    {
        if (!TryValidatePng(data, out _, out _, out string rejection))
            throw new InvalidDataException(rejection);
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Invalid logo cache path.");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("The logo cache must use regular files and directories.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                output.Write(data, 0, data.Length);
                output.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            created = false;
        }
        finally
        {
            if (created)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool TryResolveLogoPath(
        string? bepinexRoot,
        string? relativePath,
        out string fullPath,
        out string rejection)
    {
        fullPath = string.Empty;
        rejection = string.Empty;

        string root = bepinexRoot?.Trim() ?? string.Empty;
        string requested = relativePath?.Trim() ?? string.Empty;
        if (root.Length == 0)
        {
            rejection = "The BepInEx root is unavailable.";
            return false;
        }

        if (requested.Length == 0)
        {
            rejection = "Logo Path is empty.";
            return false;
        }

        // URI schemes and Windows alternate-stream syntax are never relative
        // logo files, even on runtimes whose GetFullPath accepts a colon.
        if (Path.IsPathRooted(requested) || requested.IndexOf(':') >= 0)
        {
            rejection = "Logo Path must be relative to the BepInEx directory.";
            return false;
        }

        if (!string.Equals(
                Path.GetExtension(requested),
                ".png",
                StringComparison.OrdinalIgnoreCase))
        {
            rejection = "Logo Path must name a PNG file.";
            return false;
        }

        try
        {
            string canonicalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(canonicalRoot, requested));
            string rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
            StringComparison comparison =
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

            if (!candidate.StartsWith(rootPrefix, comparison))
            {
                rejection = "Logo Path may not leave the BepInEx directory.";
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException ||
            exception is NotSupportedException ||
            exception is PathTooLongException)
        {
            rejection = "Logo Path is invalid: " + exception.Message;
            return false;
        }
    }

    internal static bool TryValidatePng(
        byte[]? data,
        out int width,
        out int height,
        out string rejection)
    {
        width = 0;
        height = 0;
        rejection = string.Empty;

        if (data == null || data.Length < 33)
        {
            rejection = "The logo is not a complete PNG file.";
            return false;
        }

        if (data.Length > MaximumLogoBytes)
        {
            rejection = $"The logo exceeds the {MaximumLogoBytes} byte limit.";
            return false;
        }

        byte[] signature =
        {
            0x89, 0x50, 0x4E, 0x47,
            0x0D, 0x0A, 0x1A, 0x0A
        };
        for (int index = 0; index < signature.Length; index++)
        {
            if (data[index] != signature[index])
            {
                rejection = "The logo does not have a valid PNG signature.";
                return false;
            }
        }

        uint ihdrLength = ReadBigEndianUInt32(data, 8);
        if (ihdrLength != 13 ||
            data[12] != (byte)'I' ||
            data[13] != (byte)'H' ||
            data[14] != (byte)'D' ||
            data[15] != (byte)'R')
        {
            rejection = "The logo PNG does not begin with a valid IHDR chunk.";
            return false;
        }

        uint pngWidth = ReadBigEndianUInt32(data, 16);
        uint pngHeight = ReadBigEndianUInt32(data, 20);
        if (pngWidth == 0 || pngHeight == 0 ||
            pngWidth > MaximumLogoDimension ||
            pngHeight > MaximumLogoDimension ||
            (long)pngWidth * pngHeight > MaximumLogoPixels)
        {
            rejection =
                $"The logo dimensions must be between 1 and {MaximumLogoDimension} pixels " +
                "and remain within the decoded pixel limit.";
            return false;
        }

        width = (int)pngWidth;
        height = (int)pngHeight;
        return true;
    }

    internal static void OnSetupGui(FejdStartup startup)
    {
        if (!CanUseClientUi(startup))
        {
            return;
        }

        ObserveStartup(startup);
        RefreshMenuCompatibility(startup);
        ResetMenuGuide();
        UpdateMenuGuide(startup);
        ApplyLogo(startup);
        if (TryGetConfiguredEndpoint(out _))
        {
            ApplyMainButtonText(startup);
        }
    }

    internal static void BeforeMainStart(FejdStartup startup)
    {
        CancelFlow(clearConnectionPassword: true);
        if (!CanUseClientUi(startup) ||
            IsAltBypassPressed() ||
            HasQueuedJoinOrCannotInspect(startup) ||
            !TryGetConfiguredEndpoint(out _))
        {
            return;
        }

        _armed = true;
    }

    internal static void AfterMainStart(FejdStartup startup)
    {
        if (!CanUseClientUi(startup))
        {
            return;
        }

        ObserveStartup(startup);
        ApplyCharacterButtonText(startup);
    }

    internal static void AfterCharacterListUpdated(FejdStartup startup)
    {
        if (!CanUseClientUi(startup))
        {
            return;
        }

        ObserveStartup(startup);
        ApplyCharacterButtonText(startup);
    }

    internal static void OnUiUpdate(FejdStartup startup)
    {
        if (CanUseClientUi(startup)) RefreshMenuCompatibility(startup);
        UpdateMenuGuide(startup);
        TickRemoteLogo(startup);
        if (!_armed ||
            !CanUseClientUi(startup) ||
            !HasQueuedJoinOrCannotInspect(startup))
        {
            return;
        }

        // A Steam invite or another integration queued a destination after the
        // branded character screen opened. Preserve that request, but disarm
        // our label immediately so the visible target cannot disagree with the
        // server the next click will join.
        CancelFlow(clearConnectionPassword: true);
        ApplyCharacterButtonText(startup);
    }

    internal static void BeforeCharacterStart(FejdStartup startup)
    {
        if (!_armed)
        {
            return;
        }

        if (!CanUseClientUi(startup) ||
            IsAltBypassPressed() ||
            HasQueuedJoinOrCannotInspect(startup) ||
            !TryGetConfiguredEndpoint(out ServerJoinData destination))
        {
            CancelFlow(clearConnectionPassword: true);
            ApplyCharacterButtonText(startup);
            return;
        }

        string pendingPassword =
            ServerManagerPlugin.BrandingServerPassword?.Value ?? string.Empty;
        if (!TrySetQueuedJoinServer(startup, destination))
        {
            CancelFlow(clearConnectionPassword: true);
            ApplyCharacterButtonText(startup);
            return;
        }

        _pendingPassword = pendingPassword;
        _passwordAwaitingHandshake = true;
        _brandedJoinDispatched = false;
        _armed = false;
        RestoreLabel(ref _characterButtonLabel);
    }

    internal static void AfterCharacterStart(FejdStartup startup)
    {
        if (!_passwordAwaitingHandshake || _brandedJoinDispatched)
        {
            return;
        }

        // Vanilla can return before TransitionToMainScene for an invalid
        // profile, privilege denial, version mismatch, or an unjoinable server.
        // Do not let our queued destination or its secret leak into a later
        // Steam invite/FastLink connection.
        TrySetQueuedJoinServer(startup, ServerJoinData.None);
        ClearConnectionPasswordState();
    }

    internal static void BeforeTransitionToMainScene(FejdStartup startup)
    {
        if (_passwordAwaitingHandshake &&
            ReferenceEquals(_startup, startup))
        {
            _brandedJoinDispatched = true;
        }
    }

    internal static void AfterCharacterSelectionBack(FejdStartup startup)
    {
        CancelFlow(clearConnectionPassword: true);
        if (CanUseClientUi(startup))
        {
            ApplyCharacterButtonText(startup);
        }
    }

    internal static void AfterLanguageChanged(FejdStartup startup)
    {
        if (!CanUseClientUi(startup))
        {
            return;
        }

        ObserveStartup(startup);
        UpdateMenuGuide(startup, refreshText: true);
        if (TryGetConfiguredEndpoint(out _))
        {
            ApplyMainButtonText(startup);
        }

        ApplyCharacterButtonText(startup);
    }

    internal static void BeforeClientHandshake(ZNet znet)
    {
        if (znet == null ||
            znet.IsServer() ||
            !_passwordAwaitingHandshake ||
            !_brandedJoinDispatched)
        {
            return;
        }

        string? ownedPassword =
            _pendingPassword.Length == 0 ? null : _pendingPassword;
        if (!TryGetServerPassword(out string? previousPassword) ||
            !TrySetServerPassword(ownedPassword))
        {
            ClearConnectionPasswordState();
            return;
        }

        _previousPassword = previousPassword;
        _ownedPassword = ownedPassword;
        _passwordWasOverridden = true;
    }

    internal static void AfterClientHandshake(ZNet znet)
    {
        if (znet != null && !znet.IsServer())
        {
            ClearConnectionPasswordState();
        }
    }

    internal static void BeforeStartupDestroyed(FejdStartup startup)
    {
        if (!ReferenceEquals(_startup, startup))
        {
            return;
        }

        ResetMenuGuide();
        _armed = false;
        if (!_brandedJoinDispatched)
        {
            ClearConnectionPasswordState();
        }
        else
        {
            ClearOwnedPassword();
        }
        RestoreLabel(ref _mainButtonLabel);
        RestoreLabel(ref _characterButtonLabel);
        ReleaseCustomLogo(restoreVanilla: true);
        _startup = null;
    }

    internal static void OnConnectionError()
    {
        CancelFlow(clearConnectionPassword: true);
    }

    internal static void Shutdown()
    {
        ResetMenuGuide();
        CancelFlow(clearConnectionPassword: true);
        RestoreLabel(ref _mainButtonLabel);
        RestoreLabel(ref _characterButtonLabel);
        ReleaseCustomLogo(restoreVanilla: true);
        _startup = null;
        _lastEndpointWarning = string.Empty;
        _lastLogoWarning = string.Empty;
        _lastQueuedJoinAccessWarning = string.Empty;
        _lastServerPasswordAccessWarning = string.Empty;
        _lastLocalizationCacheAccessWarning = string.Empty;
    }

    private static bool TryParsePort(string value, out ushort port)
    {
        return ushort.TryParse(value, out port) && port != 0;
    }

    private static uint ReadBigEndianUInt32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }

    private static bool CanUseClientUi(FejdStartup? startup)
    {
        return startup != null &&
               !Application.isBatchMode &&
               SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
    }

    private static bool HasQueuedJoinOrCannotInspect(FejdStartup startup)
    {
        return !TryGetQueuedJoinServer(startup, out ServerJoinData queuedJoin) ||
               queuedJoin.IsValid;
    }

    private static bool TryGetQueuedJoinServer(
        FejdStartup startup,
        out ServerJoinData queuedJoin)
    {
        queuedJoin = ServerJoinData.None;
        FieldInfo? field = QueuedJoinServerField;
        if (field == null)
        {
            WarnOnce(
                ref _lastQueuedJoinAccessWarning,
                "missing",
                "Client branding quick-connect is disabled because Valheim's " +
                "queued-join field could not be found.");
            return false;
        }

        try
        {
            if (field.GetValue(startup) is ServerJoinData value)
            {
                queuedJoin = value;
                return true;
            }

            WarnOnce(
                ref _lastQueuedJoinAccessWarning,
                "unexpected-type",
                "Client branding quick-connect is disabled because Valheim's " +
                "queued-join field has an unexpected type.");
        }
        catch (Exception exception)
        {
            WarnOnce(
                ref _lastQueuedJoinAccessWarning,
                exception.GetType().FullName ?? exception.GetType().Name,
                "Client branding quick-connect could not inspect Valheim's " +
                "queued destination: " + exception.Message);
        }

        return false;
    }

    private static bool TrySetQueuedJoinServer(
        FejdStartup startup,
        ServerJoinData queuedJoin)
    {
        FieldInfo? field = QueuedJoinServerField;
        if (field == null)
        {
            WarnOnce(
                ref _lastQueuedJoinAccessWarning,
                "missing",
                "Client branding quick-connect is disabled because Valheim's " +
                "queued-join field could not be found.");
            return false;
        }

        try
        {
            field.SetValue(startup, queuedJoin);
            return true;
        }
        catch (Exception exception)
        {
            WarnOnce(
                ref _lastQueuedJoinAccessWarning,
                exception.GetType().FullName ?? exception.GetType().Name,
                "Client branding quick-connect could not update Valheim's " +
                "queued destination: " + exception.Message);
            return false;
        }
    }

    private static bool TryGetServerPassword(out string? password)
    {
        password = null;
        PropertyInfo? property = ServerPasswordProperty;
        if (property == null || !property.CanRead)
        {
            WarnOnce(
                ref _lastServerPasswordAccessWarning,
                "missing-getter",
                "Client branding could not locate Valheim's server-password property.");
            return false;
        }

        try
        {
            object? value = property.GetValue(null, null);
            if (value == null || value is string)
            {
                password = (string?)value;
                return true;
            }

            WarnOnce(
                ref _lastServerPasswordAccessWarning,
                "unexpected-type",
                "Client branding found an unexpected Valheim server-password type.");
        }
        catch (Exception exception)
        {
            WarnOnce(
                ref _lastServerPasswordAccessWarning,
                exception.GetType().FullName ?? exception.GetType().Name,
                "Client branding could not read Valheim's server password: " +
                exception.Message);
        }

        return false;
    }

    private static bool TrySetServerPassword(string? password)
    {
        PropertyInfo? property = ServerPasswordProperty;
        if (property?.GetSetMethod(nonPublic: true) == null)
        {
            WarnOnce(
                ref _lastServerPasswordAccessWarning,
                "missing-setter",
                "Client branding could not locate Valheim's server-password setter.");
            return false;
        }

        try
        {
            property.SetValue(null, password, null);
            return true;
        }
        catch (Exception exception)
        {
            WarnOnce(
                ref _lastServerPasswordAccessWarning,
                exception.GetType().FullName ?? exception.GetType().Name,
                "Client branding could not update Valheim's server password: " +
                exception.Message);
            return false;
        }
    }

    internal static bool TryGetLocalizationTextMeshStrings(
        Localization localization,
        out Dictionary<TMP_Text, string> textMeshStrings)
    {
        textMeshStrings = null!;
        FieldInfo? field = LocalizationTextMeshStringsField;
        if (field == null)
        {
            WarnOnce(
                ref _lastLocalizationCacheAccessWarning,
                "missing",
                "Client branding could not locate Valheim's TMP localization cache.");
            return false;
        }

        try
        {
            if (field.GetValue(localization) is
                Dictionary<TMP_Text, string> value)
            {
                textMeshStrings = value;
                return true;
            }

            WarnOnce(
                ref _lastLocalizationCacheAccessWarning,
                "unexpected-type",
                "Client branding found an unexpected TMP localization-cache type.");
        }
        catch (Exception exception)
        {
            WarnOnce(
                ref _lastLocalizationCacheAccessWarning,
                exception.GetType().FullName ?? exception.GetType().Name,
                "Client branding could not inspect Valheim's TMP localization cache: " +
                exception.Message);
        }

        return false;
    }

    private static bool IsAltBypassPressed()
    {
        // Keep the ordinary world/server picker reachable without editing the
        // branding configuration. This never bypasses server admission checks.
        return ZInput.GetKey(KeyCode.LeftAlt, true) ||
               ZInput.GetKey(KeyCode.RightAlt, true);
    }

    // Pure parsing/vanilla value construction: no Steam lookup, join or scene work.
    // Presence and catalog availability must never decide where Start connects.
    internal static bool TryCreateJoinDestination(string? value, out ServerJoinData destination, out string rejection)
    {
        destination = ServerJoinData.None;
        if (value?.Trim().StartsWith("steam:", StringComparison.OrdinalIgnoreCase) == true)
        {
            rejection = "Use steam:<host Steam64 ID> for a Steam local-host server.";
            if (!OptionalModLobbyQuery.TryParseHostEndpoint(value, out ulong steamHost)) return false;
            destination = new ServerJoinData(new ServerJoinDataSteamUser(steamHost));
            rejection = string.Empty;
            return true;
        }
        if (!TryParseDedicatedEndpoint(value, out string host, out ushort port, out rejection)) return false;
        destination = new ServerJoinData(new ServerJoinDataDedicated(host, port));
        return true;
    }

    private static bool TryGetConfiguredEndpoint(out ServerJoinData destination)
    {
        string value =
            ServerManagerPlugin.BrandingServerAddress?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            destination = ServerJoinData.None;
            _lastEndpointWarning = string.Empty;
            return false;
        }
        if (TryCreateJoinDestination(value, out destination, out string rejection))
        {
            _lastEndpointWarning = string.Empty;
            return true;
        }

        WarnOnce(
            ref _lastEndpointWarning,
            value + "\n" + rejection,
            "Client branding quick-connect is disabled for this menu session: " +
            rejection);
        return false;
    }

    private static void ObserveStartup(FejdStartup startup)
    {
        if (ReferenceEquals(_startup, startup))
        {
            return;
        }

        ResetMenuGuide();
        CancelFlow(clearConnectionPassword: true);
        RestoreLabel(ref _mainButtonLabel);
        RestoreLabel(ref _characterButtonLabel);
        ReleaseCustomLogo(restoreVanilla: true);
        _startup = startup;
    }

    private static void ApplyMainButtonText(FejdStartup startup)
    {
        TMP_Text? text = FindMainStartButtonText(startup);
        string label = GetConfiguredButtonText();
        if (text == null || label.Length == 0)
        {
            return;
        }

        ApplyLabel(ref _mainButtonLabel, text, label);
    }

    private static void ApplyCharacterButtonText(FejdStartup startup)
    {
        TMP_Text? text = startup.m_csStartButton == null
            ? null
            : startup.m_csStartButton.GetComponentInChildren<TMP_Text>(true);
        if (text == null)
        {
            return;
        }

        if (_armed)
        {
            string label = GetConfiguredButtonText();
            if (label.Length == 0)
            {
                return;
            }

            ApplyLabel(ref _characterButtonLabel, text, label);
        }
        else
        {
            RestoreLabel(ref _characterButtonLabel);
        }
    }

    private static TMP_Text? FindMainStartButtonText(FejdStartup startup)
    {
        if (_mainButtonLabel != null)
        {
            return _mainButtonLabel.Text;
        }

        if (startup.m_menuList == null)
        {
            return null;
        }

        Button[] buttons =
            startup.m_menuList.GetComponentsInChildren<Button>(true);
        Button? fallback = buttons.Length > 0 ? buttons[0] : null;
        foreach (Button button in buttons)
        {
            int listenerCount = button.onClick.GetPersistentEventCount();
            for (int index = 0; index < listenerCount; index++)
            {
                if (string.Equals(
                        button.onClick.GetPersistentMethodName(index),
                        nameof(FejdStartup.OnStartGame),
                        StringComparison.Ordinal))
                {
                    return button.GetComponentInChildren<TMP_Text>(true);
                }
            }
        }

        return fallback == null
            ? null
            : fallback.GetComponentInChildren<TMP_Text>(true);
    }

    private static string GetConfiguredButtonText()
    {
        string value =
            ServerManagerPlugin.BrandingButtonText?.Value?.Trim() ?? string.Empty;
        return value.Length <= 128 ? value : value.Substring(0, 128);
    }

    private static void ApplyLabel(
        ref LabelOverride? current,
        TMP_Text text,
        string label)
    {
        if (current == null || !ReferenceEquals(current.Text, text))
        {
            RestoreLabel(ref current);
            current = new LabelOverride(text);
        }

        if (!current.LocalizationDetached)
        {
            Localization? localization = Localization.instance;
            if (localization != null)
            {
                localization.RemoveTextFromCache(text);
                current.LocalizationDetached = true;
            }
        }

        ConfigureLabelForLongText(text);
        text.text = label;
    }

    private static void ConfigureLabelForLongText(TMP_Text text)
    {
        text.enableAutoSizing = true;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.fontSizeMin = Mathf.Min(
            text.fontSizeMin > 0f ? text.fontSizeMin : 12f,
            12f);
        text.fontSizeMax = Mathf.Max(text.fontSizeMax, text.fontSize);
    }

    private static void RestoreLabel(ref LabelOverride? current)
    {
        LabelOverride? restored = current;
        current = null;
        if (restored == null || restored.Text == null)
        {
            return;
        }

        TMP_Text text = restored.Text;
        Localization? localization = Localization.instance;
        if (restored.LocalizationDetached &&
            restored.HadLocalizationSource &&
            restored.LocalizationSource != null &&
            localization != null &&
            TryGetLocalizationTextMeshStrings(
                localization,
                out Dictionary<TMP_Text, string> textMeshStrings))
        {
            textMeshStrings[text] = restored.LocalizationSource;
            text.text = localization.Localize(restored.LocalizationSource);
        }
        else
        {
            text.text = restored.OriginalText;
        }

        text.enableAutoSizing = restored.OriginalEnableAutoSizing;
        text.textWrappingMode = restored.OriginalWrappingMode;
        text.fontSizeMin = restored.OriginalFontSizeMin;
        text.fontSizeMax = restored.OriginalFontSizeMax;
    }

    private static void ApplyLogo(FejdStartup startup)
    {
        if (_customMenuActive)
        {
            ReleaseCustomLogo(restoreVanilla: true);
            return;
        }
        Transform? logoTransform =
            startup.m_mainMenu?.transform.Find("Logo/LOGO");
        Image? image = logoTransform == null
            ? null
            : logoTransform.GetComponent<Image>();
        if (image == null)
        {
            WarnOnce(
                ref _lastLogoWarning,
                "missing-logo-image",
                "Client branding could not find the vanilla Logo/LOGO image; " +
                "the vanilla logo was retained.");
            return;
        }

        string requested = ServerManagerPlugin.BrandingLogoPath?.Value ?? string.Empty;
        if (!ReferenceEquals(_logoImage, image) || !string.Equals(_logoSource, requested, StringComparison.Ordinal))
        {
            ReleaseCustomLogo(restoreVanilla: true);
            _logoImage = image;
            _vanillaLogo = image.sprite;
            _vanillaLogoPreserveAspect = image.preserveAspect;
            _logoSource = requested;
        }

        if (string.IsNullOrWhiteSpace(requested)) return;
        string root = BepInEx.Paths.BepInExRootPath;
        if (requested.IndexOf("://", StringComparison.Ordinal) >= 0)
        {
            if (!TryParseLogoUrl(requested, out Uri url, out string urlRejection))
            {
                WarnOnce(ref _lastLogoWarning, "invalid-logo-url", urlRejection);
                return;
            }
            if (string.Equals(_remoteLogoAttemptedSource, requested, StringComparison.Ordinal)) return;
            CancelRemoteLogo();
            _remoteLogoAttemptedSource = requested;
            string? cachePath = null;
            try
            {
                // Resolve after Valheim has initialized its local save path.
                // This does not bind or prepare the server-owned repositories.
                string cacheRoot = Path.Combine(ServerDataRoot.ResolveCurrentPath(), "cache");
                cachePath = GetLogoCachePath(cacheRoot, url);
                if (File.Exists(cachePath)) ApplyLogoBytes(image, ReadLogoBytes(cachePath));
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                WarnOnce(ref _lastLogoWarning, "logo-cache-read", "The cached menu logo was unavailable; trying HTTPS.");
            }
            _remoteLogoLoad = new RemoteLogoLoad(startup, image, requested, url, cachePath);
            return;
        }
        if (!TryResolveLogoPath(
                root,
                requested,
                out string logoPath,
                out string pathRejection))
        {
            WarnOnce(
                ref _lastLogoWarning,
                requested + "\n" + pathRejection,
                "Client branding retained the vanilla logo: " + pathRejection);
            return;
        }

        try
        {
            ApplyLogoBytes(image, ReadLogoBytes(logoPath));
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            WarnOnce(
                ref _lastLogoWarning,
                logoPath + "\n" + exception.GetType().FullName + "\n" + exception.Message,
                "Client branding retained the vanilla logo: " + exception.Message);
        }
    }

    private static void TickRemoteLogo(FejdStartup startup)
    {
        RemoteLogoLoad? load = _remoteLogoLoad;
        if (load == null) return;
        // Cancellation alone cannot prevent an already-completed old request
        // from replacing another menu/source. Only the current owner may apply.
        if (!ReferenceEquals(load.Startup, startup) || !ReferenceEquals(_startup, startup) ||
            !ReferenceEquals(load.Image, _logoImage) || load.Image == null ||
            !CanUseClientUi(startup) ||
            !string.Equals(load.Source, ServerManagerPlugin.BrandingLogoPath?.Value, StringComparison.Ordinal))
        {
            CancelRemoteLogo();
            return;
        }
        if (!load.Download.IsCompleted) return;
        _remoteLogoLoad = null;
        load.Stop.Dispose();
        byte[] data;
        try
        {
            data = load.Download.GetAwaiter().GetResult(); // Already completed; never wait on UI.
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            WarnOnce(ref _lastLogoWarning, "logo-download-failed", LogoDownloadFailureMessage(exception));
            return;
        }
        try
        {
            ApplyLogoBytes(load.Image, data);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            WarnOnce(ref _lastLogoWarning, "logo-decode-failed",
                "The HTTPS menu logo failed during Unity PNG decode/apply; keeping the cached or vanilla logo.");
            return;
        }
        if (load.CachePath != null)
        {
            try { WriteLogoCache(load.CachePath, data); }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                WarnOnce(ref _lastLogoWarning, "logo-cache-write",
                    "The HTTPS menu logo was applied, but its cache could not be saved.");
            }
        }
    }

    private static void CancelRemoteLogo()
    {
        RemoteLogoLoad? load = _remoteLogoLoad;
        _remoteLogoLoad = null;
        if (load == null) return;
        load.Stop.Cancel();
        _ = load.Download.ContinueWith(completed =>
        {
            if (completed.IsFaulted) _ = completed.Exception;
            load.Stop.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void ApplyLogoBytes(Image image, byte[] data)
    {
        Texture2D? texture = null;
        Sprite? sprite = null;
        try
        {
            if (!TryValidatePng(
                    data,
                    out int width,
                    out int height,
                    out string validationRejection))
            {
                throw new InvalidDataException(validationRejection);
            }

            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "ServerManager.ClientMenuLogo"
            };
            if (!TryLoadPng(texture, data) ||
                texture.width != width ||
                texture.height != height)
            {
                throw new InvalidDataException(
                    "Unity could not decode the validated PNG dimensions.");
            }

            sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            sprite.name = "ServerManager.ClientMenuLogo";

            Sprite? oldLogo = _customLogo;
            Texture2D? oldTexture = _customLogoTexture;
            image.preserveAspect = true;
            image.sprite = sprite;
            _customLogo = sprite;
            _customLogoTexture = texture;
            sprite = null;
            texture = null;
            DestroyCustomLogo(oldLogo, oldTexture);
            _lastLogoWarning = string.Empty;
            ServerManagerPlugin.Log.LogInfo(
                $"Applied client menu logo ({width}x{height}).");
        }
        finally
        {
            // Includes reflection/decode/Sprite.Create exceptions, not only a
            // decoder returning false. Never leak an uncommitted GPU resource.
            DestroyCustomLogo(sprite, texture);
        }
    }

    private static void ReleaseCustomLogo(bool restoreVanilla)
    {
        CancelRemoteLogo();
        _remoteLogoAttemptedSource = null;
        _logoSource = null;
        if (restoreVanilla &&
            _logoImage != null &&
            _customLogo != null &&
            ReferenceEquals(_logoImage.sprite, _customLogo))
        {
            _logoImage.sprite = _vanillaLogo;
            _logoImage.preserveAspect = _vanillaLogoPreserveAspect;
        }

        DestroyCustomLogo(_customLogo, _customLogoTexture);
        _customLogo = null;
        _customLogoTexture = null;
        _logoImage = null;
        _vanillaLogo = null;
        _vanillaLogoPreserveAspect = false;
    }

    private static bool TryLoadPng(Texture2D texture, byte[] data)
    {
        MethodInfo? loadImage = AccessTools.Method(
            typeof(ImageConversion),
            "LoadImage",
            new[]
            {
                typeof(Texture2D),
                typeof(byte[]),
                typeof(bool)
            });
        if (loadImage == null)
        {
            return false;
        }

        object? result = loadImage.Invoke(
            null,
            new object[] { texture, data, true });
        return result is bool loaded && loaded;
    }

    private static void DestroyCustomLogo(
        Sprite? sprite,
        Texture2D? texture)
    {
        if (sprite != null)
        {
            UnityEngine.Object.Destroy(sprite);
        }

        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static void CancelFlow(bool clearConnectionPassword)
    {
        _armed = false;
        if (clearConnectionPassword)
        {
            ClearConnectionPasswordState();
        }
    }

    private static void ClearConnectionPasswordState()
    {
        ClearOwnedPassword();
        _pendingPassword = string.Empty;
        _passwordAwaitingHandshake = false;
        _brandedJoinDispatched = false;
    }

    private static void ClearOwnedPassword()
    {
        if (_passwordWasOverridden &&
            TryGetServerPassword(out string? currentPassword) &&
            string.Equals(
                currentPassword,
                _ownedPassword,
                StringComparison.Ordinal))
        {
            TrySetServerPassword(_previousPassword);
        }

        _ownedPassword = null;
        _previousPassword = null;
        _passwordWasOverridden = false;
    }

    private static void WarnOnce(
        ref string previousKey,
        string key,
        string message)
    {
        if (string.Equals(previousKey, key, StringComparison.Ordinal))
        {
            return;
        }

        previousKey = key;
        ServerManagerPlugin.Log.LogWarning(message);
    }
}

[HarmonyPatch(typeof(FejdStartup), "SetupGui")]
internal static class ClientMenuBrandingSetupGuiPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(FejdStartup __instance)
    {
        ClientMenuBranding.OnSetupGui(__instance);
    }
}

[HarmonyPatch(typeof(FejdStartup), "Update")]
internal static class ClientMenuBrandingUiUpdatePatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix() => ClientMenuBranding.BeforeUiUpdate();

    private static void Postfix(FejdStartup __instance)
    {
        ClientMenuBranding.OnUiUpdate(__instance);
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnStartGame))]
internal static class ClientMenuBrandingMainStartPatch
{
    private static void Prefix(FejdStartup __instance)
    {
        ClientMenuBranding.BeforeMainStart(__instance);
    }

    private static void Postfix(FejdStartup __instance)
    {
        ClientMenuBranding.AfterMainStart(__instance);
    }
}

[HarmonyPatch(typeof(FejdStartup), "UpdateCharacterList")]
internal static class ClientMenuBrandingCharacterListPatch
{
    private static void Postfix(FejdStartup __instance)
    {
        ClientMenuBranding.AfterCharacterListUpdated(__instance);
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnCharacterStart))]
internal static class ClientMenuBrandingCharacterStartPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(FejdStartup __instance)
    {
        ClientMenuBranding.BeforeCharacterStart(__instance);
    }

    private static Exception? Finalizer(
        FejdStartup __instance,
        Exception? __exception)
    {
        ClientMenuBranding.AfterCharacterStart(__instance);
        return __exception;
    }
}

[HarmonyPatch(typeof(FejdStartup), "TransitionToMainScene")]
internal static class ClientMenuBrandingTransitionPatch
{
    private static void Prefix(FejdStartup __instance)
    {
        ClientMenuBranding.BeforeTransitionToMainScene(__instance);
    }
}

[HarmonyPatch(typeof(FejdStartup), "OnSelelectCharacterBack")]
internal static class ClientMenuBrandingCharacterBackPatch
{
    private static void Postfix(FejdStartup __instance)
    {
        ClientMenuBranding.AfterCharacterSelectionBack(__instance);
    }
}

[HarmonyPatch(typeof(FejdStartup), "OnLanguageChange")]
internal static class ClientMenuBrandingLanguagePatch
{
    private static void Postfix(FejdStartup __instance)
    {
        ClientMenuBranding.AfterLanguageChanged(__instance);
    }
}

[HarmonyPatch(typeof(ZNet), "RPC_ClientHandshake")]
internal static class ClientMenuBrandingPasswordCleanupPatch
{
    private static void Prefix(ZNet __instance)
    {
        ClientMenuBranding.BeforeClientHandshake(__instance);
    }

    private static Exception? Finalizer(
        ZNet __instance,
        Exception? __exception)
    {
        ClientMenuBranding.AfterClientHandshake(__instance);
        return __exception;
    }
}
