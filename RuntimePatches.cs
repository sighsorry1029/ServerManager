using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using ServerManager.Events;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ServerManager;

// Only player-facing presentation uses localization. Diagnostics remain English.
internal static class PlayerConnectionMessages
{
    internal static string ExceptionKey(Exception exception) => exception switch
    {
        CharacterProtocolException protocol when protocol.PlayerMessageKey.Length != 0 => protocol.PlayerMessageKey,
        CharacterStorageException storage when storage.PlayerMessageKey.Length != 0 => storage.PlayerMessageKey,
        _ => "sm_character_apply_failed"
    };

    internal static string[] ExceptionArguments(Exception exception) => exception switch
    {
        CharacterProtocolException protocol => protocol.PlayerMessageArguments,
        CharacterStorageException storage => storage.PlayerMessageArguments,
        _ => Array.Empty<string>()
    };

    internal static string FromException(Exception exception) =>
        PlayerLocalizer.Text(ExceptionKey(exception), ExceptionArguments(exception));

    internal static string FromRejection(ProtocolRejection rejection)
    {
        // Never trust a remote diagnostic or message key to reveal detection details.
        if (rejection.Code == ProtocolRejectCode.CheatDetected ||
            rejection.Code == ProtocolRejectCode.DetectionProtocolViolation)
            return PlayerLocalizer.Text("sm_security_ended");

        string key = rejection.PlayerMessageKey;
        string[] arguments = rejection.PlayerMessageArguments;
        if (key == "sm_mod_mismatch" && arguments.Length == 3)
        {
            StringBuilder text = new(PlayerLocalizer.Text("sm_mod_mismatch"));
            string[] headings = { "sm_mod_install", "sm_mod_remove", "sm_mod_update" };
            bool hasActions = false;
            for (int index = 0; index < arguments.Length; ++index)
            {
                if (arguments[index].Length == 0) continue;
                text.Append('\n').Append(PlayerLocalizer.Text(headings[index], arguments[index]));
                hasActions = true;
            }
            text.Append('\n').Append(PlayerLocalizer.Text(hasActions ? "sm_mod_restart" : "sm_mod_report_invalid"));
            return text.ToString();
        }
        if (PlayerLocalizer.IsKnownMessage(key, arguments.Length))
            return PlayerLocalizer.Text(key, arguments);

        key = rejection.Code switch
        {
            ProtocolRejectCode.ProtocolVersionMismatch => "sm_version_mismatch",
            ProtocolRejectCode.ManifestRejected or ProtocolRejectCode.ManifestValidatorFailed => "sm_mod_report_invalid",
            ProtocolRejectCode.DuplicateConnection => "sm_duplicate_connection",
            ProtocolRejectCode.PeerIdentityUnavailable or ProtocolRejectCode.PeerInfoAuthenticationIncomplete => "sm_auth_failed",
            ProtocolRejectCode.ClientCharacterRejected => "sm_fresh_character_required",
            _ when (int)rejection.Code >= 1500 && (int)rejection.Code < 1700 => "sm_character_apply_failed",
            _ => "sm_connection_failed"
        };
        return PlayerLocalizer.Text(key);
    }
}

internal static class ConnectionErrorPanelPresentation
{
    internal const float PreferredPanelWidth = 675f;
    internal const float VerticalChromeHeight = 140f;
    internal const float ScreenMargin = 40f;
    internal const float MinimumFontSize = 15f;
    internal const float MaximumFontSize = 25f;

    // These are temporary overrides of a vanilla modal, not a replacement UI.
    // In particular, the original wooden image and OK action remain untouched.
    private sealed class RectState
    {
        internal RectState(RectTransform rect)
        {
            Rect = rect;
            Parent = rect.parent;
            SiblingIndex = rect.GetSiblingIndex();
            AnchorMin = rect.anchorMin;
            AnchorMax = rect.anchorMax;
            Pivot = rect.pivot;
            SizeDelta = rect.sizeDelta;
            Position = rect.anchoredPosition3D;
            Scale = rect.localScale;
            Rotation = rect.localRotation;
        }

        internal RectTransform Rect { get; }
        internal Transform? Parent { get; }
        internal int SiblingIndex { get; }
        internal Vector2 AnchorMin { get; }
        internal Vector2 AnchorMax { get; }
        internal Vector2 Pivot { get; }
        internal Vector2 SizeDelta { get; }
        internal Vector3 Position { get; }
        internal Vector3 Scale { get; }
        internal Quaternion Rotation { get; }

        internal void Restore()
        {
            if (Rect == null) return;
            Rect.SetParent(Parent, false);
            Rect.SetSiblingIndex(SiblingIndex);
            Rect.anchorMin = AnchorMin;
            Rect.anchorMax = AnchorMax;
            Rect.pivot = Pivot;
            Rect.sizeDelta = SizeDelta;
            Rect.anchoredPosition3D = Position;
            Rect.localScale = Scale;
            Rect.localRotation = Rotation;
        }
    }

    private sealed class LayoutState
    {
        internal LayoutState(FejdStartup startup, TMP_Text text, RectTransform background, RectTransform okButton)
        {
            Startup = startup;
            Text = text;
            Background = background;
            OkButton = okButton;
            TextRect = new RectState(text.rectTransform);
            BackgroundRect = new RectState(background);
            OkButtonRect = new RectState(okButton);
            BackgroundSize = background.rect.size;
            OkButtonSize = okButton.rect.size;
            BaseText = text.text ?? string.Empty;
            FontSize = text.fontSize;
            FontSizeMin = text.fontSizeMin;
            FontSizeMax = text.fontSizeMax;
            EnableAutoSizing = text.enableAutoSizing;
            WrappingMode = text.textWrappingMode;
            OverflowMode = text.overflowMode;
            RichText = text.richText;
            Alignment = text.alignment;
            Margin = text.margin;
            PageToDisplay = text.pageToDisplay;
            FirstVisibleCharacter = text.firstVisibleCharacter;
            MaxVisibleCharacters = text.maxVisibleCharacters;
            MaxVisibleWords = text.maxVisibleWords;
            MaxVisibleLines = text.maxVisibleLines;
            UseMaxVisibleDescender = text.useMaxVisibleDescender;
            Localization? localization = Localization.instance;
            if (localization != null &&
                ClientMenuBranding.TryGetLocalizationTextMeshStrings(localization, out Dictionary<TMP_Text, string> cache) &&
                cache.TryGetValue(text, out string source))
                LocalizationSource = source;
        }

