using System;
using System.Collections.Generic;
using UnityEngine;

namespace ServerManager.Events;

internal sealed class ServerEventOverlay : MonoBehaviour
{
    private static ServerEventOverlay? _instance;
    private readonly List<Entry> _entries = new();
    private GUIStyle? _style;

    internal static void Show(string kind, string message)
    {
        if (ServerManagerPlugin.ShowEventNotifications?.Value == false)
        {
            _instance?._entries.Clear();
            return;
        }

        if (Application.isBatchMode || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (_instance == null)
        {
            GameObject owner = new("ServerManager event overlay");
            DontDestroyOnLoad(owner);
            _instance = owner.AddComponent<ServerEventOverlay>();
        }

        _instance.Add(kind, message);
    }

    internal static void Shutdown()
    {
        if (_instance != null)
        {
            Destroy(_instance.gameObject);
            _instance = null;
        }
    }

    private void Add(string kind, string message)
    {
        _entries.Add(new Entry(
            kind == ServerManagerEventKinds.ServerAnnouncement
                ? "[SERVER] " + message
                : message,
            Time.unscaledTime + 8f));
        if (_entries.Count > 5)
        {
            _entries.RemoveAt(0);
        }
    }

    private void Update()
    {
        if (!HasVisibleEntries())
        {
            return;
        }

        float now = Time.unscaledTime;
        for (int index = _entries.Count - 1; index >= 0; --index)
        {
            if (_entries[index].ExpiresAt <= now)
            {
                _entries.RemoveAt(index);
            }
        }
    }

    private void OnGUI()
    {
        if (!HasVisibleEntries())
        {
            return;
        }

        _style ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
            richText = false
        };
        _style.normal.textColor = Color.white;

        float width = Math.Min(800f, Screen.width - 40f);
        Rect area = new(
            (Screen.width - width) / 2f,
            50f,
            width,
            180f);
        GUILayout.BeginArea(area);
        for (int index = 0; index < _entries.Count; ++index)
        {
            GUILayout.Label(_entries[index].Message, _style);
        }

        GUILayout.EndArea();
    }

    private bool HasVisibleEntries()
    {
        // Check on render as well as Update so a local toggle hides existing
        // messages immediately. Discard them instead of replaying on re-enable.
        if (ServerManagerPlugin.ShowEventNotifications?.Value == false)
        {
            _entries.Clear();
        }

        return _entries.Count != 0;
    }

    private sealed class Entry
    {
        internal Entry(string message, float expiresAt)
        {
            Message = message;
            ExpiresAt = expiresAt;
        }

        internal string Message { get; }
        internal float ExpiresAt { get; }
    }
}
