# ServerManager operator guide

[First server setup](../README.md) · [Files and rules](#files-and-rules) · [Character recovery](#bring-an-existing-character) · [Commands](#administrator-commands) · [Discord](#discord) · [Scheduling](#scheduled-commands) · [Client setup](#client-menu-and-translations) · [Logs](#log-files)

## Files and rules

The server and a local host use the same layout, relative to the Valheim save directory. Use a separate `-savedir` per independent server. Distribute client settings and translations if needed, but **never** put server credentials, logs or character saves in a modpack.

```text
ServerManager/
├── ServerManager.yml       Server rules and starting items
├── discord.yml             Webhooks, bot commands and chat
├── log.yml                 Audit, player and chat file logging
├── cron.yml                Scheduled commands
├── cron_last.yml           Saved scheduling progress — do not edit
├── Itemdata.yml            Item custom-data presets
├── required/               Required mod reference DLLs
├── optional/               Optional mod reference DLLs
├── characters/
│   ├── <Steam64>/          Character files and backups
│   └── HowToRestore.txt    Import and recovery instructions
├── logs/
│   ├── events-audit.log    Server, security and admin activity
│   ├── events-chat.log     Chat history
│   └── <Steam64>/          Individual player logs
└── cache/logos/            Downloaded client logos, when used
```

Use spaces in YAML; spelling and case matter. Settings reload after an edit settles. Check the server log: an invalid edit need not replace the active settings. Generated defaults do not overwrite your existing files. Leave generated lock files in place.

### ServerManager.yml defaults

```yaml
serverSettings:
  maxPlayers: 10
  maxCharactersPerAccount: 3
  backupsPerProfile: 30
  loadServerCharacterOnJoin: true

forbiddenItems: []

cheatDetection:
  action: kick

statCaps:
  action: log
  health: 800
  stamina: 800
  eitr: 500
  weight: 2000
  damage: 50000

startItems:
  - HelmetMidsummerCrown
  - ArmorRagsChest
  - ArmorRagsLegs
  - Torch
```

Both action fields accept `log`, `kick` and `ban`. Forbidden items are exact prefab names: an incoming save with a forbidden item is rejected, while a finding in an existing server save is logged. Verified admins and the local host are exempt from this item rule, **not** from invalid data or identity checks. Stat-cap exemptions require admin-list membership, even for a host. Admin status does not exempt cheat-tool detections. Review evidence before enabling automatic bans.

`startItems` applies only to *new server characters*. A prefab name means one item; `Wood, 5` means five. Use `startItems: []` for no items. Invalid prefabs or a kit that does not fit reject the edit. Wearable equipment starts equipped if possible; weapons, shields and torches stay in the inventory. When items compete for a slot, the first in prefab-name order wins.

### Update or troubleshoot the modpack

- Install server-side mods where their authors specify. Put **reference copies** of approved client DLLs in `required/` or `optional/`. Clients install plugins in `BepInEx/plugins` and BepInEx 5 preloader patchers in `BepInEx/patchers`. Restart after changing patchers.
- You may approve multiple versions in the same folder. Don't list the same module as both required and optional. A DLL declaring both plugin and patcher entry points is checked as both. Standalone managed libraries are skipped; libraries merged into a plugin are part of its hash. Native DLLs aren't supported references.
- When updating, refresh server, clients **and reference copies**. Changes to the reference folders affect new connections. If a reload fails, the previous valid policy remains active. If the server has **never** loaded a valid policy, fix the first policy error in its log; reinstalling the client won't fix a bad server reference. `sm:modsreload` requests a rescan. `sm:modsstatus` shows server-loaded plugins, not proof of a client's admission.
- A hash match verifies the file, not the client's memory or whether a patcher ran. Assembly-detector diagnostics are separate, log-only reports. Look at the affected client's BepInEx log for the original error. Restricting `printseeds` and `freefly` does not make the world seed secret.

## Character saves

| Mode | What happens on join |
| --- | --- |
| `loadServerCharacterOnJoin: true` (default) | The server restores its latest accepted character. Local single-player changes do not replace it. A new character keeps the selected appearance and receives the server's starting kit. |
| `loadServerCharacterOnJoin: false` | The player brings their selected local/Steam Cloud character, including progress from other worlds. The server still captures saves and backups; it does not apply the starting kit. |

Before switching from local capture back to server characters, have players log out, run `save` and wait for the completed checkpoint. The change applies on the next connection.

**What survives a reconnect?** The running server can restore updates it has accepted in RAM, even before the next disk checkpoint. Inventory changes arrive more often than full saves. Full updates also keep remaining poison, drop counters, upgrade state and rejoin position. Position refreshes about every five minutes and during normal save/logout handling, subject to vanilla death/intro rules. An abrupt disconnect can leave position older than inventory.

**When is it safe to stop?** Wait for `WorldCharacterCheckpointCompleted ... pending=0` after your requested `save`. Character writes run in the background after the world disk save. One character's failure does not stop other writes. Check for failure messages; a world-save webhook alone is not proof that the character files are on disk. A checkpoint covers accepted updates up to its cutoff, not every action a client just took. Character-store reads and disk-based admin commands can briefly be busy; retry after the write finishes.

### Bring an existing character

1. Stop the server completely and back up its files.
2. Confirm the owner’s 17-digit Steam64 ID.
3. Copy the native `.fch` into `characters/<Steam64>/`.
4. Name it `Steam_<Steam64>_<lowercase-character-name>.fch`.
   Use the name stored inside the character; do not change its internal name or Player ID.
   Keep every space, including leading/trailing spaces in the name before `.fch`.
5. Keep `loadServerCharacterOnJoin: true`, restart, check the log, and join with
   the matching character.

For example, a character named `MyHero` uses:

```text
characters/76561198000000001/Steam_76561198000000001_myhero.fch
```

There is no import folder or conversion step. Do not copy `.old`, `.signature`
or `.serverbackup` files into active storage. To use an older native backup,
copy its `.fch` and rename the copy as above. The required game/mod versions
must still be compatible.

Names may contain leading, trailing or repeated spaces. These are kept as part of
the character's identity: `MyHero` and `MyHero ` are different names. Blank names,
control characters and unsafe path characters are not allowed. In text commands,
wrap the full name in double quotes, for example `sm:characterbackups "Two Words"`.

### Restore a backup

The primary and backups share the character’s Steam64 folder:

```text
Steam_76561198000000001_myhero.fch
Steam_76561198000000001_myhero.2026-09-04_23-04-27.fch
```

**With the server running:** have the player log out and stay offline, run
`save`, and wait for a completed checkpoint with `pending=0`. Then:

```text
sm:characterbackups MyHero
sm:characterrestore MyHero <backupId>
```

Use the backup ID returned by the first command, not a filename or list number.
Wait for explicit success before allowing the player to reconnect.
Discord uses `/characterbackups player:MyHero` and
`/characterrestore player:MyHero backup_id:<backupId>`.

**By replacing files:** stop the server first, then copy the chosen backup over
the primary, removing its timestamp and any collision suffix. Keep `.fch`.
Never replace a character file while the server is running: its in-memory state
can override your replacement. Do not rename `.fch.pending` into a primary.

Live restore requires a valid primary and a matching Player ID. For a missing or
corrupt primary, or the host’s own character, use the stopped-server method.
For a local host, close the game too. Set `loadServerCharacterOnJoin: true`
before reconnecting to apply the restored save.
Restore changes the character only, not the world. Check an uncertain result
before retrying. See `characters/HowToRestore.txt` for recovery details.

### If a player cannot join

| Symptom | First check |
| --- | --- |
| Steam authentication timed out | Let at least 30 seconds pass before reconnecting. Early attempts or late Steam callbacks can extend the wait. A server reboot or character restore is not normally needed. |
| The server has no matching character | Create a fresh character, or import the right file under the owner's Steam64 folder. Check the internal name, including spaces. |
| Character loading stopped | Ask for the **client's** BepInEx log immediately after failure. Keep the current server `.fch` and a known-good backup before trying recovery. Audit joins/leaves alone can't identify a client-side mod exception. |
| Gear or inventory changed on load | Missing item prefabs are logged and skipped by the game; later saves may preserve that loss. If only the final re-equip step fails, loading may continue with items unequipped. Other incomplete loads still block saving. |
| Two items reported in the same slot | Remote capture retries an overlap for up to 10 seconds; invalid snapshots are not submitted. Check the logged slot and items and investigate the inventory mod if it persists. |

Character files have a 10 MiB whole-profile limit and a shared 4 MiB UTF-8 string budget. Oversized player data is reported by key and byte count, not by exposing the value.

## Administrator commands

Use `sm:<command>` in F5 or the server console, `/sm:<command>` in game chat,
and `/<command>` in Discord. Remote in-game admins are checked against Valheim's
`adminlist.txt`. Add one from the server console with `sm:adminadd <Steam64>`.
Discord uses its own user-ID list.

| Task | F5 / server console |
| --- | --- |
| Help and status | `sm:help`, `sm:status`, `sm:players [target]` |
| Announce or shout | `sm:announce <message>`, `sm:chat <message>` |
| Character information | `sm:characterlist`, `sm:characterinfo <target>` |
| Backups | `sm:characterbackups <target> [page]`, `sm:characterrestore <target> <backupId>` |
| Give items | `sm:giveitem <target> <prefab> <amount> [quality] [dataId] [notify=false]` |
| Teleport | `sm:teleport <target> to <player>`, or `sm:teleport <target> <x> <y> <z>` |
| Skills | `sm:skillget <target> [skill]`, `sm:skillset <target> <skill> <value> [notify=false]` |
| Health | `sm:heal <target> <amount>`, `sm:damage <target> <amount>` |
| Access lists | `sm:banlist`, `sm:adminlist`, `sm:accesslist` |
| Edit admins | `sm:adminadd <Steam64>`, `sm:adminremove <Steam64>` |
| Edit access | `sm:accessadd <Steam64>`, `sm:accessremove <Steam64>` |
| World keys | `sm:keylist`, `sm:keyadd <key>`, `sm:keyremove <key>` |
| Raids | `sm:eventstart <name> <x> <y> <z>`, `sm:eventstop` |
| Schedules | `sm:cronstatus`, `sm:cronack <jobId>` |
| Mod policy | `sm:modsstatus`, `sm:modsreload` |
| Discord checks | `sm:discordstatus`, `sm:discordtest` |

An empty access list allows all accounts.

Use exact names or Steam64 IDs; ambiguous targets are rejected. Character
commands also accept `Steam64/CharacterName`. Quote names containing spaces.
Use `Steam64/CharacterName` for numeric names or otherwise ambiguous targets.
F5 Tab completion covers command names and single-word online-player names;
type multiword names in double quotes.

```text
sm:giveitem "Some Player" SwordIron 1 4
sm:skillget "Some Player" Swords
sm:skillset "Some Player" Swords 50
sm:teleport "Some Player" to "Admin Name"
```

`skillset` sets a level from 0 to 100 and clears progress toward the next level.
Use 0 to reset a skill, or `all` to set all built-in skills.
`skillget` without a skill lists them all.

Online players get one private chat line when they receive an item or an admin
sets their skills. They can read it again in the current session's chat history.
To skip that line for one command, add `notify=false` at the end in F5 or the
server console, or set `notify:false` in Discord. This does not change the
command result or audit logging.

Use vanilla `save`, `kick`, `ban` and `unban` in the server console or through Discord `/rcon`. There are no `sm:save` or `sm:kick` aliases. In Discord, use typed slash commands for mod features. For example:

```text
/giveitem player:Two Words prefab:SwordIron amount:1 quality:4 notify:false
/rcon command:save
```

Enter a character name containing spaces directly in Discord's `player` field, without surrounding quotes. Item grants require an online player and room for the entire grant; excess items are not dropped. `skillget` and `skillset` can also target a saved offline character. Offline edits don't queue a chat notification for next login. A timeout is not proof of failure: inspect the result before repeating a grant or restore.

### Give items with custom data

Copy an item’s `CustomData` block from its player log into `Itemdata.yml` and
give the preset an ID:

```yaml
- id: restored
  CustomData:
    "your.mod.key": "value"
```

Then apply it to an item grant:

```text
sm:giveitem MyHero ShieldWood 1 2 restored
/giveitem player:MyHero prefab:ShieldWood amount:1 quality:2 data_id:restored
```

The preset stores custom data only, not the item type or amount. Keys and values
must be strings. Replace the example with real data understood by the receiving
mod; not every mod’s item state is portable.

## Discord

The bot and webhooks are independent. New `discord.yml` examples are **disabled** with blank credentials. Unspecified `enabled` means true, so enable only the components you have configured. A webhook posts to its own URL, **not** to the bot's admin channel list. Put private information in a webhook created in a private channel.

### Send webhooks

[Create a webhook in Discord](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks), copy its URL, and edit the matching route in `discord.yml`. Example for two separate destinations:

```yaml
bot:
  enabled: false
webhooks:
  - name: Public feed
    enabled: true
    url: '' # Fill in the public channel's webhook URL.
    events: [server.status, player.connection, chat.shout, raid.status, player.death, boss.killed]
    language: English
  - name: New characters
    enabled: true
    url: '' # Fill in a private admin channel's webhook URL.
    events: [player.first_join]
    include_steam_id: true
```

| Event filter | Notifications |
| --- | --- |
| `server.status` | Server ready and normal shutdown |
| `server.saved` | World saved; character saves may still be pending |
| `server.announcement` | ServerManager announcements |
| `player.connection` | Remote player joins, first joins and leaves |
| `player.first_join` | First login for a new server character profile; no reconnects or leaves |
| `chat.shout` | In-game shout messages |
| `discord.shout` | Discord user/admin chat and admin `chat` commands delivered to the game |
| `raid.status` | Raid starts and ends, with name and coordinates |
| `player.death`, `boss.killed` | Deaths, including PvP, and boss kills |
| `moderation.action`, `command.executed` | Moderation and manual admin activity |
| `cron.executed` | One compact final result for each scheduled job |
| `security.alert`, `security.admin_bypass` | Detection/response reports and admin exemptions |
| `character.validation` | Rejected saves and validation warnings |
| `character.shadow_stalled`, `character.revision_observed` | Delayed character updates and revision warnings |
| `connection.rejected` | Connection failures, including mod mismatches |

`player.connection` **includes first joins**. The private `player.first_join` filter selects only a new server character, including a new character on a returning account. Selecting both in one route sends one card; two separate routes can each receive it.

Steam64 IDs for connection notifications are opt-in per route. `anonymous_prefix: Anonymous` gives labels such as `Anonymous 1` and hides player IDs even when inclusion is enabled. It does not redact names people type into chat. Keep **rejection** reports private: they can include the observed Steam ID and reported name; before authentication these are marked unverified. Optional `username` and `avatar_url` change the webhook's display. Set `language` per route for supported public event text.

### Enable the bot

1. Create a bot application in the [Discord Developer Portal](https://discord.com/developers/applications)
   and copy its bot token.
2. Install it to your Discord server with the `bot` and `applications.commands`
   scopes. See Discord’s [installation guide](https://docs.discord.com/developers/quick-start/getting-started).
3. Allow the bot to view and send messages in the channels you use. Enable
   **Message Content Intent** on its Bot page for ordinary chat.
4. Enable Developer Mode in Discord to [copy server, user and channel IDs](https://support.discord.com/hc/en-us/articles/206346498-Where-can-I-find-my-User-Server-Message-ID).
5. Fill the `bot` block in `discord.yml`, replacing these example IDs:

```yaml
bot:
  enabled: true
  token: ''
  guild_ids: [123456789012345678]
  admin_user_ids: [234567890123456789]
  admin_channel_ids: [345678901234567890]
  chat_channel_ids: [456789012345678901]
```

**Who can use it?** Slash commands require an approved Discord user, guild and admin channel. The IDs here are Discord IDs, not Steam IDs. Text from an approved admin in an admin channel appears in-game as `[Discord] Admin`. Ordinary messages in a public chat channel use the Discord display name. Admin rules win if a channel is in both lists.

Ordinary Discord text becomes an in-game shout, not a command. To send game replies back, select `chat.shout` on a webhook; local, whisper and clan messages are not relayed. `discord.shout` can also post Discord-originated messages; if you send them back to the same channel, both the original and the webhook copy will be visible. Bot/webhook input is ignored to prevent loops.

Keep the bot token and webhook URLs private. `SERVERMANAGER_DISCORD_BOT_TOKEN` overrides `bot.token`. The bot stops when the server does; it can't start an offline server. Try `/status`, then `/discordstatus` for diagnostics. `/discordtest` sends a test to routes selecting `server.announcement`.

The bot uses Korean descriptions and notices for Discord locale `ko`, English otherwise. Command names, detailed results and external console output are not translated. Command description overrides take effect at the next registration; webhook language is independent.

Use distinct, stable route names when editing a live config. An invalid named route can retain its last working settings in memory while valid routes update. Invalid file-level YAML retains the active configuration. Deleted or disabled routes stop; after a restart invalid entries start disabled.

## Scheduled commands

Scheduling works without Discord. The server creates `cron.yml` when the world is ready; `jobs: []` runs nothing. This is a complete minimal file for one evening announcement. If you already have jobs, append only the list entry under your existing `jobs:` key.

```yaml
timezone: local
interval: 10
jobs:
  - command: 'announce Good evening, Vikings!'
    schedule: '0 21 * * *'
```

`schedule` uses `minute hour day month weekday`; an optional first field adds
seconds. Keep it quoted. `timezone` can be `local`, `UTC` or an installed timezone
ID. `interval` is how often to check jobs, in seconds—not how often each job runs.

Use `command` for one command or `commands` for an ordered list.
`commands` wins if both are present. Remove or comment out a job to disable it.
Use flat ServerManager command names without `sm:`, or other installed
server-console commands. They run with server authority, not as shell commands.

A failed step stops its batch. Scheduled `save` waits for checkpoint confirmation. Ordinary jobs skip missed occurrences; other mods' commands may start work asynchronously. See the generated file for `globalKeys`, `bannedGlobalKeys`, `chance` and `useGameTime`. Zone/join triggers and DiscordConnector delivery from NewCron/CronJob are not supported.

### Hideable broadcasts

Let players hide repeated tips without hiding important notices:

```yaml
jobs:
  - command: broadcast center <color=yellow>Server restart in two minutes.</color>
    schedule: "28 5,17 * * *"
    allowClientHide: false
  - command: broadcast center <color=yellow><size=32>Use the hammer to save a house blueprint.</size></color>
    schedule: "14-59/30 * * * *"
    allowClientHide: true
```

The flag applies only to the job's `broadcast center` messages. With `true`, the game client can hide them without skipping the job or its logging. Unmarked broadcasts still use the installed console command. A job enabling this flag must contain a center broadcast. Messages can contain color and size tags and are limited to 2,000 UTF-8 bytes.

### Job history and maintenance

- Edit `cron.yml`, **not** `cron_last.yml`. The latter records progress. Don't delete it or `.cron-writer.lock` to replay a job. Both filenames also accept `.yaml`, but never keep both suffixes for the same file. Stop the server before renaming the progress file. NewCron's progress file is not interchangeable. Missing or damaged progress can suspend scheduling.
- `logJobs: false` and `logSkipped: true` are the defaults for BepInEx **console** messages; failures still warn. Select file records in `log.yml` (`cron.failed` or `cron.executed`), not a per-job `log` field. Select `cron.executed` on a webhook for one final report per job.
- For Upgrade World, back up before enabling world changes. Use `commands` for an ordered maintenance batch. Queued operations (including `world_clean` and `clean_*`) require `start`. Use plain `save` explicitly if wanted; the adapter adds no pre/post saves. If Upgrade World's root users are restricted, allow dedicated-console user `-1`.
- Maintenance can catch up once after downtime. If a job is interrupted or marked **needs review**, inspect the world and backups, use `sm:cronstatus`, then `sm:cronack <jobId>` only once work is idle. Acknowledging **skips** that occurrence; it does not retry or undo it.
- If FreshWorld is absent and ServerManager cannot track the installed Upgrade World implementation, a job with exactly one eligible world-change command may be **dispatched, completion not tracked**. This does not bypass missing plugins or denied permissions and is not confirmation that the work finished. See [maintenance rules and recovery](freshworld-maintenance.md) before enabling resets.

## Client menu and translations

Edit `BepInEx/config/sighsorry.ServerManager.cfg` on the **client**. The `[1 - Client]` settings are:

| Setting | What to enter |
| --- | --- |
| Server Address | Dedicated IPv4 or DNS with optional port (default 2456); use `steam:<host Steam64 ID>` for a friend hosting in-game. Empty keeps the normal Start flow. Hold Alt when clicking Start to choose another world. |
| Server Password | Leave blank for the game's prompt. A value here is **plain text**; do not share a CFG containing a private password. |
| Server Button Text | Defaults to `Start Modded Valheim Server`. Character selection still comes before joining. |
| Logo Path | Use a path relative to `BepInEx` such as `plugins/MyModpack/logo.png`, or a direct HTTPS PNG (not a webpage). The shipped default is a remote logo; blank restores vanilla. Maximum 8 MiB and 4096 × 4096. |
| Show Event Notifications | Show or hide ServerManager's top-center announcements, death and boss messages. Does not affect vanilla messages or webhooks. |
| Show Optional Broadcasts | Show or hide cron tips designated `allowClientHide: true`. Important unmarked broadcasts remain visible. |

Restart after editing the CFG. Both notification toggles can also be changed live with Configuration Manager. Neither toggle hides personal admin command receipts.

The lobby preview lists optional mods from the configured server and hides already installed matching versions. Patchers are labeled. It refreshes about once a minute; admission still uses exact hashes. For dedicated servers, allow the Steam query port (usually UDP 2457 for game port 2456); DNS must resolve to IPv4. For a local host, use the **host account ID**, not a lobby ID; the host must be a Steam friend with visible Valheim lobby information. A missing preview does not block Start.

For player messages, English and Korean are embedded. To add a language, copy the packaged `ServerManager.English.yml` to `ServerManager.<Language>.yml`, translate the values and keep the placeholders. A copy directly in `BepInEx/config` wins over one distributed elsewhere under `BepInEx`. Avoid duplicates and restart after edits. Install the translation on the server too for webhook messages and set that route's `language`. Missing translations fall back to English.

## Log files

The top-level keys in `log.yml` take lists directly (**no** `events` or `enabled` nesting). Defaults:

```yaml
audit:
  - server.started
  - server.status
  - server.saved
  - server.announcement
  - player.connection
  - player.death
  - boss.killed
  - raid.status
  - command.executed
  - moderation.action
  - cron.failed
  - connection.rejected
  - security.alert
  - security.admin_bypass
  - character.validation
  - character.shadow_stalled
  - character.revision_observed

player:
  - player.connection
  - player.position
  - player.death
  - combat.damage
  - inventory.changed
  - inventory.snapshot
  - skill.changed
  - character.save_rejected

chat:
  - chat.shout
  - chat.normal
  - chat.whisper
  - chat.clan
  - discord.shout
```

Remove a selector to stop those records, or use `chat: []` (likewise `audit` and `player`) to disable a file category. An omitted category uses its defaults. Invalid edits retain the last valid selection. No existing log is deleted by changing these filters. They do not change game events, chat delivery, command audit policy on other outputs, character saves, anti-cheat responses or Discord routes.

| File under `logs/` | Contents |
| --- | --- |
| `events-audit.log` | UTC server events, commands, security and rejected connections. Rotates by size. Event groups select the original event names; e.g. `player.connection` covers first join, login and leave. `command.executed` is for manual commands; `cron.failed` (default) or `cron.executed` selects scheduled final results without duplicating them. |
| `events-chat.log` | UTC game and relayed chat; rotates by size. Whisper and clan text can be private. Keep access restricted or remove these selectors. |
| `<Steam64>/<name>_<PlayerID>_<date>.log` | Player activity in server local time: connections, position, deaths, damage, skills and inventory. `inventory.changed` is item-count deltas; `inventory.snapshot` includes full item/custom data. |

Standalone positions are recorded about every five minutes; other events may still include coordinates. Inventory snapshots are checked every five minutes; unchanged contents aren't repeated after the first snapshot for a session/date. Current player logs remain plain text; finished parts and past dates are compressed as `.gz`. Up to **30 files per account**, including rotated parts, are kept — not 30 days. Extract an archive to read the original log.