        internal FejdStartup Startup { get; }
        internal TMP_Text Text { get; }
        internal RectTransform Background { get; }
        internal RectTransform OkButton { get; }
        internal RectState TextRect { get; }
        internal RectState BackgroundRect { get; }
        internal RectState OkButtonRect { get; }
        internal Vector2 BackgroundSize { get; }
        internal Vector2 OkButtonSize { get; }
        internal string BaseText { get; }
        internal string? LocalizationSource { get; }
        internal string AppliedText { get; set; } = string.Empty;
        internal float FontSize { get; }
        internal float FontSizeMin { get; }
        internal float FontSizeMax { get; }
        internal bool EnableAutoSizing { get; }
        internal TextWrappingModes WrappingMode { get; }
        internal TextOverflowModes OverflowMode { get; }
        internal bool RichText { get; }
        internal TextAlignmentOptions Alignment { get; }
        internal Vector4 Margin { get; }
        internal int PageToDisplay { get; }
        internal int FirstVisibleCharacter { get; }
        internal int MaxVisibleCharacters { get; }
        internal int MaxVisibleWords { get; }
        internal int MaxVisibleLines { get; }
        internal bool UseMaxVisibleDescender { get; }
        internal Vector2 AvailableSize { get; set; }
        internal int PageCount { get; set; } = 1;
        internal GameObject? Pager { get; set; }
        internal Button? Previous { get; set; }
        internal Button? Next { get; set; }
        internal TMP_Text? PageLabel { get; set; }
    }

    private static LayoutState? _activeLayout;

    internal static string ComposeMessage(string? baseText, string? detail)
    {
        string first = baseText?.TrimEnd() ?? string.Empty;
        string second = detail?.Trim() ?? string.Empty;
        if (first.Length == 0) return second;
        if (second.Length == 0) return first;
        return first + "\n\n" + second;
    }

    internal static float CalculatePanelHeight(float originalHeight, float renderedTextHeight, float availableHeight)
    {
        float original = IsFiniteNonNegative(originalHeight) ? originalHeight : 0f;
        float rendered = IsFiniteNonNegative(renderedTextHeight) ? renderedTextHeight : 0f;
        float desired = Mathf.Max(original, rendered + VerticalChromeHeight);
        // A smaller window must also be allowed to shrink the original prefab.
        return IsFinite(availableHeight) && availableHeight > 0f
            ? Mathf.Min(desired, availableHeight)
            : desired;
    }

    internal static int CalculatePageNumber(int current, int delta, int pageCount) =>
        (int)Math.Max(1L, Math.Min(Math.Max(1, pageCount), (long)current + delta));

    internal static void BeforeShow(FejdStartup startup) => RestoreActiveLayout();

    internal static void AfterShow(FejdStartup startup)
    {
        if (startup == null || startup.m_connectionFailedPanel == null ||
            !startup.m_connectionFailedPanel.activeSelf || startup.m_connectionFailedError == null ||
            string.IsNullOrWhiteSpace(ServerManagerPlugin.ConnectionError))
            return;

        string detail = ServerManagerPlugin.ConnectionError;
        try
        {
            TMP_Text text = startup.m_connectionFailedError;
            Transform? imageTransform = startup.m_connectionFailedPanel.transform.Find("Image");
            RectTransform? background = imageTransform?.GetComponent<RectTransform>();
            RectTransform? okButton = imageTransform?.Find("ButtonOk")?.GetComponent<RectTransform>();
            if (background == null || okButton == null)
                throw new InvalidOperationException("The vanilla connection-error Image/ButtonOk is unavailable.");

            LayoutState layout = new(startup, text, background, okButton);
            _activeLayout = layout;
            // FejdStartup re-localizes visible cached TMP labels every Update.
            // Detach only our temporary text, then restore its cache entry on close.
            Localization.instance?.RemoveTextFromCache(text);
            text.enableAutoSizing = false;
            text.fontSizeMin = MinimumFontSize;
            text.fontSizeMax = MaximumFontSize;
            text.fontSize = MaximumFontSize;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Page;
            text.richText = false;
            text.margin = Vector4.zero;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.firstVisibleCharacter = 0;
            text.maxVisibleCharacters = int.MaxValue;
            text.maxVisibleWords = int.MaxValue;
            text.maxVisibleLines = int.MaxValue;
            text.useMaxVisibleDescender = false;
            text.pageToDisplay = 1;
            layout.AppliedText = ComposeMessage(layout.BaseText, detail);
            text.text = layout.AppliedText;
            Reflow(layout);
            // Consume only after the complete message has a usable presentation.
            ServerManagerPlugin.ConnectionError = string.Empty;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            RestoreActiveLayout();
            ServerManagerPlugin.Log.LogWarning(
                "Could not lay out the ServerManager connection error panel: " + exception.Message);
        }
    }

