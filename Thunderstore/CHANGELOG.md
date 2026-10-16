# Changelog

## 1.2.2

- Fixed oversized Discord link text with CustomMainMenu by leaving its button styles unchanged while preserving ServerManager's quick-connect text and behavior.
- Restored the original font size when releasing ServerManager's button styling, without restoring styles it never changed.
- Added regression coverage for custom-menu button styling, style ownership and restoration. Restart Valheim after updating to recreate existing menu buttons.

## 1.2.1

- Reduced routine scheduled-broadcast logging. `logJobs` now defaults to `false`, with per-job `log` overrides; existing explicit settings are preserved. Successful center-broadcast-only jobs can omit their audit-file entries while failures, other commands, Discord reports and saved schedule progress remain unchanged.
- Improved assembly-inspection diagnostics with the first exception and stack in the affected client's log, plus short audit and webhook summaries. Partial inspection failures are distinguished from assembly-load monitoring and enumeration failures; these diagnostics remain log-only.
- Bounded repeated diagnostic output and kept exception messages, stacks and local paths out of transmitted summaries. Existing detection checks continue when part of an inspection fails.
- Added regression coverage for quiet scheduled jobs, diagnostic limits, partial inspection, protocol validation and webhook privacy.
- Update ServerManager on both the server and all clients; the connection protocol has changed.

## 1.2.0

- Added per-job `allowClientHide` for scheduled `broadcast center` messages. It defaults to `false`, preserving existing notices; set it to `true` for repeated tips players may hide.
- Added the client-local **Show Optional Broadcasts** setting, enabled by default and independent of **Show Event Notifications**. Configuration Manager changes apply to new messages immediately.
- Preserved message color and size tags, scheduled execution, logs and Discord reports. Changing the hide permission does not reset saved schedule progress; manual broadcasts and other game messages are unchanged.
- Added regression coverage for optional-message routing, independent display settings, authentication and schedule reloads.
- Update ServerManager on both the server and all clients; the connection protocol has changed.

## 1.1.10

- Improved connection compatibility by accepting bounded server-side PeerInfo extensions instead of disconnecting with "PeerInfo contained trailing data." Original packet data is preserved for Valheim and mod handlers.
- Kept existing packet-size, required-field and client-to-server validation safeguards. Added regression coverage for extensions, truncated packets and unchanged packet forwarding.
- Added a once-per-connection diagnostic reporting extension size without logging packet contents.
- Update ServerManager on both the server and all clients.

## 1.1.9

- Added Valheim 1.0.20 character-format support, preserving new drop counters and item-upgrade state. Existing pre-update Deep North character files and backups remain readable.
- Improved startup diagnostics when the game's character format is unsupported, distinguishing compatibility failures from invalid YAML.
- Made `maxPlayers` optional: 10 disables ServerManager's override, while 11-250 sets the player limit. Changes require a server restart or reopening a hosted world; other YAML settings still reload live.
- Allowed coexistence with MaxPlayerCount, NetworkPerformanceSystem and Valheim Plus by leaving player limits to those mods while keeping authentication and character management active. Increased bounded authentication capacity for larger servers; 250 is a configuration ceiling, not a performance guarantee.
- Update ServerManager on both the server and all clients. Existing YAML values are not rewritten; set `maxPlayers: 10` to disable the override.

## 1.1.8

- Allowed larger Player custom-data values within existing total-size safeguards, fixing save failures and disconnects when mods accumulate progression in one value.
- Improved character-data size errors with the affected key and byte limits, without logging the stored value.
- Added regression coverage for large mod data, inventory-update preservation, size boundaries and malformed data. Existing total-size limits and save protections remain unchanged.
- Update ServerManager on both the server and all clients.

## 1.1.7

- Improved compatibility when restoring equipped items during character loading. Failures limited to the final equipment step are logged without blocking entry; other load-failure safeguards remain in place.
- Added character names and available Steam IDs to connection-rejection webhooks, with unverified labels before authentication and identity hiding on anonymous routes.
- Strengthened defensive checks.
- Update ServerManager on both the server and all clients; the connection protocol has changed.

## 1.1.6

- Preserved the original exception stack when managed character loading fails, making the initial game or mod error easier to locate in the affected client's BepInEx log.
- Kept existing load-failure safeguards: incomplete character loads still block saving and return the player to the lobby. Normal game-handled missing items remain allowed.
- Added regression coverage for exception origins, early and late patch failures, fatal exceptions, and load-state cleanup.

## 1.1.5

- Fixed periodic full character updates retaining an outdated rejoin position. Updates now refresh the logout point using Valheim's normal death and intro rules; inventory-only updates and save intervals are unchanged.
- Added English and Korean Discord command descriptions, help and bot notices based on each user's Discord language. Command names stay English, while detailed command results and game/mod RCON output remain unchanged.
- Reused the existing translation files for bot text and kept webhook language settings independent. Both bot languages are included in the DLL.
- Added regression tests for rejoin-position capture, profile/inventory round trips, Discord localization, command registration and English fallback.

