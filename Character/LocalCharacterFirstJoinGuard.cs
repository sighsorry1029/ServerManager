using System;

namespace ServerManager;

/// <summary>
/// Protects a selected client character with existing world history from being
/// replaced by an unmaterialized first-join server profile. This is a local
/// data-safety check, not an authority or anti-cheat decision.
/// </summary>
internal static class LocalCharacterFirstJoinGuard
{
    internal static bool ShouldRejectUsedLocalFirstJoin(
        CharacterEnvelope envelope,
        bool selectedHasWorldHistory,
        bool managedHasPlayerData)
    {
        if (envelope == null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        return envelope.Kind == CharacterEnvelopeKind.Snapshot &&
               (envelope.RequiresFreshLocalCharacter ||
                (envelope.Revision == 1 && envelope.BaseRevision == 0 &&
                 !managedHasPlayerData)) &&
               selectedHasWorldHistory;
    }
}