    private static void Reflow(LayoutState layout)
    {
        TMP_Text text = layout.Text;
        int previousCharacter = 0;
        if (text.textInfo.pageCount > 0)
        {
            int oldPage = Mathf.Clamp(text.pageToDisplay - 1, 0, text.textInfo.pageCount - 1);
            previousCharacter = text.textInfo.pageInfo[oldPage].firstCharacterIndex;
        }

        Vector2 available = GetAvailableSize(layout);
        layout.AvailableSize = available;
        float width = Mathf.Min(Mathf.Max(layout.BackgroundSize.x, PreferredPanelWidth), available.x);
        float padding = Mathf.Min(24f, width * 0.05f);
        float textWidth = Mathf.Max(1f, width - padding * 2f);
        text.fontSize = MaximumFontSize;
        // Reserve at least one complete line. TMP Page can discard text if even
        // the first glyph cannot fit vertically (e.g. a very small resized window).
        float lineHeight = text.GetPreferredValues("Ag", textWidth, float.PositiveInfinity).y + 4f;
        if (lineHeight > available.y * 0.5f)
        {
            text.fontSize = Mathf.Max(1f, Mathf.Min(MinimumFontSize, MaximumFontSize * available.y * 0.5f / lineHeight));
            lineHeight = text.GetPreferredValues("Ag", textWidth, float.PositiveInfinity).y + 4f;
        }
        float chrome = Mathf.Min(VerticalChromeHeight, Mathf.Max(0f, available.y - lineHeight));
        float controlScale = chrome / VerticalChromeHeight;
        Vector2 preferred = text.GetPreferredValues(layout.AppliedText, textWidth, float.PositiveInfinity);
        float height = CalculatePanelHeight(layout.BackgroundSize.y, preferred.y + 4f, available.y);
        SetCenteredRect(layout.Background, width, height);

        // Resize the actual text rectangle, not just the decorative background.
        RectTransform body = text.rectTransform;
        body.SetParent(layout.Background, false);
        body.anchoredPosition3D = Vector3.zero;
        body.localScale = Vector3.one;
        body.localRotation = Quaternion.identity;
        body.anchorMin = Vector2.zero;
        body.anchorMax = Vector2.one;
        body.pivot = new Vector2(0.5f, 0.5f);
        body.offsetMin = new Vector2(padding, 116f * controlScale);
        body.offsetMax = new Vector2(-padding, -24f * controlScale);

        layout.OkButton.SetParent(layout.Background, false);
        float okWidth = Mathf.Min(Mathf.Max(100f, layout.OkButtonSize.x), width - padding * 2f);
        float okHeight = Mathf.Clamp(layout.OkButtonSize.y, 28f, 45f);
        SetBottomRect(layout.OkButton, new Vector2(okWidth, okHeight),
            new Vector2(0f, 40f * controlScale), controlScale);

        // First render page 1: TMP must build its pageInfo array before selecting
        // a later page (the array grows dynamically, including beyond 32 pages).
        text.pageToDisplay = 1;
        layout.Background.ForceUpdateRectTransforms();
        text.ForceMeshUpdate(false, false);
        layout.PageCount = Mathf.Max(1, text.textInfo.pageCount);
        if (layout.PageCount > 1 && layout.Pager == null) CreatePager(layout);
        if (layout.Pager != null)
        {
            layout.Pager.SetActive(layout.PageCount > 1);
            SetBottomRect((RectTransform)layout.Pager.transform,
                new Vector2(240f, 32f), new Vector2(0f, 88f * controlScale),
                Mathf.Min(controlScale, textWidth / 240f));
        }
        int page = 1;
        for (int index = 0; index < text.textInfo.pageCount; ++index)
        {
            if (text.textInfo.pageInfo[index].firstCharacterIndex > previousCharacter) break;
            page = index + 1;
        }
        ChangePage(layout, page - 1);
    }

    private static void SetCenteredRect(RectTransform rect, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition3D = Vector3.zero;
        rect.localScale = Vector3.one;
        rect.localRotation = Quaternion.identity;
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void SetBottomRect(RectTransform rect, Vector2 size, Vector2 position, float scale)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition3D = new Vector3(position.x, position.y, 0f);
        rect.localScale = Vector3.one * scale;
        rect.localRotation = Quaternion.identity;
        rect.sizeDelta = size;
    }

    private static void CreatePager(LayoutState layout)
    {
        // Do not clone ButtonOk: cloned persistent click listeners can dismiss
        // the modal when the user meant to turn a page.
        GameObject pager = new("ServerManagerNoticePages", typeof(RectTransform));
        layout.Pager = pager;
        pager.transform.SetParent(layout.Background, false);
        Button? template = layout.OkButton.GetComponent<Button>();
        layout.Previous = CreatePageButton(layout, template, "<", -90f, -1);
        layout.Next = CreatePageButton(layout, template, ">", 90f, 1);
        layout.PageLabel = CreatePageLabel(layout, pager.transform, string.Empty);
        SetCenteredRect(layout.PageLabel.rectTransform, 110f, 32f);
    }