## 1.1.4

- Moved world-aligned character checkpoint file writes, backup rotation and disk verification off the main thread, with one background write at a time.
- Fixed failed checkpoint retries retaining already-completed characters from the same batch after their memory accounting had been released.
- Preserved newer in-memory progress during writes and report checkpoint completion only after disk results are confirmed. Failed characters keep their isolated retry path.
- Added regression coverage for stalled storage, reconnects, partial failures, retry memory release and shutdown ownership. World-save intervals and character validation rules are unchanged.

## 1.1.3

- Reduced player-log size by skipping unchanged full inventory snapshots at the five-minute check, while keeping the first snapshot after joining or on a new local date.
- Added background gzip compression for past-date player logs and completed size-split parts. Active logs stay plain text, and compression is verified before removing the original.
- Preserved late-arriving records and existing log retention rules across compressed files, with recovery tests for failed writes and archive conflicts.
- Kept individual inventory-change entries, character transfers, and save timing unchanged.

## 1.1.2

- Allowed leading and trailing spaces in character names, preserving every space so distinct native profiles keep separate storage identities.
- Fixed custom-data item grants and failed-grant inventory rollback to use the current Valheim inventory method signatures.
- Clarified how to target names containing spaces in F5 and Discord commands, with additional storage and command regression tests.
- Updated the required BepInExPack Valheim version to 5.4.2351.

## 1.1.1

- Added inventory overlap diagnostics identifying the slot and both conflicting items, with local prefab names when available.
- Added a brief client-side capture retry for overlapping inventory items: once per second for up to 10 seconds, with warnings limited to once per 30 seconds. Invalid snapshots remain blocked, and existing save and logout deadlines still apply.
- Removed the redundant "Client report" labels from Discord combat and security webhook cards.
- Simplified anonymous webhook prefix validation and scheduler state handling while preserving configuration and saved schedule identities.

## 1.1.0

- Added detailed character-save errors with the affected inventory item number, slot, saved prefab hash and flag values.
- Distinguished missing prefab identity, a zero prefab hash, invalid stack counts and unsupported flag bits while preserving existing save validation rules.

## 1.0.10

- Allowed ordinary wrong-password attempts to retry without entering the process-lifetime Steam authentication quarantine.
- Made scheduled Upgrade World integration validate its runtime contract instead of requiring version 1.80 exactly.
- Added a single-command dispatch fallback when Upgrade World completion cannot be tracked, with no automatic saves, retries or completion claim.

## 1.0.9

- Added an opt-in `discord.shout` webhook event for Discord user/admin chat and admin `chat` commands delivered to the game.
- Kept Discord chat notifications compact, with anonymous-prefix support, hidden author IDs, and existing duplicate and relay-loop protection.

## 1.0.8

- Preserved the appearance selected during character creation on the first server join, while keeping server-defined starting items and progression.
- Limited required/optional checks to BepInEx plugins. Standalone library DLLs are now skipped and reported when the policy loads or reloads, avoiding rejections caused by shared game libraries.
- Clarified that ServerManager.dll is automatically required, must not be placed in optional, and does not need a copy in required.

## 1.0.7

- Added optional CustomMainMenu compatibility so its changelog and logo remain visible, with ServerManager guidance and allowed mods available from a compact Server Info dialog.
- Made new Discord examples disabled by default and isolated live-reload failures to the affected bot or named webhook block while valid blocks continue to update.
- Added compact `cron.executed` webhook summaries that combine each scheduled job's commands, content, schedule and result into one notification.

## 1.0.6

- Centralized YAML safety checks while retaining each configuration's existing limits and behavior.
- Simplified internal connection-session identity checks without changing authentication or character handling.

## 1.0.5

- Preserved Valheim's used-cheats achievement flag for auditing without rejecting otherwise valid character revisions.
- Fixed per-player activity logs being skipped after a valid Steam-authenticated Ready handshake.

## 1.0.3

- Fixed administrator mod-policy exemptions by confirming them only after Steam authentication against the live server admin list.
- Updated Steam admin, access and ban-list handling for Valheim 1.0's `V_` account prefix while continuing to recognize existing numeric and `Steam_` entries.
- Fixed repeated Steam authentication callbacks preventing a previously authenticated player from reconnecting after an interrupted session.

## 1.0.2

- Fixed Valheim 1.0.7 PeerInfo validation rejecting valid client connections as trailing packet data.
- Restored the intended character flow: new server characters may join, while existing local characters are asked to create a new character when required.

## 1.0.1

- Updated character storage, inventory parsing, world-save tracking and Harmony patches for Valheim 1.0.7.
- Fixed local-host character startup and protected failed character loads from overwriting server data.
- Allow the game to skip items from removed mods instead of blocking entry.
- Updated the required BepInExPack Valheim version to 5.4.2350.

## 1.0.0

- Initial release.
