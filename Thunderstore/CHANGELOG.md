# Changelog

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