    private static Button CreatePageButton(LayoutState layout, Button? template, string label, float x, int delta)
    {
        GameObject target = new("Page" + (delta < 0 ? "Previous" : "Next"),
            typeof(RectTransform), typeof(Image), typeof(Button));
        target.transform.SetParent(layout.Pager!.transform, false);
        SetCenteredRect((RectTransform)target.transform, 60f, 32f);
        ((RectTransform)target.transform).anchoredPosition = new Vector2(x, 0f);
        Image image = target.GetComponent<Image>();
        Image? source = layout.OkButton.GetComponent<Image>();
        if (source != null)
        {
            image.sprite = source.sprite;
            image.type = source.type;
            image.color = source.color;
            image.material = source.material;
            image.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
        }
        Button button = target.GetComponent<Button>();
        button.targetGraphic = image;
        if (template != null)
        {
            button.colors = template.colors;
            button.transition = template.transition;
            button.spriteState = template.spriteState;
        }
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => ChangePage(layout, delta));
        TMP_Text caption = CreatePageLabel(layout, target.transform, label);
        SetCenteredRect(caption.rectTransform, 56f, 30f);
        return button;
    }

    private static TMP_Text CreatePageLabel(LayoutState layout, Transform parent, string value)
    {
        GameObject target = new("PageLabel", typeof(RectTransform));
        // Set the vanilla font before TMP Awake, even when the pager is visible.
        target.SetActive(false);
        target.transform.SetParent(parent, false);
        TMP_Text label = target.AddComponent<TextMeshProUGUI>();
        TMP_Text source = layout.OkButton.GetComponentInChildren<TMP_Text>(true) ?? layout.Text;
        label.font = source.font;
        label.fontSharedMaterial = source.fontSharedMaterial;
        label.fontSize = 20f;
        label.color = source.color;
        label.richText = false;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        label.text = value;
        target.SetActive(true);
        return label;
    }

    private static void ChangePage(LayoutState layout, int delta)
    {
        if (!ReferenceEquals(layout, _activeLayout) || layout.Text == null) return;
        int page = CalculatePageNumber(layout.Text.pageToDisplay, delta, layout.PageCount);
        layout.Text.pageToDisplay = page;
        layout.Text.ForceMeshUpdate(false, false);
        if (layout.PageLabel != null) layout.PageLabel.text = page + " / " + layout.PageCount;
        if (layout.Previous != null) layout.Previous.interactable = page > 1;
        if (layout.Next != null) layout.Next.interactable = page < layout.PageCount;
    }

    internal static void Tick(FejdStartup startup)
    {
        LayoutState? layout = _activeLayout;
        if (layout == null || !ReferenceEquals(layout.Startup, startup)) return;
        if (startup.m_connectionFailedPanel == null || !startup.m_connectionFailedPanel.activeInHierarchy ||
            layout.Text == null || layout.Background == null || layout.OkButton == null)
        {
            RestoreActiveLayout();
            return;
        }
        try
        {
            if ((GetAvailableSize(layout) - layout.AvailableSize).sqrMagnitude > 0.25f)
                Reflow(layout);
            if (layout.PageCount <= 1) return;
            float wheel = ZInput.GetMouseScrollWheel();
            if (ZInput.GetKeyDown(KeyCode.PageDown, true) || ZInput.GetButtonDown("JoyRBumper") || wheel < 0f)
                ChangePage(layout, 1);
            else if (ZInput.GetKeyDown(KeyCode.PageUp, true) || ZInput.GetButtonDown("JoyLBumper") || wheel > 0f)
                ChangePage(layout, -1);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            RestoreActiveLayout();
            ServerManagerPlugin.Log.LogWarning("Connection error panel update failed: " + exception.Message);
        }
    }

    internal static void BeforeStartupDestroyed(FejdStartup startup)
    {
        if (_activeLayout != null && ReferenceEquals(_activeLayout.Startup, startup)) RestoreActiveLayout();
    }

    internal static void AfterAcknowledged(FejdStartup startup)
    {
        if (_activeLayout != null && ReferenceEquals(_activeLayout.Startup, startup)) RestoreActiveLayout();
    }

    internal static void Shutdown() => RestoreActiveLayout();

    private static Vector2 GetAvailableSize(LayoutState layout)
    {
        RectTransform? parent = layout.Background.parent as RectTransform;
        Vector2 size = parent == null ? layout.BackgroundSize : parent.rect.size;
        if (!IsFinite(size.x) || size.x <= 0f) size.x = Mathf.Max(1f, layout.BackgroundSize.x);
        if (!IsFinite(size.y) || size.y <= 0f) size.y = Mathf.Max(1f, layout.BackgroundSize.y);
        // Reduce the margin, not the viewport bound, for very small windows.
        return new Vector2(
            size.x - Mathf.Min(ScreenMargin, size.x * 0.1f),
            size.y - Mathf.Min(ScreenMargin, size.y * 0.1f));
    }

    private static bool IsFiniteNonNegative(float value) => IsFinite(value) && value >= 0f;
    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void RestoreActiveLayout()
    {
        LayoutState? layout = _activeLayout;
        _activeLayout = null;
        if (layout == null) return;
        try
        {
            if (layout.Pager != null)
            {
                layout.Pager.SetActive(false);
                UnityEngine.Object.Destroy(layout.Pager);
            }
            if (layout.Text != null)
            {
                if (string.Equals(layout.Text.text, layout.AppliedText, StringComparison.Ordinal))
                    layout.Text.text = layout.BaseText;
                layout.Text.fontSizeMin = layout.FontSizeMin;
                layout.Text.fontSizeMax = layout.FontSizeMax;
                layout.Text.enableAutoSizing = layout.EnableAutoSizing;
                layout.Text.fontSize = layout.FontSize;
                layout.Text.textWrappingMode = layout.WrappingMode;
                layout.Text.overflowMode = layout.OverflowMode;
                layout.Text.richText = layout.RichText;
                layout.Text.alignment = layout.Alignment;
                layout.Text.margin = layout.Margin;
                layout.Text.pageToDisplay = layout.PageToDisplay;
                layout.Text.firstVisibleCharacter = layout.FirstVisibleCharacter;
                layout.Text.maxVisibleCharacters = layout.MaxVisibleCharacters;
                layout.Text.maxVisibleWords = layout.MaxVisibleWords;
                layout.Text.maxVisibleLines = layout.MaxVisibleLines;
                layout.Text.useMaxVisibleDescender = layout.UseMaxVisibleDescender;
                if (layout.LocalizationSource != null && Localization.instance != null &&
                    ClientMenuBranding.TryGetLocalizationTextMeshStrings(Localization.instance, out Dictionary<TMP_Text, string> cache))
                    cache[layout.Text] = layout.LocalizationSource;
            }
            layout.TextRect.Restore();
            layout.OkButtonRect.Restore();
            layout.BackgroundRect.Restore();
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Connection error panel cleanup was already unavailable: " + exception.Message);
        }
    }
}

[HarmonyPatch(typeof(ZNet), "Start")]
internal static class SteamworksBackendStartupPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ZNet __instance)
    {
        return ServerManagerRuntime.BeforeNetworkStart(__instance);
    }
}

[HarmonyPatch(typeof(Game), "Start")]
internal static class LocalHostCharacterStartPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Game __instance)
    {
        ServerManagerRuntime.BeforeLocalHostGameplay(__instance);
    }
}

[HarmonyPatch(typeof(Game), "FixedUpdate")]
internal static class LocalHostCharacterSpawnGatePatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Game __instance)
    {
        return ServerManagerRuntime.BeforeLocalHostGameplay(__instance);
    }
}

[HarmonyPatch(typeof(Game), "Shutdown", new[] { typeof(bool) })]
internal static class LocalHostCharacterShutdownPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(ref bool __0)
    {
        ServerManagerRuntime.BeforeGameShutdown(ref __0);
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.OpenServer))]
internal static class SteamworksServerListenerPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ZNet __instance)
    {
        return ServerManagerRuntime.BeforeServerOpen(__instance);
    }
}

