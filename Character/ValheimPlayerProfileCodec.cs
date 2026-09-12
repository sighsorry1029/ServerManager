using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ServerManager
{
    /// <summary>
    /// Serializes the raw outer PlayerProfile ZPackage without creating a .fch file.
    /// This codec intentionally fails closed when Valheim changes its player profile version.
    /// </summary>
    public sealed partial class ValheimPlayerProfileCodec
    {
        public const int SupportedPlayerProfileVersion = 46;
        public const int SupportedPlayerDataVersion = 33;
        public const int MaximumInventorySnapshotBytes = 1024 * 1024;
        private const int SupportedPlayerStatCount = 205;
        private const int SupportedStatGroupCount = 10;
        private const int SupportedEnemyStatGroupCount = 5;
        private const int MaxMetadataStringUtf8Bytes = 64 * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly CharacterStorageOptions _options;

        public ValheimPlayerProfileCodec(CharacterStorageOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            EnsureSupportedGameVersion();
        }

        private static PlayerProfile CreateEmptyProfile(string canonicalCharacterName)
        {
            string characterName =
                CharacterNamePolicy.NormalizeAndValidate(canonicalCharacterName);
            if (!string.Equals(
                    characterName,
                    canonicalCharacterName,
                    StringComparison.Ordinal))
            {
                throw new CharacterProtocolException(
                    "The empty profile character name must already be canonical.");
            }

            PlayerProfile profile =
                new PlayerProfile(null, FileHelpers.FileSource.Local);
            profile.SetName(characterName);
            return profile;
        }

        public byte[] CreateEmptyProfileBytes(string canonicalCharacterName)
        {
            return SerializeProfileToBytes(CreateEmptyProfile(canonicalCharacterName));
        }

        /// <summary>
        /// Captures Player.m_playerData through the vanilla Player.Save path, then serializes
        /// the complete outer PlayerProfile package. Call this on Unity's main thread.
        /// </summary>
        public byte[] CaptureProfileToBytes(PlayerProfile profile, Player player)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            profile.SavePlayerData(player);
            return SerializeProfileToBytes(profile);
        }

        /// <summary>
        /// Captures exactly the bytes written by vanilla Inventory.Save. Call this
        /// on Unity's main thread while the inventory is stable.
        /// </summary>
        public byte[] CaptureInventoryToBytes(Inventory inventory)
        {
            if (inventory == null)
            {
                throw new ArgumentNullException(nameof(inventory));
            }

            EnsureSupportedGameVersion();
            ZPackage package = new ZPackage();
            inventory.Save(package);
            byte[] snapshot = package.GetArray();
            ValidateInventorySnapshot(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Validates one complete Inventory.Save payload. Trailing bytes are
        /// rejected so they cannot be interpreted as the following Player fields
        /// after the inventory is spliced into a full profile.
        /// </summary>
        public void ValidateInventorySnapshot(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            EnsureSupportedGameVersion();
            if (payload.Length < sizeof(int) + sizeof(ushort) ||
                payload.Length > MaximumInventorySnapshotBytes)
            {
                throw new CharacterProtocolException(
                    "The inventory snapshot has an invalid length.");
            }

            try
            {
                InnerPlayerDataReader reader = new InnerPlayerDataReader(
                    payload,
                    _options.MaxProfileCollectionEntries);
                _ = ParseAndValidateInnerInventory(reader);
                reader.RequireEnd();
            }
            catch (CharacterProtocolException)
            {
                throw;
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                throw new CharacterProtocolException(
                    "The inventory snapshot could not be decoded.",
                    exception);
            }
        }

        /// <summary>
        /// Replaces only the Inventory.Save byte range in an existing complete
        /// PlayerProfile. Every byte outside that range is retained, then the
        /// resulting full profile is validated through the ordinary canonical path.
        /// </summary>
        internal byte[] ReplaceInventorySnapshot(
            CharacterIdentity identity,
            byte[] fullProfile,
            byte[] inventorySnapshot,
            out CharacterValidatedSnapshot validatedSnapshot)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (fullProfile == null)
            {
                throw new ArgumentNullException(nameof(fullProfile));
            }

            if (inventorySnapshot == null)
            {
                throw new ArgumentNullException(nameof(inventorySnapshot));
            }

            ValidateInventorySnapshot(inventorySnapshot);

            CharacterSemanticSnapshot ignoredSnapshot;
            int inventoryOffset;
            int inventoryLength;
            PlayerProfile decoded = DeserializeProfileFromBytesCore(
                fullProfile,
                null,
                FileHelpers.FileSource.Local,
                out ignoredSnapshot,
                out inventoryOffset,
                out inventoryLength);
            string canonicalName =
                CharacterNamePolicy.NormalizeAndValidate(decoded.GetName());
            if (!string.Equals(
                    identity.CharacterName,
                    canonicalName,
                    StringComparison.Ordinal))
            {
                throw new CharacterProtocolException(
                    "The embedded PlayerProfile name does not match the server session.");
            }

            byte[]? playerData = ProfilePrivateAccess.GetPlayerData(decoded);
            if (playerData == null)
            {
                throw new CharacterProtocolException(
                    "An inventory-only save requires an existing full Player profile.");
            }

            int outerPlayerDataOffset = fullProfile.Length - playerData.Length;
            if (outerPlayerDataOffset < sizeof(int) + sizeof(byte) ||
                fullProfile[outerPlayerDataOffset - sizeof(int) - sizeof(byte)] != 1 ||
                ReadLittleEndianInt32(
                    fullProfile,
                    outerPlayerDataOffset - sizeof(int)) != playerData.Length)
            {
                throw new CharacterProtocolException(
                    "The full PlayerProfile player-data boundary is invalid.");
            }

            int newPlayerDataLength = checked(
                playerData.Length - inventoryLength + inventorySnapshot.Length);
            int newProfileLength = checked(
                fullProfile.Length - inventoryLength + inventorySnapshot.Length);
            if (newPlayerDataLength > _options.MaxPayloadBytes ||
                newProfileLength > _options.MaxPayloadBytes)
            {
                throw new CharacterProtocolException(
                    "The materialized PlayerProfile exceeds the configured payload limit.");
            }

            int inventoryStart = checked(outerPlayerDataOffset + inventoryOffset);
            int inventoryEnd = checked(inventoryStart + inventoryLength);
            byte[] materialized = new byte[newProfileLength];
            Buffer.BlockCopy(
                fullProfile,
                0,
                materialized,
                0,
                inventoryStart);
            Buffer.BlockCopy(
                inventorySnapshot,
                0,
                materialized,
                inventoryStart,
                inventorySnapshot.Length);
            Buffer.BlockCopy(
                fullProfile,
                inventoryEnd,
                materialized,
                inventoryStart + inventorySnapshot.Length,
                fullProfile.Length - inventoryEnd);
            WriteLittleEndianInt32(
                materialized,
                outerPlayerDataOffset - sizeof(int),
                newPlayerDataLength);

            validatedSnapshot =
                ExtractValidatedSnapshot(identity, materialized);
            if (validatedSnapshot.PlayerId != decoded.GetPlayerID())
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile ID changed while materializing the inventory snapshot.");
            }

            return materialized;
        }

        /// <summary>Splices only the version-2 skill section into a validated 46/33 profile.</summary>
        internal byte[] ReplaceSkillAdmin(
            CharacterIdentity identity, byte[] fullProfile, string operation,
            string skillName, float value, out CharacterValidatedSnapshot validatedSnapshot)
        {
            CharacterValidatedSnapshot original = ExtractValidatedSnapshot(identity, fullProfile);
            PlayerProfile decoded = DeserializeProfileFromBytes(fullProfile, null, FileHelpers.FileSource.Local);
            byte[] playerData = ProfilePrivateAccess.GetPlayerData(decoded) ??
                throw new CharacterProtocolException("Offline skill editing requires an existing materialized Player profile.");
            ParseAndValidateInnerPlayerData(playerData, out _, out _, out int skillsOffset, out int skillsLength);
            List<CharacterSemanticSkillState> edited = CharacterAdminActions.EditSkills(
                original.SemanticSnapshot.Skills.Values, operation, skillName, value);
            byte[] replacement = CharacterAdminActions.EncodeSkills(edited);
            int outerOffset = fullProfile.Length - playerData.Length;
            if (outerOffset < sizeof(int) + sizeof(byte) ||
                fullProfile[outerOffset - sizeof(int) - sizeof(byte)] != 1 ||
                ReadLittleEndianInt32(fullProfile, outerOffset - sizeof(int)) != playerData.Length)
                throw new CharacterProtocolException("The full PlayerProfile player-data boundary is invalid.");
            int resultLength = checked(fullProfile.Length - skillsLength + replacement.Length);
            if (resultLength > _options.MaxPayloadBytes)
                throw new CharacterProtocolException("The edited PlayerProfile exceeds its payload limit.");
            int start = checked(outerOffset + skillsOffset);
            int end = checked(start + skillsLength);
            byte[] result = new byte[resultLength];
            Buffer.BlockCopy(fullProfile, 0, result, 0, start);
            Buffer.BlockCopy(replacement, 0, result, start, replacement.Length);
            Buffer.BlockCopy(fullProfile, end, result, start + replacement.Length, fullProfile.Length - end);
            WriteLittleEndianInt32(result, outerOffset - sizeof(int), checked(playerData.Length - skillsLength + replacement.Length));
            validatedSnapshot = ExtractValidatedSnapshot(identity, result);
            if (validatedSnapshot.PlayerId != original.PlayerId)
                throw new CharacterProtocolException("The PlayerProfile ID changed while editing skills.");
            return result;
        }

        /// <summary>
        /// Serializes the current in-memory PlayerProfile to the raw ZPackage payload that
        /// vanilla normally wraps with a length and generated hash inside a .fch file.
        /// </summary>
        public byte[] SerializeProfileToBytes(PlayerProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            EnsureSupportedGameVersion();
            ValidateProfileForSerialization(profile);

            ZPackage package = new ZPackage();
            package.Write(SupportedPlayerProfileVersion);
            WriteProfileStatistics(package, profile);

            package.Write(profile.m_firstSpawn);

            System.Collections.IDictionary worldData =
                ProfilePrivateAccess.GetWorldData(profile);
            List<long> worldIds = new List<long>(worldData.Count);
            foreach (object key in worldData.Keys)
            {
                if (!(key is long worldId))
                {
                    throw new CharacterProtocolException(
                        "A PlayerProfile world-data key is not a world ID.");
                }

                worldIds.Add(worldId);
            }

            worldIds.Sort();
            WriteCount(package, worldIds.Count, "world profile count");
            for (int index = 0; index < worldIds.Count; ++index)
            {
                long worldId = worldIds[index];
                object? world = worldData[worldId];
                if (world == null)
                {
                    throw new CharacterProtocolException(
                        "A PlayerProfile world-data entry is null.");
                }

                bool haveCustomSpawnPoint =
                    ProfilePrivateAccess.GetWorldHaveCustomSpawnPoint(world);
                Vector3 spawnPoint =
                    ProfilePrivateAccess.GetWorldSpawnPoint(world);
                bool haveLogoutPoint =
                    ProfilePrivateAccess.GetWorldHaveLogoutPoint(world);
                Vector3 logoutPoint =
                    ProfilePrivateAccess.GetWorldLogoutPoint(world);
                bool haveDeathPoint =
                    ProfilePrivateAccess.GetWorldHaveDeathPoint(world);
                Vector3 deathPoint =
                    ProfilePrivateAccess.GetWorldDeathPoint(world);
                Vector3 homePoint =
                    ProfilePrivateAccess.GetWorldHomePoint(world);
                byte[]? mapData =
                    ProfilePrivateAccess.GetWorldMapData(world);

                EnsureFinite(spawnPoint, "spawn point");
                EnsureFinite(logoutPoint, "logout point");
                EnsureFinite(deathPoint, "death point");
                EnsureFinite(homePoint, "home point");

                package.Write(worldId);
                package.Write(haveCustomSpawnPoint);
                package.Write(spawnPoint);
                package.Write(haveLogoutPoint);
                package.Write(logoutPoint);
                package.Write(haveDeathPoint);
                package.Write(deathPoint);
                package.Write(homePoint);
                package.Write(mapData != null);
                if (mapData != null)
                {
                    EnsureByteArrayLength(mapData, "world map data");
                    package.Write(mapData);
                }
            }

            WriteProfileString(package, profile.GetName(), "character name");
            package.Write(profile.GetPlayerID());
            WriteProfileString(
                package,
                ProfilePrivateAccess.GetStartSeed(profile) ?? string.Empty,
                "start seed");
            package.Write(profile.m_usedCheats);

            DateTime dateCreated = profile.m_dateCreated;
            if (dateCreated < DateTimeOffset.MinValue.DateTime ||
                dateCreated > DateTimeOffset.MaxValue.DateTime)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile creation date is out of range.");
            }

            package.Write(new DateTimeOffset(dateCreated).ToUnixTimeSeconds());

            byte[]? playerData = ProfilePrivateAccess.GetPlayerData(profile);
            package.Write(playerData != null);
            if (playerData != null)
            {
                EnsureByteArrayLength(playerData, "inner player data");
                _ = ParseAndValidateInnerPlayerData(playerData);
                package.Write(playerData);
            }

            byte[] serialized = package.GetArray();
            if (serialized.Length > _options.MaxPayloadBytes)
            {
                throw new CharacterProtocolException(
                    "The serialized PlayerProfile exceeds the configured payload limit.");
            }

            return serialized;
        }

        public PlayerProfile DeserializeProfileFromBytes(
            byte[] rawPlayerProfile,
            string? localFilename,
            FileHelpers.FileSource fileSource)
        {
            CharacterSemanticSnapshot semanticSnapshot;
            int inventoryOffset;
            int inventoryLength;
            return DeserializeProfileFromBytesCore(
                rawPlayerProfile,
                localFilename,
                fileSource,
                out semanticSnapshot,
                out inventoryOffset,
                out inventoryLength);
        }

        private PlayerProfile DeserializeProfileFromBytesCore(
            byte[] rawPlayerProfile,
            string? localFilename,
            FileHelpers.FileSource fileSource,
            out CharacterSemanticSnapshot semanticSnapshot,
            out int inventoryOffset,
            out int inventoryLength)
        {
            semanticSnapshot = CharacterSemanticSnapshot.Empty;
            inventoryOffset = -1;
            inventoryLength = 0;
            if (rawPlayerProfile == null)
            {
                throw new ArgumentNullException(nameof(rawPlayerProfile));
            }

            EnsureSupportedGameVersion();
            if (rawPlayerProfile.Length < 16 ||
                rawPlayerProfile.Length > _options.MaxPayloadBytes)
            {
                throw new CharacterProtocolException(
                    "The raw PlayerProfile has an invalid length.");
            }

            try
            {
                ZPackage package = new ZPackage(rawPlayerProfile);
                int profileVersion = package.ReadInt();
                if (profileVersion != SupportedPlayerProfileVersion)
                {
                    throw new CharacterProtocolException(
                        "The raw PlayerProfile version is unsupported.");
                }

                PlayerProfile profile = new PlayerProfile(localFilename, fileSource);

                ReadProfileStatistics(package, rawPlayerProfile, profile);

                profile.m_firstSpawn = package.ReadBool();
                System.Collections.IDictionary worldData =
                    ProfilePrivateAccess.GetWorldData(profile);
                worldData.Clear();
                int worldCount = ReadCount(package, "world profile count");
                for (int index = 0; index < worldCount; ++index)
                {
                    long worldId = package.ReadLong();
                    if (worldData.Contains(worldId))
                    {
                        throw new CharacterProtocolException(
                            "The raw PlayerProfile contains a duplicate world ID.");
                    }

                    object world = ProfilePrivateAccess.CreateWorldData();
                    bool haveCustomSpawnPoint = package.ReadBool();
                    Vector3 spawnPoint = package.ReadVector3();
                    bool haveLogoutPoint = package.ReadBool();
                    Vector3 logoutPoint = package.ReadVector3();
                    bool haveDeathPoint = package.ReadBool();
                    Vector3 deathPoint = package.ReadVector3();
                    Vector3 homePoint = package.ReadVector3();

                    EnsureFinite(spawnPoint, "spawn point");
                    EnsureFinite(logoutPoint, "logout point");
                    EnsureFinite(deathPoint, "death point");
                    EnsureFinite(homePoint, "home point");

                    ProfilePrivateAccess.SetWorldHaveCustomSpawnPoint(
                        world,
                        haveCustomSpawnPoint);
                    ProfilePrivateAccess.SetWorldSpawnPoint(world, spawnPoint);
                    ProfilePrivateAccess.SetWorldHaveLogoutPoint(
                        world,
                        haveLogoutPoint);
                    ProfilePrivateAccess.SetWorldLogoutPoint(world, logoutPoint);
                    ProfilePrivateAccess.SetWorldHaveDeathPoint(
                        world,
                        haveDeathPoint);
                    ProfilePrivateAccess.SetWorldDeathPoint(world, deathPoint);
                    ProfilePrivateAccess.SetWorldHomePoint(world, homePoint);

                    if (package.ReadBool())
                    {
                        ProfilePrivateAccess.SetWorldMapData(
                            world,
                            ReadBoundedByteArray(package, "world map data"));
                    }

                    worldData.Add(worldId, world);
                }

                string characterName =
                    ReadProfileString(
                        package,
                        rawPlayerProfile,
                        "character name");
                string canonicalName =
                    CharacterNamePolicy.NormalizeAndValidate(characterName);
                if (!string.Equals(
                        characterName,
                        canonicalName,
                        StringComparison.Ordinal))
                {
                    throw new CharacterProtocolException(
                        "The embedded PlayerProfile name is not canonical.");
                }

                profile.SetName(characterName);
                long playerId = package.ReadLong();
                if (playerId == 0)
                {
                    throw new CharacterProtocolException(
                        "The embedded PlayerProfile ID must be nonzero.");
                }

                ProfilePrivateAccess.SetPlayerId(profile, playerId);

                ProfilePrivateAccess.SetStartSeed(
                    profile,
                    ReadProfileString(
                        package,
                        rawPlayerProfile,
                        "start seed"));
                profile.m_usedCheats = package.ReadBool();

                long creationSeconds = package.ReadLong();
                // Preserve the stored instant. Date would discard the time and
                // reinterpret UTC midnight in the local timezone when saved.
                profile.m_dateCreated =
                    DateTimeOffset.FromUnixTimeSeconds(creationSeconds).UtcDateTime;

                if (package.ReadBool())
                {
                    byte[] playerData =
                        ReadBoundedByteArray(package, "inner player data");
                    semanticSnapshot =
                        ParseAndValidateInnerPlayerData(
                            playerData,
                            out inventoryOffset,
                            out inventoryLength);
                    ProfilePrivateAccess.SetPlayerData(profile, playerData);
                }
                else
                {
                    ProfilePrivateAccess.SetPlayerData(profile, null);
                }

                if (package.GetPos() != package.Size())
                {
                    throw new CharacterProtocolException(
                        "The raw PlayerProfile contains trailing data.");
                }

                semanticSnapshot = semanticSnapshot.WithUsedCheats(profile.m_usedCheats);
                ProfilePrivateAccess.SetLastSaveLoad(profile, DateTime.Now);
                return profile;
            }
            catch (CharacterProtocolException)
            {
                throw;
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                throw new CharacterProtocolException(
                    "The raw PlayerProfile could not be decoded.",
                    exception);
            }
        }

        public void ValidateSnapshot(
            CharacterIdentity expectedIdentity,
            byte[] rawPlayerProfile)
        {
            _ = ExtractValidatedSnapshot(expectedIdentity, rawPlayerProfile);
        }

        /// <summary>
        /// Reads only the bounded outer-profile prefix needed to bind a native
        /// .fch file to its canonical filename. This deliberately stops after
        /// the embedded character name and nonzero player ID, so ordinary
        /// repository startup does not require ObjectDB or parse inventories.
        /// Final/pending crash pairs and ordinary admission receive separate
        /// complete profile and semantic validation where that stronger check
        /// is required.
        /// </summary>
        internal long ValidateStoredIdentityHeader(
            CharacterIdentity expectedIdentity,
            byte[] rawPlayerProfile)
        {
            if (expectedIdentity == null)
            {
                throw new ArgumentNullException(nameof(expectedIdentity));
            }

            return ReadStoredIdentityHeaderCore(rawPlayerProfile, expectedIdentity, out _);
        }

        // Filenames lowercase only the storage key. Read the actual embedded
        // display name when enumerating an offline/native file; runtime identity
        // validation continues to require its original, exact spelling.
        internal long ReadStoredIdentityHeader(byte[] rawPlayerProfile, out string characterName)
        {
            return ReadStoredIdentityHeaderCore(rawPlayerProfile, null, out characterName);
        }

        private long ReadStoredIdentityHeaderCore(byte[] rawPlayerProfile,
            CharacterIdentity? expectedIdentity, out string embeddedCharacterName)
        {
            embeddedCharacterName = string.Empty;

            if (rawPlayerProfile == null)
            {
                throw new ArgumentNullException(nameof(rawPlayerProfile));
            }

            EnsureSupportedGameVersion();
            if (rawPlayerProfile.Length < 16 ||
                rawPlayerProfile.Length > _options.MaxPayloadBytes)
            {
                throw new CharacterProtocolException(
                    "The raw PlayerProfile has an invalid length.");
            }

            try
            {
                ZPackage package = new ZPackage(rawPlayerProfile);
                if (package.ReadInt() != SupportedPlayerProfileVersion)
                {
                    throw new CharacterProtocolException(
                        "The raw PlayerProfile version is unsupported.");
                }

                ReadProfileStatistics(package, rawPlayerProfile, null);

                _ = package.ReadBool();
                int worldCount = ReadCount(package, "world profile count");
                for (int index = 0; index < worldCount; ++index)
                {
                    _ = package.ReadLong();
                    _ = package.ReadBool();
                    EnsureFinite(package.ReadVector3(), "spawn point");
                    _ = package.ReadBool();
                    EnsureFinite(package.ReadVector3(), "logout point");
                    _ = package.ReadBool();
                    EnsureFinite(package.ReadVector3(), "death point");
                    EnsureFinite(package.ReadVector3(), "home point");
                    if (package.ReadBool())
                    {
                        _ = ReadBoundedByteArray(
                            package,
                            "world map data");
                    }
                }

                string characterName = ReadProfileString(
                    package,
                    rawPlayerProfile,
                    "character name");
                string canonicalName =
                    CharacterNamePolicy.NormalizeAndValidate(characterName);
                if (!string.Equals(
                        characterName,
                        canonicalName,
                        StringComparison.Ordinal) ||
                    (expectedIdentity != null && !string.Equals(
                        expectedIdentity.CharacterName,
                        characterName,
                        StringComparison.Ordinal)))
                {
                    throw new CharacterProtocolException(
                        "The embedded PlayerProfile name does not match its canonical storage filename.");
                }

                long playerId = package.ReadLong();
                if (playerId == 0)
                {
                    throw new CharacterProtocolException(
                        "The embedded PlayerProfile ID must be nonzero.");
                }

                embeddedCharacterName = characterName;
                return playerId;
            }
            catch (CharacterProtocolException)
            {
                throw;
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                throw new CharacterProtocolException(
                    "The stored PlayerProfile identity header could not be decoded.",
                    exception);
            }
        }

        internal CharacterValidatedSnapshot ExtractValidatedSnapshot(
            CharacterIdentity expectedIdentity,
            byte[] rawPlayerProfile)
        {
            if (expectedIdentity == null)
            {
                throw new ArgumentNullException(nameof(expectedIdentity));
            }

            string decodedName;
            CharacterValidatedSnapshot validated = ExtractValidatedSnapshot(
                rawPlayerProfile,
                out decodedName);
            if (!string.Equals(
                    expectedIdentity.CharacterName,
                    decodedName,
                    StringComparison.Ordinal))
            {
                throw new CharacterProtocolException(
                    "The embedded PlayerProfile name does not match the server session.");
            }

            return validated;
        }

        internal CharacterValidatedSnapshot ExtractValidatedSnapshot(
            byte[] rawPlayerProfile,
            out string canonicalCharacterName)
        {
            CharacterSemanticSnapshot semanticSnapshot;
            int inventoryOffset;
            int inventoryLength;
            PlayerProfile decoded = DeserializeProfileFromBytesCore(
                rawPlayerProfile,
                null,
                FileHelpers.FileSource.Local,
                out semanticSnapshot,
                out inventoryOffset,
                out inventoryLength);
            canonicalCharacterName =
                CharacterNamePolicy.NormalizeAndValidate(decoded.GetName());
            return new CharacterValidatedSnapshot(
                decoded.GetPlayerID(),
                semanticSnapshot);
        }

        private void ValidateProfileForSerialization(PlayerProfile profile)
        {
            string canonicalName =
                CharacterNamePolicy.NormalizeAndValidate(profile.GetName());
            if (!string.Equals(
                    canonicalName,
                    profile.GetName(),
                    StringComparison.Ordinal))
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile name is not canonical.");
            }

            if (profile.GetPlayerID() == 0)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile ID must be nonzero.");
            }

            if (ProfilePrivateAccess.GetWorldData(profile).Count >
                _options.MaxProfileCollectionEntries)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile contains too many world entries.");
            }
        }

        private void WriteProfileStatistics(ZPackage package, PlayerProfile profile)
        {
            if (profile.m_playerStats.Length != SupportedStatGroupCount)
                throw new CharacterProtocolException("The PlayerProfile statistic groups are unsupported.");
            package.Write(SupportedPlayerStatCount);
            package.Write(SupportedStatGroupCount);
            foreach (PlayerProfile.PlayerStats stats in profile.m_playerStats)
            {
                if (stats == null || stats.m_enemyStats.Length != SupportedEnemyStatGroupCount)
                    throw new CharacterProtocolException("The PlayerProfile enemy statistic groups are unsupported.");
                for (int index = 0; index < SupportedPlayerStatCount; ++index)
                {
                    stats.m_stats.TryGetValue((PlayerStatType)index, out float value);
                    EnsureFinite(value, "player statistic");
                    package.Write(value);
                }
                WriteStringFloatDictionary(package, stats.m_knownWorlds, "known worlds");
                WriteStringFloatDictionary(package, stats.m_knownWorldKeys, "known world keys");
                WriteStringFloatDictionary(package, stats.m_knownCommands, "known commands");
                package.Write(SupportedEnemyStatGroupCount);
                foreach (Dictionary<string, float> enemies in stats.m_enemyStats)
                    WriteStringFloatDictionary(package, enemies, "enemy statistics");
                WriteStringFloatDictionary(package, stats.m_itemPickupStats, "item pickup statistics");
                WriteStringFloatDictionary(package, stats.m_itemCraftStats, "item craft statistics");
                WriteStringFloatDictionary(package, stats.m_pickableStats, "pickable statistics");
                WriteStringFloatDictionary(package, stats.m_foodEatenStats, "food statistics");
                WriteStringFloatDictionary(package, stats.m_piecesPlacedStats, "building statistics");
            }
        }

        // The identity-only reader consumes the same bounded prefix without
        // constructing a profile or touching ObjectDB/Unity objects.
        private void ReadProfileStatistics(ZPackage package, byte[] backing, PlayerProfile? profile)
        {
            if (ReadCount(package, "player statistic count") != SupportedPlayerStatCount ||
                ReadCount(package, "statistic group count") != SupportedStatGroupCount)
                throw new CharacterProtocolException("The raw PlayerProfile statistic counts are unsupported.");
            for (int group = 0; group < SupportedStatGroupCount; ++group)
            {
                PlayerProfile.PlayerStats? stats = profile?.m_playerStats[group];
                for (int index = 0; index < SupportedPlayerStatCount; ++index)
                {
                    float value = package.ReadSingle();
                    EnsureFinite(value, "player statistic");
                    if (stats != null) stats[(PlayerStatType)index] = value;
                }
                ReadStringFloatDictionary(package, backing, stats?.m_knownWorlds, "known worlds");
                ReadStringFloatDictionary(package, backing, stats?.m_knownWorldKeys, "known world keys");
                ReadStringFloatDictionary(package, backing, stats?.m_knownCommands, "known commands");
                if (ReadCount(package, "enemy statistic group count") != SupportedEnemyStatGroupCount)
                    throw new CharacterProtocolException("The raw PlayerProfile enemy statistic count is unsupported.");
                for (int enemy = 0; enemy < SupportedEnemyStatGroupCount; ++enemy)
                    ReadStringFloatDictionary(package, backing, stats?.m_enemyStats[enemy], "enemy statistics");
                ReadStringFloatDictionary(package, backing, stats?.m_itemPickupStats, "item pickup statistics");
                ReadStringFloatDictionary(package, backing, stats?.m_itemCraftStats, "item craft statistics");
                ReadStringFloatDictionary(package, backing, stats?.m_pickableStats, "pickable statistics");
                ReadStringFloatDictionary(package, backing, stats?.m_foodEatenStats, "food statistics");
                ReadStringFloatDictionary(package, backing, stats?.m_piecesPlacedStats, "building statistics");
            }
        }

        private void WriteStringFloatDictionary(
            ZPackage package,
            Dictionary<string, float> values,
            string fieldName)
        {
            if (values == null)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " dictionary is null.");
            }

            WriteCount(package, values.Count, fieldName);
            List<string> keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int index = 0; index < keys.Count; ++index)
            {
                string key = keys[index];
                float value = values[key];
                EnsureFinite(value, fieldName);
                WriteProfileString(package, key, fieldName + " key");
                package.Write(value);
            }
        }

        private void ReadStringFloatDictionary(
            ZPackage package,
            byte[] profileBacking,
            Dictionary<string, float>? destination,
            string fieldName)
        {
            destination?.Clear();
            HashSet<string>? skippedKeys = destination == null ? new HashSet<string>(StringComparer.Ordinal) : null;
            int count = ReadCount(package, fieldName);
            for (int index = 0; index < count; ++index)
            {
                string key =
                    ReadProfileString(
                        package,
                        profileBacking,
                        fieldName + " key");
                float value = package.ReadSingle();
                EnsureFinite(value, fieldName);
                if (destination != null ? destination.ContainsKey(key) : !skippedKeys!.Add(key))
                {
                    throw new CharacterProtocolException(
                        "The PlayerProfile contains a duplicate " + fieldName + " key.");
                }

                destination?.Add(key, value);
            }
        }

        private void WriteCount(ZPackage package, int count, string fieldName)
        {
            if (count < 0 || count > _options.MaxProfileCollectionEntries)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " exceeds the configured limit.");
            }

            package.Write(count);
        }

        private int ReadCount(ZPackage package, string fieldName)
        {
            int count = package.ReadInt();
            if (count < 0 || count > _options.MaxProfileCollectionEntries)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " exceeds the configured limit.");
            }

            return count;
        }

        private static void WriteProfileString(
            ZPackage package,
            string value,
            string fieldName)
        {
            if (value == null ||
                StrictUtf8.GetByteCount(value) > MaxMetadataStringUtf8Bytes)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " is missing or too large.");
            }

            package.Write(value);
        }

        private static string ReadProfileString(
            ZPackage package,
            byte[] profileBacking,
            string fieldName)
        {
            int packageSize = profileBacking.Length;
            int position = package.GetPos();
            int byteLength =
                ReadBounded7BitEncodedInt(
                    profileBacking,
                    ref position,
                    packageSize,
                    fieldName);
            if (byteLength > MaxMetadataStringUtf8Bytes ||
                byteLength > packageSize - position)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " is missing or too large.");
            }

            string value;
            try
            {
                value =
                    StrictUtf8.GetString(
                        profileBacking,
                        position,
                        byteLength);
            }
            catch (DecoderFallbackException exception)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " is not valid UTF-8.",
                    exception);
            }

            package.SetPos(position + byteLength);
            return value;
        }

        private static int ReadBounded7BitEncodedInt(
            byte[] backing,
            ref int position,
            int end,
            string fieldName)
        {
            uint result = 0;
            for (int index = 0; index < 5; ++index)
            {
                if (position < 0 || position >= end)
                {
                    throw new CharacterProtocolException(
                        "The PlayerProfile " + fieldName +
                        " string length is truncated.");
                }

                byte current = backing[position++];
                if (index == 4 && (current & 0xf8) != 0)
                {
                    throw new CharacterProtocolException(
                        "The PlayerProfile " + fieldName +
                        " string length is invalid.");
                }

                result |= (uint)(current & 0x7f) << (index * 7);
                if ((current & 0x80) == 0)
                {
                    return (int)result;
                }
            }

            throw new CharacterProtocolException(
                "The PlayerProfile " + fieldName +
                " string length is invalid.");
        }

        private static int ReadLittleEndianInt32(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - sizeof(int))
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile integer boundary is invalid.");
            }

            return bytes[offset] |
                   bytes[offset + 1] << 8 |
                   bytes[offset + 2] << 16 |
                   bytes[offset + 3] << 24;
        }

        private static void WriteLittleEndianInt32(
            byte[] bytes,
            int offset,
            int value)
        {
            if (offset < 0 || offset > bytes.Length - sizeof(int))
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile integer boundary is invalid.");
            }

            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private byte[] ReadBoundedByteArray(ZPackage package, string fieldName)
        {
            int length = package.ReadInt();
            if (length < 0 || length > _options.MaxPayloadBytes)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " exceeds the configured limit.");
            }

            if (package.Size() - package.GetPos() < length)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " is truncated.");
            }

            byte[] value = package.ReadByteArray(length);
            if (value.Length != length)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " is truncated.");
            }

            return value;
        }

        private void EnsureByteArrayLength(byte[] value, string fieldName)
        {
            if (value.Length > _options.MaxPayloadBytes)
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " exceeds the configured limit.");
            }
        }

        private CharacterSemanticSnapshot ParseAndValidateInnerPlayerData(
            byte[] playerData)
        {
            int inventoryOffset;
            int inventoryLength;
            return ParseAndValidateInnerPlayerData(
                playerData,
                out inventoryOffset,
                out inventoryLength);
        }

        private CharacterSemanticSnapshot ParseAndValidateInnerPlayerData(
            byte[] playerData,
            out int inventoryOffset,
            out int inventoryLength)
        {
            return ParseAndValidateInnerPlayerData(playerData, out inventoryOffset,
                out inventoryLength, out _, out _);
        }

        private CharacterSemanticSnapshot ParseAndValidateInnerPlayerData(
            byte[] playerData,
            out int inventoryOffset,
            out int inventoryLength,
            out int skillsOffset,
            out int skillsLength)
        {
            InnerPlayerDataReader reader = new InnerPlayerDataReader(
                playerData,
                _options.MaxProfileCollectionEntries);
            int version = reader.ReadInt32("version");
            if (version != SupportedPlayerDataVersion)
            {
                throw new CharacterProtocolException(
                    "The inner Player data version is unsupported.");
            }

            float maximumHealth =
                reader.ReadNonNegativeFinite("maximum health");
            float health = reader.ReadNonNegativeFinite("health");
            float maximumStamina =
                reader.ReadNonNegativeFinite("maximum stamina");
            if (maximumHealth <= 0f ||
                maximumStamina < 0f ||
                health > maximumHealth + 0.01f)
            {
                throw new CharacterProtocolException(
                    "The inner Player health or stamina values are invalid.");
            }

            reader.ReadNonNegativeFinite("time since death");
            reader.ReadString("guardian power");
            reader.ReadNonNegativeFinite("guardian power cooldown");
            inventoryOffset = reader.Position;
            List<CharacterSemanticItemState> items =
                ParseAndValidateInnerInventory(reader);
            inventoryLength = reader.Position - inventoryOffset;

            reader.SkipStringSet("known recipes");
            reader.SkipStringIntDictionary("known stations");
            reader.SkipStringSet("known materials");
            reader.SkipStringSet("shown tutorials");
            reader.SkipStringSet("unique keys");
            reader.SkipStringSet("trophies");
            reader.SkipStringSet("known biomes");
            reader.SkipStringStringDictionary("known texts");

            reader.ReadString("beard item");
            reader.ReadString("hair item");
            ValidateColor(reader, "skin color");
            ValidateColor(reader, "hair color");

            int modelIndex = reader.ReadInt32("player model");
            if (modelIndex < 0 || modelIndex > 16)
            {
                throw new CharacterProtocolException(
                    "The inner Player model index is invalid.");
            }

            int foodCount = reader.ReadCount("active foods", 16);
            for (int index = 0; index < foodCount; ++index)
            {
                reader.ReadString("food prefab");
                reader.ReadNonNegativeFinite("food time");
            }

            skillsOffset = reader.Position;
            int skillsVersion = reader.ReadInt32("skills version");
            if (skillsVersion != 2)
            {
                throw new CharacterProtocolException(
                    "The inner Player skills version is unsupported.");
            }

            int skillCount = reader.ReadCount("skills", 256);
            HashSet<int> skillTypes = new HashSet<int>();
            List<CharacterSemanticSkillState> skills =
                new List<CharacterSemanticSkillState>(skillCount);
            for (int index = 0; index < skillCount; ++index)
            {
                int skillType = reader.ReadInt32("skill type");
                float level = reader.ReadFinite("skill level");
                float accumulator = reader.ReadFinite("skill accumulator");
                // Skill IDs and progression values belong to the game/mods.
                // We only need an unambiguous finite record for lossless storage
                // and observation; the vanilla enum is not a mod-skill registry.
                if (!skillTypes.Add(skillType))
                {
                    throw new CharacterProtocolException(
                        "The inner Player contains duplicate skill IDs.");
                }

                skills.Add(
                    new CharacterSemanticSkillState(
                        skillType,
                        level,
                        accumulator));
            }

            skillsLength = reader.Position - skillsOffset;
            List<string> playerCustomDataKeys =
                reader.ReadStringStringDictionaryKeys(
                    "player custom data",
                    maximumCount: 1024);
            float stamina = reader.ReadNonNegativeFinite("current stamina");
            float maximumEitr =
                reader.ReadNonNegativeFinite("maximum eitr");
            float eitr = reader.ReadNonNegativeFinite("current eitr");
            if (stamina > maximumStamina + 0.01f ||
                eitr > maximumEitr + 0.01f)
            {
                throw new CharacterProtocolException(
                    "The inner Player stamina or eitr values are invalid.");
            }

            reader.SkipByteArray("build menu state");
            reader.RequireEnd();
            return new CharacterSemanticSnapshot(
                true,
                maximumHealth,
                maximumStamina,
                maximumEitr,
                skills,
                items,
                playerCustomDataKeys);
        }

        private static List<CharacterSemanticItemState>
            ParseAndValidateInnerInventory(InnerPlayerDataReader reader)
        {
            int inventoryVersion = reader.ReadInt32("inventory version");
            if (inventoryVersion != 109)
            {
                throw new CharacterProtocolException(
                    "The inner Player inventory version is unsupported.");
            }

            int itemCount = reader.ValidateCount(reader.ReadUInt16("inventory items"), "inventory items", 256);
            HashSet<long> occupiedPositions = new HashSet<long>();
            List<CharacterSemanticItemState> items =
                new List<CharacterSemanticItemState>(itemCount);
            for (int index = 0; index < itemCount; ++index)
            {
                _ = reader.ReadInt32("item durability hundredths");
                int positionX = reader.ReadByte("item position X");
                int positionY = reader.ReadByte("item position Y");
                int worldLevel = reader.ReadByte("item world level");
                int flags = reader.ReadByte("item flags");
                int quality = (flags & 4) != 0 ? reader.ReadUInt16("item quality") : 1;
                int stack = (flags & 8) != 0 ? reader.ReadUInt16("item stack") : 1;
                if ((flags & 16) != 0) _ = reader.ReadInt32("item variant");
                if ((flags & 32) != 0)
                {
                    _ = reader.ReadInt64("item crafter ID");
                    reader.ReadString("item crafter name");
                }
                int prefabHash = (flags & 64) != 0 ? reader.ReadInt32("item prefab hash") : 0;
                IReadOnlyList<KeyValuePair<string, string>> customData = (flags & 128) != 0
                    ? reader.ReadSemanticStringDictionary("item custom data", 256, compactCount: true)
                    : Array.Empty<KeyValuePair<string, string>>();
                int cheatFlags = reader.ReadByte("item cheat flags");

                if (prefabHash == 0 || stack < 1 || (cheatFlags & ~1) != 0)
                {
                    throw new CharacterProtocolException(
                        "The inner Player contains invalid inventory item data.");
                }

                if (positionX >= 0 && positionY >= 0)
                {
                    long positionKey =
                        ((long)positionX << 32) | (uint)positionY;
                    if (!occupiedPositions.Add(positionKey))
                    {
                        throw new CharacterProtocolException(
                            "The inner Player inventory contains overlapping items.");
                    }
                }

                // Preserve the serialized quality/variant/durability and custom
                // data as-is. Loading a prefab on the server to judge these
                // values can reject client/mod-defined data or normalize it.
                items.Add(
                    new CharacterSemanticItemState(
                        prefabHash,
                        stack,
                        quality,
                        worldLevel,
                        positionX,
                        positionY,
                        customData));
            }

            return items;
        }

        private static void ValidateColor(
            InnerPlayerDataReader reader,
            string fieldName)
        {
            for (int component = 0; component < 3; ++component)
            {
                float value = reader.ReadFinite(fieldName);
                if (value < 0f || value > 1f)
                {
                    throw new CharacterProtocolException(
                        "The inner Player " + fieldName + " is invalid.");
                }
            }
        }

        private sealed class InnerPlayerDataReader
        {
            private const int MaximumStringBytes = 64 * 1024;
            private const int MaximumTotalStringBytes = 4 * 1024 * 1024;
            private const int MaximumTotalEntries = 8192;

            private readonly byte[] _bytes;
            private readonly int _maximumCollectionEntries;
            private int _position;
            private int _totalStringBytes;
            private int _totalEntries;

            internal InnerPlayerDataReader(
                byte[] bytes,
                int maximumCollectionEntries)
            {
                _bytes = bytes ??
                    throw new ArgumentNullException(nameof(bytes));
                _maximumCollectionEntries =
                    Math.Min(maximumCollectionEntries, MaximumTotalEntries);
            }

            internal int Position
            {
                get { return _position; }
            }

            internal int ReadCount(
                string fieldName,
                int? maximumCount = null)
            {
                return ValidateCount(ReadInt32(fieldName + " count"), fieldName, maximumCount);
            }

            internal int ValidateCount(int count, string fieldName, int? maximumCount = null)
            {
                int limit = Math.Min(
                    _maximumCollectionEntries,
                    maximumCount ?? _maximumCollectionEntries);
                if (count < 0 ||
                    count > limit ||
                    count > MaximumTotalEntries - _totalEntries)
                {
                    throw new CharacterProtocolException(
                        "The inner Player " + fieldName +
                        " count exceeds the configured limit.");
                }

                _totalEntries += count;
                return count;
            }

            internal int ReadInt32(string fieldName)
            {
                Require(sizeof(int), fieldName);
                int value =
                    _bytes[_position] |
                    _bytes[_position + 1] << 8 |
                    _bytes[_position + 2] << 16 |
                    _bytes[_position + 3] << 24;
                _position += sizeof(int);
                return value;
            }

            internal byte ReadByte(string fieldName)
            {
                Require(1, fieldName);
                return _bytes[_position++];
            }

            internal int ReadUInt16(string fieldName)
            {
                Require(2, fieldName);
                int value = _bytes[_position] | _bytes[_position + 1] << 8;
                _position += 2;
                return value;
            }

            internal void SkipByteArray(string fieldName)
            {
                int length = ReadInt32(fieldName + " length");
                Require(length, fieldName);
                _position += length;
            }

            internal long ReadInt64(string fieldName)
            {
                Require(sizeof(long), fieldName);
                uint low =
                    (uint)(
                        _bytes[_position] |
                        _bytes[_position + 1] << 8 |
                        _bytes[_position + 2] << 16 |
                        _bytes[_position + 3] << 24);
                uint high =
                    (uint)(
                        _bytes[_position + 4] |
                        _bytes[_position + 5] << 8 |
                        _bytes[_position + 6] << 16 |
                        _bytes[_position + 7] << 24);
                _position += sizeof(long);
                return (long)((ulong)low | (ulong)high << 32);
            }

            internal bool ReadBoolean(string fieldName)
            {
                Require(sizeof(byte), fieldName);
                byte value = _bytes[_position++];
                if (value > 1)
                {
                    throw new CharacterProtocolException(
                        "The inner Player " + fieldName + " is invalid.");
                }

                return value != 0;
            }

            internal float ReadFinite(string fieldName)
            {
                Require(sizeof(float), fieldName);
                float value = BitConverter.ToSingle(_bytes, _position);
                _position += sizeof(float);
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    throw new CharacterProtocolException(
                        "The inner Player " + fieldName +
                        " contains a non-finite number.");
                }

                return value;
            }

            internal float ReadNonNegativeFinite(string fieldName)
            {
                float value = ReadFinite(fieldName);
                if (value < 0f)
                {
                    throw new CharacterProtocolException(
                        "The inner Player " + fieldName + " is negative.");
                }

                return value;
            }

            internal string ReadString(string fieldName)
            {
                int byteCount = Read7BitLength(fieldName);
                if (byteCount < 0 ||
                    byteCount > MaximumStringBytes ||
                    byteCount > _bytes.Length - _position ||
                    byteCount >
                        MaximumTotalStringBytes - _totalStringBytes)
                {
                    throw new CharacterProtocolException(
                        "The inner Player " + fieldName +
                        " string exceeds the configured limit.");
                }

                string value;
                try
                {
                    value = StrictUtf8.GetString(
                        _bytes,
                        _position,
                        byteCount);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new CharacterProtocolException(
                        "The inner Player " + fieldName +
                        " string is not valid UTF-8.",
                        exception);
                }

                _position += byteCount;
                _totalStringBytes += byteCount;
                return value;
            }

            internal void SkipStringSet(string fieldName)
            {
                int count = ReadCount(fieldName);
                HashSet<string> values =
                    new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < count; ++index)
                {
                    if (!values.Add(ReadString(fieldName + " entry")))
                    {
                        throw new CharacterProtocolException(
                            "The inner Player " + fieldName +
                            " contains a duplicate entry.");
                    }
                }
            }

            internal void SkipInt32Set(string fieldName)
            {
                int count = ReadCount(fieldName);
                HashSet<int> values = new HashSet<int>();
                for (int index = 0; index < count; ++index)
                {
                    if (!values.Add(ReadInt32(fieldName + " entry")))
                    {
                        throw new CharacterProtocolException(
                            "The inner Player " + fieldName +
                            " contains a duplicate entry.");
                    }
                }
            }

            internal void SkipStringIntDictionary(string fieldName)
            {
                int count = ReadCount(fieldName);
                HashSet<string> keys =
                    new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < count; ++index)
                {
                    string key = ReadString(fieldName + " key");
                    int value = ReadInt32(fieldName + " value");
                    if (!keys.Add(key) || value < 0 || value > 10000)
                    {
                        throw new CharacterProtocolException(
                            "The inner Player " + fieldName +
                            " contains an invalid entry.");
                    }
                }
            }

            internal void SkipStringStringDictionary(
                string fieldName,
                int? maximumCount = null)
            {
                ReadStringStringDictionaryKeys(fieldName, maximumCount);
            }

            internal List<string> ReadStringStringDictionaryKeys(
                string fieldName,
                int? maximumCount = null)
            {
                int count = ReadCount(fieldName, maximumCount);
                HashSet<string> keys =
                    new HashSet<string>(StringComparer.Ordinal);
                List<string> orderedKeys = new List<string>(count);
                for (int index = 0; index < count; ++index)
                {
                    string key = ReadString(fieldName + " key");
                    ReadString(fieldName + " value");
                    if (!keys.Add(key))
                    {
                        throw new CharacterProtocolException(
                            "The inner Player " + fieldName +
                            " contains a duplicate key.");
                    }

                    orderedKeys.Add(key);
                }

                return orderedKeys;
            }

            internal IReadOnlyList<KeyValuePair<string, string>>
                ReadSemanticStringDictionary(
                string fieldName,
                int? maximumCount = null,
                bool compactCount = false)
            {
                int count;
                if (compactCount)
                {
                    count = ReadByte(fieldName + " count");
                    if ((count & 128) != 0)
                        count = ((count & 127) << 8) | ReadByte(fieldName + " count");
                    count = ValidateCount(count, fieldName, maximumCount);
                }
                else count = ReadCount(fieldName, maximumCount);
                HashSet<string> keys =
                    new HashSet<string>(StringComparer.Ordinal);
                List<KeyValuePair<string, string>> entries =
                    new List<KeyValuePair<string, string>>(count);
                for (int index = 0; index < count; ++index)
                {
                    string key = ReadString(fieldName + " key");
                    string value = ReadString(fieldName + " value");
                    if (!keys.Add(key))
                    {
                        throw new CharacterProtocolException(
                            "The inner Player " + fieldName +
                            " contains a duplicate key.");
                    }

                    entries.Add(
                        new KeyValuePair<string, string>(key, value));
                }

                entries.Sort(
                    (left, right) => StringComparer.Ordinal.Compare(
                        left.Key,
                        right.Key));
                return entries.AsReadOnly();
            }

            internal void RequireEnd()
            {
                if (_position != _bytes.Length)
                {
                    throw new CharacterProtocolException(
                        "The inner Player data contains trailing bytes.");
                }
            }

            private int Read7BitLength(string fieldName)
            {
                uint result = 0;
                for (int index = 0; index < 5; ++index)
                {
                    Require(sizeof(byte), fieldName + " string length");
                    byte current = _bytes[_position++];
                    if (index == 4 && (current & 0xF8) != 0)
                    {
                        break;
                    }

                    result |=
                        (uint)(current & 0x7F) << (index * 7);
                    if ((current & 0x80) == 0)
                    {
                        return (int)result;
                    }
                }

                throw new CharacterProtocolException(
                    "The inner Player " + fieldName +
                    " string length is invalid.");
            }

            private void Require(int byteCount, string fieldName)
            {
                if (byteCount < 0 ||
                    _position < 0 ||
                    byteCount > _bytes.Length - _position)
                {
                    throw new CharacterProtocolException(
                        "The inner Player " + fieldName + " is truncated.");
                }
            }
        }

        private static void EnsureFinite(float value, string fieldName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new CharacterProtocolException(
                    "The PlayerProfile " + fieldName + " contains a non-finite number.");
            }
        }

        private static void EnsureFinite(Vector3 value, string fieldName)
        {
            EnsureFinite(value.x, fieldName);
            EnsureFinite(value.y, fieldName);
            EnsureFinite(value.z, fieldName);
        }

        /// <summary>
        /// Keeps Valheim's non-public PlayerProfile schema behind reflection so the
        /// compiled mod never emits an access to a publicized-only field or type.
        /// The codec still validates the exact runtime names and field types before
        /// accepting or producing authoritative character bytes.
        /// </summary>
        private static class ProfilePrivateAccess
        {
            private const BindingFlags InstanceFields =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            private static readonly Lazy<Members> CachedMembers =
                new Lazy<Members>(() => new Members(), true);

            internal static Type ValheimVersionType =>
                CachedMembers.Value.ValheimVersionType;

            internal static void EnsureResolved()
            {
                _ = CachedMembers.Value;
            }

            internal static System.Collections.IDictionary GetWorldData(
                PlayerProfile profile)
            {
                object? value =
                    CachedMembers.Value.ProfileWorldData.GetValue(profile);
                if (value is System.Collections.IDictionary dictionary)
                {
                    return dictionary;
                }

                throw SchemaMismatch("PlayerProfile.m_worldData is not a dictionary.");
            }

            internal static string? GetStartSeed(PlayerProfile profile)
            {
                return GetOptionalReference<string>(
                    CachedMembers.Value.ProfileStartSeed,
                    profile,
                    "PlayerProfile.m_startSeed");
            }

            internal static byte[]? GetPlayerData(PlayerProfile profile)
            {
                return GetOptionalReference<byte[]>(
                    CachedMembers.Value.ProfilePlayerData,
                    profile,
                    "PlayerProfile.m_playerData");
            }

            internal static void SetPlayerId(PlayerProfile profile, long value)
            {
                CachedMembers.Value.ProfilePlayerId.SetValue(profile, value);
            }

            internal static void SetStartSeed(
                PlayerProfile profile,
                string value)
            {
                CachedMembers.Value.ProfileStartSeed.SetValue(profile, value);
            }

            internal static void SetPlayerData(
                PlayerProfile profile,
                byte[]? value)
            {
                CachedMembers.Value.ProfilePlayerData.SetValue(profile, value);
            }

            internal static void SetLastSaveLoad(
                PlayerProfile profile,
                DateTime value)
            {
                CachedMembers.Value.ProfileLastSaveLoad.SetValue(profile, value);
            }

            internal static object CreateWorldData()
            {
                object? world = Activator.CreateInstance(
                    CachedMembers.Value.WorldPlayerDataType,
                    true);
                if (world == null)
                {
                    throw SchemaMismatch(
                        "PlayerProfile.WorldPlayerData could not be created.");
                }

                return world;
            }

            internal static bool GetWorldHaveCustomSpawnPoint(object world)
            {
                return GetRequiredValue<bool>(
                    CachedMembers.Value.WorldHaveCustomSpawnPoint,
                    world,
                    "WorldPlayerData.m_haveCustomSpawnPoint");
            }

            internal static Vector3 GetWorldSpawnPoint(object world)
            {
                return GetRequiredValue<Vector3>(
                    CachedMembers.Value.WorldSpawnPoint,
                    world,
                    "WorldPlayerData.m_spawnPoint");
            }

            internal static bool GetWorldHaveLogoutPoint(object world)
            {
                return GetRequiredValue<bool>(
                    CachedMembers.Value.WorldHaveLogoutPoint,
                    world,
                    "WorldPlayerData.m_haveLogoutPoint");
            }

            internal static Vector3 GetWorldLogoutPoint(object world)
            {
                return GetRequiredValue<Vector3>(
                    CachedMembers.Value.WorldLogoutPoint,
                    world,
                    "WorldPlayerData.m_logoutPoint");
            }

            internal static bool GetWorldHaveDeathPoint(object world)
            {
                return GetRequiredValue<bool>(
                    CachedMembers.Value.WorldHaveDeathPoint,
                    world,
                    "WorldPlayerData.m_haveDeathPoint");
            }

            internal static Vector3 GetWorldDeathPoint(object world)
            {
                return GetRequiredValue<Vector3>(
                    CachedMembers.Value.WorldDeathPoint,
                    world,
                    "WorldPlayerData.m_deathPoint");
            }

            internal static Vector3 GetWorldHomePoint(object world)
            {
                return GetRequiredValue<Vector3>(
                    CachedMembers.Value.WorldHomePoint,
                    world,
                    "WorldPlayerData.m_homePoint");
            }

            internal static byte[]? GetWorldMapData(object world)
            {
                return GetOptionalReference<byte[]>(
                    CachedMembers.Value.WorldMapData,
                    world,
                    "WorldPlayerData.m_mapData");
            }

            internal static void SetWorldHaveCustomSpawnPoint(
                object world,
                bool value)
            {
                CachedMembers.Value.WorldHaveCustomSpawnPoint.SetValue(world, value);
            }

            internal static void SetWorldSpawnPoint(object world, Vector3 value)
            {
                CachedMembers.Value.WorldSpawnPoint.SetValue(world, value);
            }

            internal static void SetWorldHaveLogoutPoint(
                object world,
                bool value)
            {
                CachedMembers.Value.WorldHaveLogoutPoint.SetValue(world, value);
            }

            internal static void SetWorldLogoutPoint(object world, Vector3 value)
            {
                CachedMembers.Value.WorldLogoutPoint.SetValue(world, value);
            }

            internal static void SetWorldHaveDeathPoint(
                object world,
                bool value)
            {
                CachedMembers.Value.WorldHaveDeathPoint.SetValue(world, value);
            }

            internal static void SetWorldDeathPoint(object world, Vector3 value)
            {
                CachedMembers.Value.WorldDeathPoint.SetValue(world, value);
            }

            internal static void SetWorldHomePoint(object world, Vector3 value)
            {
                CachedMembers.Value.WorldHomePoint.SetValue(world, value);
            }

            internal static void SetWorldMapData(object world, byte[] value)
            {
                CachedMembers.Value.WorldMapData.SetValue(world, value);
            }

            private static T GetRequiredValue<T>(
                FieldInfo field,
                object instance,
                string fieldName)
            {
                object? value = field.GetValue(instance);
                if (value is T typed)
                {
                    return typed;
                }

                throw SchemaMismatch(fieldName + " has an unexpected runtime value.");
            }

            private static T? GetOptionalReference<T>(
                FieldInfo field,
                object instance,
                string fieldName)
                where T : class
            {
                object? value = field.GetValue(instance);
                if (value == null)
                {
                    return null;
                }

                if (value is T typed)
                {
                    return typed;
                }

                throw SchemaMismatch(fieldName + " has an unexpected runtime value.");
            }

            private static FieldInfo RequireInstanceField(
                Type declaringType,
                string fieldName,
                Type expectedType)
            {
                FieldInfo? field = declaringType.GetField(fieldName, InstanceFields);
                if (field == null ||
                    field.IsStatic ||
                    field.FieldType != expectedType)
                {
                    throw SchemaMismatch(
                        declaringType.FullName + "." + fieldName +
                        " is unavailable or changed type.");
                }

                return field;
            }

            private static FieldInfo RequireWorldDataField()
            {
                FieldInfo? field = typeof(PlayerProfile).GetField(
                    "m_worldData",
                    InstanceFields);
                if (field == null ||
                    field.IsStatic ||
                    !typeof(System.Collections.IDictionary).IsAssignableFrom(
                        field.FieldType))
                {
                    throw SchemaMismatch(
                        "PlayerProfile.m_worldData is unavailable or changed type.");
                }

                return field;
            }

            private static Type RequireWorldPlayerDataType()
            {
                Type? type = typeof(PlayerProfile).GetNestedType(
                    "WorldPlayerData",
                    BindingFlags.Public | BindingFlags.NonPublic);
                if (type == null || type.IsAbstract || type.IsInterface)
                {
                    throw SchemaMismatch(
                        "PlayerProfile.WorldPlayerData is unavailable or changed type.");
                }

                return type;
            }

            private static Type RequireValheimVersionType()
            {
                Type? type = typeof(PlayerProfile).Assembly.GetType(
                    "Version",
                    false,
                    false);
                if (type == null)
                {
                    throw SchemaMismatch(
                        "Valheim's Version schema type is unavailable.");
                }

                return type;
            }

            private static NotSupportedException SchemaMismatch(string message)
            {
                return new NotSupportedException(
                    "Valheim changed its PlayerProfile schema. " + message);
            }

            private sealed class Members
            {
                internal readonly Type ValheimVersionType;
                internal readonly Type WorldPlayerDataType;
                internal readonly FieldInfo ProfilePlayerId;
                internal readonly FieldInfo ProfileStartSeed;
                internal readonly FieldInfo ProfileWorldData;
                internal readonly FieldInfo ProfilePlayerData;
                internal readonly FieldInfo ProfileLastSaveLoad;
                internal readonly FieldInfo WorldHaveCustomSpawnPoint;
                internal readonly FieldInfo WorldSpawnPoint;
                internal readonly FieldInfo WorldHaveLogoutPoint;
                internal readonly FieldInfo WorldLogoutPoint;
                internal readonly FieldInfo WorldHaveDeathPoint;
                internal readonly FieldInfo WorldDeathPoint;
                internal readonly FieldInfo WorldHomePoint;
                internal readonly FieldInfo WorldMapData;

                internal Members()
                {
                    ValheimVersionType = RequireValheimVersionType();
                    WorldPlayerDataType = RequireWorldPlayerDataType();
                    ProfilePlayerId = RequireInstanceField(
                        typeof(PlayerProfile),
                        "m_playerID",
                        typeof(long));
                    ProfileStartSeed = RequireInstanceField(
                        typeof(PlayerProfile),
                        "m_startSeed",
                        typeof(string));
                    ProfileWorldData = RequireWorldDataField();
                    ProfilePlayerData = RequireInstanceField(
                        typeof(PlayerProfile),
                        "m_playerData",
                        typeof(byte[]));
                    ProfileLastSaveLoad = RequireInstanceField(
                        typeof(PlayerProfile),
                        "m_lastSaveLoad",
                        typeof(DateTime));
                    WorldHaveCustomSpawnPoint = RequireInstanceField(
                        WorldPlayerDataType,
                        "m_haveCustomSpawnPoint",
                        typeof(bool));
                    WorldSpawnPoint = RequireInstanceField(
                        WorldPlayerDataType,
                        "m_spawnPoint",
                        typeof(Vector3));
                    WorldHaveLogoutPoint = RequireInstanceField(
                        WorldPlayerDataType,
                        "m_haveLogoutPoint",
                        typeof(bool));
                    WorldLogoutPoint = RequireInstanceField(
                        WorldPlayerDataType,
                        "m_logoutPoint",
                        typeof(Vector3));
                    WorldHaveDeathPoint = RequireInstanceField(
                        WorldPlayerDataType,
                        "m_haveDeathPoint",
                        typeof(bool));
                    WorldDeathPoint = RequireInstanceField(
                        WorldPlayerDataType,
                        "m_deathPoint",
                        typeof(Vector3));
                    WorldHomePoint = RequireInstanceField(
                        WorldPlayerDataType,
                        "m_homePoint",
                        typeof(Vector3));
                    WorldMapData = RequireInstanceField(
                        WorldPlayerDataType,
                        "m_mapData",
                        typeof(byte[]));
                }
            }
        }

        private static void EnsureSupportedGameVersion()
        {
            ProfilePrivateAccess.EnsureResolved();
            int runtimePlayerProfileVersion =
                ReadRuntimeConstant(
                    ProfilePrivateAccess.ValheimVersionType,
                    "c_PlayerVersion");
            int runtimePlayerDataVersion =
                ReadRuntimeConstant(
                    ProfilePrivateAccess.ValheimVersionType,
                    "c_PlayerDataVersion");
            int runtimePlayerStatCount = Convert.ToInt32(
                Enum.Parse(typeof(PlayerStatType), "Count", false));

            if (runtimePlayerProfileVersion != SupportedPlayerProfileVersion ||
                runtimePlayerDataVersion != SupportedPlayerDataVersion ||
                runtimePlayerStatCount != SupportedPlayerStatCount)
            {
                throw new NotSupportedException(
                    "Valheim changed its PlayerProfile schema. " +
                    "Update and review ValheimPlayerProfileCodec before enabling character saves.");
            }
        }

        private static int ReadRuntimeConstant(Type declaringType, string fieldName)
        {
            FieldInfo field = declaringType.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null || !field.IsLiteral)
            {
                throw new NotSupportedException(
                    "Valheim's " + declaringType.FullName + "." + fieldName +
                    " schema marker is unavailable.");
            }

            object value = field.GetRawConstantValue();
            if (value == null)
            {
                throw new NotSupportedException(
                    "Valheim's " + declaringType.FullName + "." + fieldName +
                    " schema marker has no metadata value.");
            }

            return Convert.ToInt32(value);
        }
    }

}