[HarmonyPatch(typeof(ZNet), "OnNewConnection")]
internal static class NewConnectionPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ZNet __instance, ZNetPeer peer)
    {
        return ServerManagerRuntime.BeforeNewConnection(__instance, peer);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ZNet __instance, ZNetPeer peer)
    {
        ServerManagerRuntime.AfterNewConnection(__instance, peer);
    }
}

[HarmonyPatch(
    typeof(ZSteamMatchmaking),
    nameof(ZSteamMatchmaking.VerifySessionTicket),
    new[] { typeof(byte[]), typeof(CSteamID) })]
internal static class SteamTicketVerificationObservationPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(CSteamID steamID)
    {
        // Observe only the connection-bound ID. Never parse/store the ticket
        // bytes and never call BeginAuthSession a second time.
        ServerManagerRuntime.BeforeVanillaSteamTicketVerification(steamID);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CSteamID steamID, bool __result)
    {
        ServerManagerRuntime.AfterVanillaSteamTicketVerification(
            steamID,
            __result);
    }

    private static Exception? Finalizer(
        CSteamID steamID,
        Exception? __exception)
    {
        if (__exception != null)
        {
            ServerManagerRuntime.AfterVanillaSteamTicketVerificationFaulted(
                steamID);
        }

        return __exception;
    }
}

[HarmonyPatch(
    typeof(ZNet),
    "SendPeerInfo",
    new[] { typeof(ZRpc), typeof(string) })]
internal static class ClientPeerInfoPatch
{
    private static bool Prefix(
        ZNet __instance,
        ZRpc rpc,
        string password)
    {
        return ServerManagerRuntime.BeforeClientSendPeerInfo(
            __instance,
            rpc,
            password);
    }
}

[HarmonyPatch(
    typeof(ZNet),
    "RPC_PeerInfo",
    new[] { typeof(ZRpc), typeof(ZPackage) })]
internal static class ServerPeerInfoPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new(instructions);
        MethodInfo countPlayers = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetNrOfPlayers));
        MethodInfo configuredLimit = AccessTools.Method(typeof(ServerManagerRuntime),
            nameof(ServerManagerRuntime.GetServerPlayerLimit));
        int limitIndex = -1;
        for (int index = 0; index + 2 < code.Count; ++index)
        {
            if (!code[index].Calls(countPlayers) || !code[index + 1].LoadsConstant(10L) ||
                (code[index + 2].opcode != OpCodes.Blt && code[index + 2].opcode != OpCodes.Blt_S))
                continue;
            if (limitIndex >= 0)
                throw new InvalidOperationException("ServerManager found multiple Valheim player-limit checks.");
            limitIndex = index + 1;
        }
        if (limitIndex < 0)
            throw new InvalidOperationException(
                "ServerManager could not locate the vanilla 10-player admission check. " +
                "Remove overlapping player-limit mods and check the supported Valheim version.");

        // Change only the operand of vanilla's full-server comparison. Keep its
        // count, Error(9), password/auth flow, branch labels and exception blocks.
        code[limitIndex].opcode = OpCodes.Call;
        code[limitIndex].operand = configuredLimit;
        return code;
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(
        ZNet __instance,
        ZRpc rpc,
        ZPackage pkg,
        out ServerManagerRuntime.PeerInfoPatchState __state)
    {
        return ServerManagerRuntime.BeforeServerPeerInfo(
            __instance,
            rpc,
            pkg,
            out __state);
    }

    private static void Postfix(
        ZNet __instance,
        ZRpc rpc,
        ServerManagerRuntime.PeerInfoPatchState? __state)
    {
        ServerManagerRuntime.AfterServerPeerInfo(
            __instance,
            rpc,
            __state);
    }

    private static Exception? Finalizer(
        ZNet __instance,
        ZRpc rpc,
        ServerManagerRuntime.PeerInfoPatchState? __state,
        Exception? __exception)
    {
        if (__exception == null)
        {
            return null;
        }

        Exception root =
            __exception is TargetInvocationException &&
            __exception.InnerException != null
                ? __exception.InnerException
                : __exception;
        bool expectedNetworkDecodeFailure =
            root is EndOfStreamException ||
            root is InvalidDataException ||
            root is DecoderFallbackException ||
            root is ArgumentOutOfRangeException ||
            root is IndexOutOfRangeException;

        if (__state?.IsServerPeerInfo == true &&
            expectedNetworkDecodeFailure &&
            !IntegrityCanonical.IsFatal(root))
        {
            ServerManagerRuntime.HandlePeerInfoException(
                __instance,
                rpc,
                __state,
                root);
            return null;
        }

        if (__state?.IsServerPeerInfo == true)
        {
            ServerManagerRuntime.AbortPeerInfoAuthentication(__state);
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(SteamGameServer), nameof(SteamGameServer.SetMaxPlayerCount))]
internal static class SteamServerPlayerLimitPatch
{
    private static void Prefix(ref int __0)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer() &&
            ZNet.m_onlineBackend == OnlineBackendType.Steamworks)
            __0 = ServerManagerRuntime.GetServerPlayerLimit();
    }
}

[HarmonyPatch]
internal static class SteamLobbyPlayerLimitPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Only the client/listen build owns this Steam lobby callback. The
        // dedicated binary may omit it and uses SteamGameServer above instead.
        MethodInfo? callback = AccessTools.Method(typeof(ZSteamMatchmaking), "OnLobbyCreated",
            new[] { typeof(LobbyCreated_t), typeof(bool) });
        if (callback != null) yield return callback;
    }

    private static void Postfix()
    {
        // Read the latest setting, including edits while CreateLobby was pending.
        ServerManagerRuntime.RefreshServerPlayerLimitAdvertisement();
    }
}

[HarmonyPatch(
    typeof(Game),
    nameof(Game.SavePlayerProfile),
    new[] { typeof(bool) })]
internal static class ManagedCharacterSavePatch
{
    private static bool Prefix()
    {
        return ServerManagerRuntime.AllowLocalHostSave();
    }

    private static void Postfix(Game __instance)
    {
        ServerManagerRuntime.AfterGameSave(__instance);
    }
}

[HarmonyPatch(typeof(ZNet), "LoadWorld")]
internal static class ServerEventWorldReadyPatch
{
    private static void Postfix()
    {
        ServerEventRuntime.OnWorldReady();
    }
}

[HarmonyPatch(typeof(ZNet), "SaveWorld")]
internal static class ServerEventWorldSavePatch
{
    private static bool Prefix()
    {
        if (!ServerManagerRuntime.AllowLocalHostSave()) return false;
        try
        {
            ServerManagerRuntime.BeforeWorldSaveInvocation();
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            try
            {
                ServerManagerPlugin.Log.LogError(
                    "World-save observation initialization failed. The vanilla " +
                    "save will continue: " + exception);
            }
            catch (Exception loggingException) when (
                !IntegrityCanonical.IsFatal(loggingException))
            {
                // Observation and logging are both non-blocking for SaveWorld.
            }
        }
        return true;
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception != null)
        {
            try
            {
                ServerManagerRuntime.AfterWorldSaveInvocationFailed(__exception);
            }
            catch (Exception observationException) when (
                !IntegrityCanonical.IsFatal(observationException))
            {
                try
                {
                    ServerManagerPlugin.Log.LogError(
                        "World-save failure observation also failed: " +
                        observationException);
                }
                catch (Exception loggingException) when (
                    !IntegrityCanonical.IsFatal(loggingException))
                {
                    // Preserve Valheim's original exception.
                }
            }
        }

        return __exception;
    }

    /// <summary>
    /// SaveWorld joins its previous worker before it prepares the next ZDO
    /// snapshot. Observe precisely at that boundary, but never branch around
    /// or cancel Valheim's world save.
    /// </summary>
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new(instructions);
        MethodInfo? gate = AccessTools.Method(
            typeof(ServerManagerRuntime),
            nameof(ServerManagerRuntime.BeforeWorldSnapshotPrepared));
        if (gate == null)
        {
            throw new MissingMethodException(
                typeof(ServerManagerRuntime).FullName,
                nameof(ServerManagerRuntime.BeforeWorldSnapshotPrepared));
        }

        int prepareCallIndex = -1;
        int anchorCount = 0;
        for (int index = 0; index < code.Count; ++index)
        {
            if ((code[index].opcode == OpCodes.Call ||
                 code[index].opcode == OpCodes.Callvirt) &&
                code[index].operand is MethodInfo method &&
                method.DeclaringType == typeof(ZDOMan) &&
                string.Equals(
                    method.Name,
                    "PrepareSave",
                    StringComparison.Ordinal) &&
                method.GetParameters().Length == 0)
            {
                ++anchorCount;
                prepareCallIndex = index;
            }
        }

        int insertionIndex = prepareCallIndex - 2;
        if (anchorCount != 1 || insertionIndex < 0 ||
            code[insertionIndex].opcode != OpCodes.Ldarg_0 ||
            code[insertionIndex + 1].opcode != OpCodes.Ldfld ||
            code[insertionIndex + 1].operand is not FieldInfo zdoManField ||
            zdoManField.DeclaringType != typeof(ZNet) ||
            zdoManField.FieldType != typeof(ZDOMan))
        {
            throw new InvalidOperationException(
                "Could not locate the unique ZNet.SaveWorld boundary " +
                "immediately before ZDOMan.PrepareSave. Refusing to apply an " +
                "unsafe character-checkpoint observation patch.");
        }

        List<Label> incomingLabels =
            new List<Label>(code[insertionIndex].labels);
        List<ExceptionBlock> incomingBlocks =
            new List<ExceptionBlock>(code[insertionIndex].blocks);
        code[insertionIndex].labels.Clear();
        code[insertionIndex].blocks.Clear();
        CodeInstruction callGate = new CodeInstruction(OpCodes.Call, gate);
        callGate.labels.AddRange(incomingLabels);
        callGate.blocks.AddRange(incomingBlocks);
        code.Insert(insertionIndex, callGate);
        return code;
    }
}

[HarmonyPatch(typeof(ZNet), "SaveWorldThread")]
internal static class VerifiedWorldSaveWorkerPatch
{
    private const string PrimarySaveSuccessLogPrefix = "World saved ( ";

    private static void Prefix()
    {
        try
        {
            ServerManagerRuntime.BeforeWorldSaveWorker();
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            TryLogObservationFailure(
                "World-save worker binding observation failed: " + exception);
        }
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        try
        {
            ServerManagerRuntime.AfterWorldSaveWorker();
        }
        catch (Exception observationException) when (
            !IntegrityCanonical.IsFatal(observationException))
        {
            TryLogObservationFailure(
                "World-save worker completion observation failed: " +
                observationException);
        }

        return __exception;
    }

    private static void TryLogObservationFailure(string message)
    {
        try
        {
            ServerManagerPlugin.Log.LogError(message);
        }
        catch (Exception loggingException) when (
            !IntegrityCanonical.IsFatal(loggingException))
        {
            // Preserve the worker's original result and exception.
        }
    }

    /// <summary>
    /// Valheim catches SaveWorldThread exceptions internally, so a Harmony
    /// finalizer cannot distinguish a successful primary save from the catch
    /// path. Mark success only after the game's primary DB/metadata success log
    /// and before optional auto-backup work begins.
    /// </summary>
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new(instructions);
        MethodInfo? successMarker = AccessTools.Method(
            typeof(ServerManagerRuntime),
            nameof(ServerManagerRuntime.MarkWorldSaveWorkerSucceeded));
        if (successMarker == null)
        {
            throw new MissingMethodException(
                typeof(ServerManagerRuntime).FullName,
                nameof(ServerManagerRuntime.MarkWorldSaveWorkerSucceeded));
        }

        int anchorCount = 0;
        int insertionIndex = -1;
        for (int index = 0; index < code.Count; ++index)
        {
            if (code[index].opcode != OpCodes.Ldstr ||
                !string.Equals(
                    code[index].operand as string,
                    PrimarySaveSuccessLogPrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }

            ++anchorCount;
            for (int scan = index + 1; scan < code.Count; ++scan)
            {
                if ((code[scan].opcode == OpCodes.Call ||
                     code[scan].opcode == OpCodes.Callvirt) &&
                    code[scan].operand is MethodInfo method &&
                    method.DeclaringType == typeof(ZLog) &&
                    string.Equals(method.Name, "Log", StringComparison.Ordinal))
                {
                    // The primary DB/metadata replacement has succeeded before
                    // Valheim builds this log message. Mark immediately before
                    // ZLog.Log so a logging patch failure cannot turn an
                    // already-durable world into a false negative.
                    insertionIndex = scan;
                    break;
                }
            }
        }

        if (anchorCount != 1 || insertionIndex < 0)
        {
            throw new InvalidOperationException(
                "ServerManager could not locate the verified Valheim world-save " +
                "success seam. This game build is not compatible with the " +
                "checkpoint patch.");
        }

        code.Insert(
            insertionIndex,
            new CodeInstruction(OpCodes.Call, successMarker));
        return code;
    }
}

[HarmonyPatch(typeof(Chat), "OnNewChatMessage")]
internal static class ServerEventChatPatch
{
    private static void Postfix(
        long senderID,
        Talker.Type type,
        UserInfo sender,
        string text)
    {
        if ((type == Talker.Type.Shout || type == Talker.Type.Normal ||
             type == Talker.Type.Whisper) && ZNet.instance != null &&
            senderID == ZNet.instance.LocalPlayerCharacterID.UserID)
        {
            ServerManagerRuntime.ReportLocalChat(type, text);
        }
    }
}

[HarmonyPatch(typeof(Player), "OnDeath")]
internal static class ServerEventPlayerDeathPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Player __instance)
    {
        ServerManagerRuntime.ReportLocalDeath(__instance);
    }
}

[HarmonyPatch(typeof(Character), "OnDeath")]
internal static class ServerEventBossDeathPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Character __instance)
    {
        ServerManagerRuntime.ReportLocalBossKill(__instance);
    }
}

[HarmonyPatch(
    typeof(Game),
    "ContinueLogout",
    new[]
    {
        typeof(bool),
        typeof(bool),
        typeof(bool)
    })]
internal static class ManagedCharacterLogoutDrainPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(
        Game __instance,
        bool save,
        bool shouldExit,
        bool changeToStartScene)
    {
        return ServerManagerRuntime.BeforeContinueLogout(
            __instance,
            save,
            shouldExit,
            changeToStartScene);
    }
}

[HarmonyPatch(typeof(Inventory), "Changed")]
internal static class ManagedCharacterInventoryDirtyPatch
{
    private static void Postfix(Inventory __instance)
    {
        ServerManagerRuntime.AfterInventoryChanged(__instance);
    }
}

[HarmonyPatch]
internal static class MaximumDamageLimitPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] damageTargets =
        {
            typeof(Character),
            typeof(WearNTear),
            typeof(MineRock5),
            typeof(Destructible),
            typeof(TreeLog),
            typeof(TreeBase)
        };

        foreach (Type target in damageTargets)
        {
            MethodInfo? method = AccessTools.Method(
                target,
                "Damage",
                new[] { typeof(HitData) });
            if (method == null)
            {
                throw new MissingMethodException(
                    target.FullName,
                    "Damage(HitData)");
            }

            yield return method;
        }
    }

    // Inspect the HitData after ordinary high-priority compatibility prefixes
    // have adjusted it, but before the vanilla method serializes RPC_Damage.
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(object __instance, HitData __0)
    {
        return ServerManagerRuntime.BeforeLocalPlayerDamage(__instance, __0);
    }
}

[HarmonyPatch(
    typeof(ZRoutedRpc),
    "RPC_RoutedRPC",
    new[] { typeof(ZRpc), typeof(ZPackage) })]
internal static class ServerRoutedDamageLimitPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ZRpc rpc, ZPackage pkg)
    {
        return ServerManagerRuntime.BeforeServerRoutedRpcDamage(rpc, pkg);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.Save), new[] { typeof(ZPackage) })]
internal static class ManagedCharacterPlayerSavePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(Player __instance) => CharacterPoisonPersistence.Capture(__instance);
}

[HarmonyPatch(
    typeof(Player),
    nameof(Player.Load),
    new[] { typeof(ZPackage) })]
internal static class ManagedCharacterPlayerLoadPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Player __instance, out bool __state)
    {
        __state = ServerManagerRuntime.BeforePlayerLoad(__instance);
        CharacterPoisonPersistence.BeforeLoad(__instance);
    }

    [HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(
        Player __instance,
        bool __state,
        bool __runOriginal,
        Exception? __exception)
    {
        try
        {
            CharacterPoisonPersistence.AfterLoad(__instance, __runOriginal && __exception == null);
        }
        finally
        {
            ServerManagerRuntime.AfterPlayerLoad(__state);
        }
        return __exception;
    }
}

[HarmonyPatch]
internal static class OperationalKickEntryPointPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(
            typeof(ZNet),
            nameof(ZNet.Kick),
            new[] { typeof(string) }) ??
            throw new MissingMethodException(typeof(ZNet).FullName, "Kick(string)");
        yield return AccessTools.Method(
            typeof(ZNet),
            "RPC_Kick",
            new[] { typeof(ZRpc), typeof(string) }) ??
            throw new MissingMethodException(typeof(ZNet).FullName, "RPC_Kick");
    }

    // Patch only the explicit console/admin kick call sites. Both overloads
    // of InternalKick are also used by ban/allowlist enforcement, so neither
    // may be intercepted globally. RPC_Kick's authorization and the client
    // branch of Kick remain untouched; a declined request runs vanilla code.
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator,
        MethodBase original)
    {
        List<CodeInstruction> code = new(instructions);
        MethodInfo? internalKick = AccessTools.Method(
            typeof(ZNet),
            "InternalKick",
            new[] { typeof(string) });
        MethodInfo? beginKick = AccessTools.Method(
            typeof(OperationalKickEntryPointPatch),
            nameof(TryBeginOperationalKickByUser));
        if (internalKick == null || beginKick == null)
        {
            throw new MissingMethodException(
                "The operational-kick call-site dependency is unavailable.");
        }

        OpCode loadUser = original.Name == nameof(ZNet.Kick)
            ? OpCodes.Ldarg_1
            : OpCodes.Ldarg_2;
        int callIndex = -1;
        int anchorCount = 0;
        for (int index = 0; index < code.Count; ++index)
        {
            if ((code[index].opcode == OpCodes.Call ||
                 code[index].opcode == OpCodes.Callvirt) &&
                Equals(code[index].operand, internalKick))
            {
                callIndex = index;
                ++anchorCount;
            }
        }

        int insertionIndex = callIndex - 2;
        if (anchorCount != 1 || insertionIndex < 0 ||
            callIndex + 1 >= code.Count ||
            code[insertionIndex].opcode != OpCodes.Ldarg_0 ||
            code[insertionIndex + 1].opcode != loadUser ||
            code[insertionIndex + 1].labels.Count != 0 ||
            code[insertionIndex + 1].blocks.Count != 0 ||
            code[callIndex].labels.Count != 0 ||
            code[callIndex].blocks.Count != 0)
        {
            throw new InvalidOperationException(
                "Could not locate the unique operational kick call site in " +
                original.Name + ". Refusing an unsafe kick patch.");
        }

        Label afterKick = generator.DefineLabel();
        code[callIndex + 1].labels.Add(afterKick);
        CodeInstruction loadServer = new(OpCodes.Ldarg_0);
        loadServer.labels.AddRange(code[insertionIndex].labels);
        loadServer.blocks.AddRange(code[insertionIndex].blocks);
        code[insertionIndex].labels.Clear();
        code[insertionIndex].blocks.Clear();
        code.InsertRange(insertionIndex, new[]
        {
            loadServer,
            new CodeInstruction(loadUser),
            new CodeInstruction(OpCodes.Call, beginKick),
            new CodeInstruction(OpCodes.Brtrue, afterKick)
        });
        return code;
    }

    private static bool TryBeginOperationalKickByUser(ZNet server, string user)
    {
        if (!server.IsServer() ||
            ZNet.m_onlineBackend != OnlineBackendType.Steamworks ||
            string.IsNullOrEmpty(user))
        {
            return false;
        }

        try
        {
            // Preserve vanilla's host-ID-first, exact-name-second targeting.
            // Never resolve names ahead of a matching authenticated host ID.
            string? host = GetVanillaSteamHostCandidate(user);
            ZNetPeer? peer = host == null ? null : server.GetPeerByHostName(host);
            peer ??= server.GetPeerByPlayerName(user);
            return peer != null &&
                   ServerManagerRuntime.TryBeginOperationalKick(server, peer);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Failure to begin an optional final save must not cancel an
            // authorized kick. The original InternalKick call follows false.
            try
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Could not begin the operational-kick final save; " +
                    "the original kick will continue: " + exception.Message);
            }
            catch (Exception loggingException) when (
                !IntegrityCanonical.IsFatal(loggingException))
            {
                // Logging is also non-blocking for the original kick.
            }

            return false;
        }
    }

    internal static string? GetVanillaSteamHostCandidate(string user)
    {
        // Installed Splatform.PlatformUserID.TryParse splits on the first '_'
        // only when both sides are nonempty. Platform equality is ordinal and
        // case-sensitive. Mirror that small rule without a new assembly ref.
        int separator = user.IndexOf('_');
        if (separator <= 0 || separator == user.Length - 1)
        {
            return user;
        }

        return string.Equals(
            user.Substring(0, separator),
            "Steam",
            StringComparison.Ordinal)
            ? user.Substring(separator + 1)
            : null;
    }
}

[HarmonyPatch(
    typeof(ZNet),
    nameof(ZNet.Disconnect),
    new[] { typeof(ZNetPeer) })]
internal static class DisconnectCleanupPatch
{
    private static void Prefix(ZNet __instance, ZNetPeer peer)
    {
        ServerManagerRuntime.BeforeDisconnect(__instance, peer);
    }
}

[HarmonyPatch(typeof(ZNet), "StopAll")]
internal static class NetworkShutdownCleanupPatch
{
    private static void Prefix(ZNet __instance, out bool __state)
    {
        __state = false;
        try
        {
            __state = ServerManagerRuntime.BeforeNetworkShutdown(__instance);
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerRuntime.ReportNetworkShutdownHookFailure(
                "StopAll prefix",
                exception);
        }
    }

    private static Exception? Finalizer(
        ZNet __instance,
        bool __state,
        Exception? __exception)
    {
        try
        {
            ServerManagerRuntime.AfterNetworkShutdown(
                __instance,
                __state,
                __exception);
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerRuntime.ReportNetworkShutdownHookFailure(
                "StopAll finalizer",
                exception);
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(FejdStartup), "ShowConnectError")]
internal static class ConnectionErrorPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(FejdStartup __instance)
    {
        ClientMenuBranding.OnConnectionError();
        ConnectionErrorPanelPresentation.BeforeShow(__instance);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(FejdStartup __instance)
    {
        ConnectionErrorPanelPresentation.AfterShow(__instance);
    }
}

[HarmonyPatch(typeof(FejdStartup), "Update")]
internal static class ConnectionErrorPanelUpdatePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(FejdStartup __instance) => ConnectionErrorPanelPresentation.Tick(__instance);
}

[HarmonyPatch(typeof(FejdStartup), "OnConnectionFailedOk")]
internal static class ConnectionErrorPanelAcknowledgePatch
{
    private static void Postfix(FejdStartup __instance)
    {
        ConnectionErrorPanelPresentation.AfterAcknowledged(__instance);
    }
}

[HarmonyPatch(typeof(FejdStartup), "OnDestroy")]
internal static class ClientUiStartupDestroyPatch
{
    private static void Prefix(FejdStartup __instance)
    {
        ClientMenuBranding.BeforeStartupDestroyed(__instance);
        ConnectionErrorPanelPresentation.BeforeStartupDestroyed(__instance);
    }
}
