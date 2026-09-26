# ServerManager

Mod control, server-side characters, player logs and Discord tools for Valheim.

Works with dedicated servers and multiplayer worlds opened from the game.
The local host’s character is managed too.

![](https://i.ibb.co/v4mhmYYV/Screenshot-2026-09-09-110138.png)
optional mods list is available on lobby.

![](https://i.ibb.co/sv59THTf/Screenshot-2026-09-09-105951.png) <br>
2-way discord chat and rcon command on server through discord channel


## Features

- Allow only approved mods, checked by exact DLL hashes (SHA-256).
- Save characters on the server and restore them when players return.
- Keep native `.fch` backups and restore characters with admin commands.
- Monitor cheat tools, cheat commands, player stats and forbidden items.
- Record player activity, inventories, chat and server events.
- Send Discord notifications, relay chat and run admin commands from Discord.
- Schedule server commands, including supported Upgrade World maintenance.
- Customize the main menu with a logo, quick connect and an optional-mod list.

No preloader patcher, separate bot program or extra RCON listener is needed.

## Install and start

1. Install `ServerManager.dll` in `BepInEx/plugins` on the server and all clients.
2. Disable crossplay. Remove ServerCharacters, MaxPlayerCount and old standalone
   OrbOfDiscord DLLs. Avoid other mods that manage the same character saves.
3. Start the dedicated server, or open a world with **Start Server**.
   ServerManager creates its files under the Valheim save folder.
4. Copy your approved mod DLLs into `required` and `optional`.
5. Edit `ServerManager.yml`. Set up `discord.yml` only if you want Discord.
6. Check the server log for errors, then join with a new character or an approved
   existing save.

By default, an existing local character with world progress cannot join unless
the server already has its save. The player is asked to create a new character;
their local save is not overwritten by this rejection.

New server characters keep the skin color, hair, beard, hair color and body model
selected during character creation. Starting items and progression follow the
server rules. Once a server save exists, its appearance is restored instead;
later in-game appearance changes are included in full character saves.

## Where files are stored

Server data is separate from mod-manager profiles.

On Windows:

```text
%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\ServerManager
```

If you use Valheim’s `-savedir` option, the folder is `<savedir>/ServerManager`.
Dedicated servers and local hosts use the same rule.

```text
ServerManager/
├── ServerManager.yml       Server rules and starting items
├── discord.yml             Webhooks, bot commands and chat
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

The editable YAML files reload automatically. **Invalid edits keep the last valid
settings.** Use spaces, not tabs, and keep key names exactly as shown. Existing
files are not rewritten when defaults change.

Characters share this storage across worlds. For independent servers on one
computer, use separate `-savedir` folders. Leave generated lock files alone;
a lock file remaining after shutdown is normal.

Ordinary remote clients do not create the server settings or character store.
Keep server settings, Discord credentials and character files out of modpacks.

## Required and optional mods

Copy trusted DLLs into these folders on the server:

| Folder | What players need |
| --- | --- |
| `required` | Every listed mod, with an approved DLL hash. |
| `optional` | The mod is optional, but its DLL hash must match if installed. |
| Neither | Unlisted BepInEx plugins are rejected for non-admin players. |

These are **reference copies**. Putting a DLL here does not install the mod.
Mods that must run on the server still belong in its `BepInEx/plugins` folder.

- ServerManager adds its own running DLL to the required policy automatically.
- Folder changes reload in the background. Update these copies when changing
  your modpack; the updated policy applies to new connections.
- Multiple approved versions can share one folder. Do not put the same plugin
  in both folders.
- Invalid updates keep the previous policy. While the first policy is still
  loading, players may need to retry joining.
- Verified admins may use extra plugins or mismatched optional mods.
  **Required mods still apply to admins.**

Only BepInEx plugins are checked. Standalone libraries such as
`Newtonsoft.Json.dll` and `YamlDotNet.dll` are skipped even when copied into
`required` or `optional`; skipped files are reported when the policy loads or
reloads. Their presence, version and hash do not affect admission. A companion
DLL that registers a BepInEx plugin (such as Newtonsoft.Json Detector) still
follows the normal plugin rules. Libraries merged into a plugin remain part of
that plugin's file hash. Native DLLs are not supported as references.

These checks do not prove that a client’s memory or external tools are unmodified.

## Server settings

The defaults in `ServerManager.yml` are:

```yaml
serverSettings:
  maxPlayers: 24
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

- `maxPlayers`: simultaneous players, from 1 to 64. Hosts and admins use a slot.
  Lowering this does not kick players already online. Higher counts need testing
  with your server and modpack.
- `maxCharactersPerAccount`: new character registrations per Steam account.
  Lowering it does not block existing characters. Verified admins and the local
  host are exempt.
- `backupsPerProfile`: previous saves kept per character, in addition to the
  current save.
- `forbiddenItems`: exact item prefab names, such as `[SwordCheat]`. Incoming
  saves containing them are rejected; findings in existing server saves are
  logged. Verified admins and the local host are exempt from this item rule,
  but not from corrupt data or identity checks.
- Both `action` fields accept `log`, `kick` or `ban`. Stat limits do not remove
  items or rewrite otherwise valid character data. Excessive damage can still
  be blocked when the action is `log`.

Admin exemptions differ by feature. Stat-cap exemptions require admin-list
membership, even for the local host. Review warnings before choosing automatic
bans. Client-reported observations can be wrong or forged, and ServerManager
cannot make a client cheat-proof.

### Starting items

`startItems` replaces the entire starting inventory for **new server characters**.
A name alone gives one item; use `Wood, 5` for five. Use `startItems: []` for no items.

Existing saves, imported characters and reconnecting players do not receive these
items again. Invalid prefabs or an inventory that cannot fit reject the edit.

Wearable equipment starts equipped, subject to the game’s and other mods’ rules.
Weapons, shields and torches stay in the inventory. If several items use the same
equipment slot, the first in prefab-name order is equipped. The default kit equips
the crown and rag armor.

If you use InventorySlots, use version 1.4.5 or newer for its multiplayer
compatibility fix.

## Characters and backups

### Joining and saving

With `loadServerCharacterOnJoin: true`:

- Returning players receive the server’s latest accepted character state.
- Single-player changes do not replace the server’s character.
- New server characters receive the configured starting items.
- The same rules also protect the local host’s character.

The game and installed mods restore the server character's content. Missing item
prefabs are logged and skipped by the game; they do not prevent joining. Later
saves can replace the server character with this reduced inventory, so restore
a backup if removed items need to be recovered. Load-time changes to equipment,
item flags, skills and custom data are also allowed.

If loading throws an exception or another patch skips the original managed
load, ServerManager blocks saving and returns the player to the menu with an
explanation. Data-format, ownership and network checks still apply.

Inventory changes are sent frequently; full character updates are also sent
periodically and during save/logout handling. The server keeps accepted updates
in memory, so reconnecting to the same running server can restore progress not
yet written to disk. Remaining poison is retained from the latest full update.

If a remote client's snapshot contains two items in the same inventory slot,
ServerManager retries capture once per second for up to 10 seconds. Invalid
snapshots are never submitted. The client log identifies the slot and both
items; retry warnings are limited to once per 30 seconds per connection. If the
overlap persists, the client disconnects with an explanation. Existing save
acknowledgement and logout deadlines still apply. This grace does not repair
items or relax the server's validation; the inventory mod may still need a fix.

Character checkpoints are written after a successful world save, one character
at a time in the background. One failed write does not stop the world or other
characters from saving. Accepted in-memory updates continue during these writes.
Joins that need to read the character store and disk-based admin commands may
briefly return a busy message; retry shortly. Settings reload waits for the write.

ServerManager does not change the vanilla automatic world-save interval. Manual
commands, cron jobs and supported Upgrade World maintenance can request extra
saves. Background character writes do not remove the game's own world-save cost.

For planned shutdown, run `save` and wait for this server-log confirmation for
that save:

```text
WorldCharacterCheckpointCompleted ... pending=0
```

Use `sm:status` or Discord `/status` to check progress. A “World saved” webhook
alone is not a safe-shutdown check. The checkpoint includes updates already
accepted by its cutoff, not every action a client may have just taken.
Crashes and lost updates can still cause rollback, lost items or duplicates.
Local / Steam Cloud saves are not proof that the server has saved.

### Save local characters without loading the server copy

Set `loadServerCharacterOnJoin: false` to accept the selected local / Steam Cloud
character on each connection while still collecting server saves and backups.
This also allows progress from other worlds into the server. Starting items are
not applied.

This setting affects new connections only. Before switching back to `true`,
have players log out normally, run `save`, and check that the character
checkpoint completed.

### Use an existing ServerCharacters or vanilla save

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

## Administrator commands

Use `sm:<command>` in F5 or the server console, `/sm:<command>` in game chat,
and `/<command>` in Discord. Remote in-game admins are checked against the
server’s admin list; Discord uses its own user-ID list.

| Task | F5 / server console |
| --- | --- |
| Help and status | `sm:help`, `sm:status`, `sm:players [target]` |
| Announce or shout | `sm:announce <message>`, `sm:chat <message>` |
| Character information | `sm:characterlist`, `sm:characterinfo <target>` |
| Backups | `sm:characterbackups <target> [page]`, `sm:characterrestore <target> <backupId>` |
| Give items | `sm:giveitem <target> <prefab> <amount> [quality] [dataId]` |
| Teleport | `sm:teleport <target> to <player>`, or `sm:teleport <target> <x> <y> <z>` |
| Skills | `sm:skillget <target> [skill]`, `sm:skillset <target> <skill> <value>` |
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

Discord uses named options:

```text
/giveitem player:MyHero prefab:SwordIron amount:1 quality:4
/teleport player:MyHero to:Admin
/rcon command:save
/rcon command:"kick 76561198000000001"
```

For a name such as `띠 오`, enter it directly in Discord's `player` option,
without surrounding quotes. F5 uses `sm:giveitem "띠 오" Wood 1`.
Keep any leading/trailing spaces inside F5 quotes or inside the Discord option.

Use vanilla `save`, `kick`, `ban` and `unban` in the server console or through
Discord `/rcon`. There are no `sm:save` or `sm:kick` aliases. Use the dedicated
slash commands for ServerManager features: `/giveitem`, not
`/rcon command:giveitem ...` or `sm:...` inside `/rcon`.

Item grants need an online target with enough inventory space. Excess items are
not dropped. A timeout does not prove that an action failed: check the result
before repeating a grant or restore.

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

Use **webhooks** for game-to-Discord notifications.
Use the **built-in bot** for Discord commands and Discord-to-game chat.
You can use either one or both.

New `discord.yml` templates have disabled bot/webhook examples with blank credentials.
Fill in the credentials and set `enabled: true` for the components you want to use.
Existing files are not rewritten. Omitting `enabled` still means true.

On live reload, the bot and each named webhook are validated independently.
An invalid webhook keeps only the last working route with the same exact `name`;
other valid changes apply. A new invalid route stays disabled. Keep names unique
and stable: duplicate names retain at most one previous route, while unnamed
invalid entries cannot recover by their position. Explicit `enabled: false`
disables a route even if its URL or other value is invalid; deleted routes are removed.
Unknown mapping keys still invalidate that block. A malformed `webhooks` list
retains the previous list while allowing a valid bot block to reload.

Invalid YAML syntax, duplicate YAML keys, unknown root keys or file-level errors
retain all active settings. Diagnostics identify webhook entries by their list
index without printing names, tokens or URLs. Last working settings are kept in
memory only; after restarting, invalid entries start disabled.

### Webhooks: game → Discord

Create a webhook in the destination channel and copy its URL.
See Discord’s [webhook guide](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks).

For webhook-only use, replace `discord.yml` with this and fill in `url`:

```yaml
bot:
  enabled: false

webhooks:
  - name: Server feed
    enabled: true
    url: '' # Paste your webhook URL.
    events: [server.status, server.saved, player.connection, chat.shout]
    language: English
    anonymous_prefix: ''
```

| Event filter | Notifications |
| --- | --- |
| `server.status` | Server ready and normal shutdown |
| `server.saved` | World saved; character saves may still be pending |
| `server.announcement` | ServerManager announcements |
| `player.connection` | Remote player joins, first joins and leaves |
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

Use separate routes for public news and private admin alerts. Multiple routes
to the same channel may produce duplicate notifications. These filters select
messages; they are not a full copy of the audit log.

- Set `language: Korean` for Korean public messages; English is the default.
- Set `anonymous_prefix: Anonymous` for labels such as `Anonymous 1`.
  Names typed into chat or announcements are not hidden.
- Set `include_steam_id: true` on a private route to include verified remote
  players’ Steam64 IDs on joins/leaves. Anonymous routes always hide IDs.
- Optional `username` and `avatar_url` change the webhook’s display name and image.

Webhook destinations are independent of bot admin channels. Bot permissions
do not protect a webhook’s audience. Keep URLs private and replace any exposed
credentials.

### Bot: Discord → game

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

- `guild_ids`: Discord servers connected to this Valheim server.
- `admin_user_ids`: **Discord user IDs, not Steam IDs**. These users can run
  commands and send chat in admin channels.
- `admin_channel_ids`: command and admin-chat channels. Ordinary messages from
  approved admins appear in-game as `[Discord] Admin`.
- `chat_channel_ids`: public chat channels. Messages appear with the sender’s
  Discord display name; access follows Discord channel permissions.

All slash commands require an approved admin user and admin channel.
If a channel is in both lists, admin rules apply. Message Content Intent is
needed for either channel list, including admin-only setups.

Ordinary text becomes a global in-game shout, not a command. No prefix is needed.
For replies from the game, use a webhook with `chat.shout`. Local, whisper and
clan chat are not sent to public webhooks. To also log Discord-to-game messages,
add `discord.shout` to the webhook's `events`. It displays `[Discord] name: text`
without the author's Discord ID. With `anonymous_prefix`, Discord authors appear
as `<prefix> player` because they have no verified Steam account attribution.
Bot and webhook messages are ignored on input to
prevent relay loops. If input and output share a channel, the original message
and its webhook copy will both be visible.

Try `/status`. Use `/discordstatus` for diagnostics. `/discordtest` sends a test
to routes selecting `server.announcement`.

Keep the token on the server only. The environment variable
`SERVERMANAGER_DISCORD_BOT_TOKEN` can override `bot.token`.
No RCON IP, password or extra port is needed. The bot stops with the game server
and cannot start an offline server.

## Scheduled commands

The server creates `cron.yml` when the world is ready. Scheduling works without
Discord. The default `jobs: []` runs nothing.

Add `cron.executed` to a webhook route to receive one compact card after each
job. Intermediate command dispatches are omitted. Announcement, chat and broadcast
cards show their text without Valheim rich-text tags; other commands show only
their verbs. Remove `server.announcement` from that route if the same scheduled
broadcast should not also appear as an announcement event.

Example: announce each evening and save every 30 minutes.

```yaml
timezone: local
interval: 10
jobs:
  - command: 'announce Good evening, Vikings!'
    schedule: '0 21 * * *'
  - command: save
    schedule: '*/30 * * * *'
```

`schedule` uses `minute hour day month weekday`; an optional first field adds
seconds. Keep it quoted. `timezone` can be `local`, `UTC` or an installed timezone
ID. `interval` is how often to check jobs, in seconds—not how often each job runs.

Use `command` for one command or `commands` for an ordered list.
`commands` wins if both are present. Remove or comment out a job to disable it.
Use flat ServerManager command names without `sm:`, or other installed
server-console commands. They run with server authority, not as shell commands.

A failed step stops its batch. Scheduled `save` waits for a confirmed checkpoint;
other mods’ commands may finish their background work later. Ordinary jobs skip
time missed while offline. See the generated comments for conditions such as
`globalKeys`, `bannedGlobalKeys`, `chance` and `useGameTime`.

Remove NewCron/CronJob before using ServerManager scheduling. Timed-job settings
use their common format, but zone/join triggers and DiscordConnector delivery
are not supported.

### Upgrade World maintenance

Requires separately installed **Upgrade World**. Completion tracking checks the required runtime members rather than enforcing an exact plugin version.
Back up the world and test your commands before enabling maintenance.
Put related changes in one job. This example is disabled:

```yaml
timezone: local
jobs: []
# Replace jobs: [] with this block only when ready:
# jobs:
#   - schedule: '35 5 * * *'
#     commands:
#       - world_clean
#       - zones_reset safeZones=1 start
```

ServerManager adds confirmed saves before and after supported maintenance and
waits for its operations. Queued changes require `start`; unsupported operations
are refused. Maintenance batches may contain supported world changes and plain
`save`, not announcements or unrelated commands. If Upgrade World’s `Root users`
is restricted, include `-1` for dedicated-console execution.

If the completion-tracking contract is incompatible, a job with exactly one world-change command is dispatched through the normal server console instead. Its report says **Dispatched; completion not tracked**, not completed. This fallback adds no automatic pre/post saves and never advances a multi-command sequence. Missing plugins, replaced command handlers, permission denials and observer cleanup failures do not enable fallback. Untracked work may overlap later scheduled operations; choose intervals accordingly. A delivered occurrence is recorded without automatic retry. After an interrupted dispatch, review is still required.

Maintenance can catch up once after downtime. Failed or interrupted work pauses
later maintenance for that world. Inspect the world and backups, then use:

```text
sm:cronstatus
sm:cronack <jobId>
```

Discord equivalents are `/cronstatus` and `/cronack job:<jobId>`.
Use the ID shown by status. Acknowledging **skips** the interrupted occurrence;
it does not retry or undo it. Upgrade World must be supported and idle.

Avoid manual world-changing commands and `uw_check` during maintenance.
`force` and `safeZones=0` can remove base protection. Automatic saves do not
provide rollback or make a reset crash-safe.

Keep `cron_last.yml` and `.cron-writer.lock`. Do not edit/delete them to retry
work or replace the progress file with NewCron’s `cron_last.yaml`.
Missing or damaged progress suspends scheduling, not the other mod features.

Scheduling also accepts `cron.yaml` and `cron_last.yaml`. Keep only one extension
for each file; new files use `.yml`. Stop the server before renaming the progress
file. Active maintenance finishes before a changed schedule takes effect.

## Client menu and notifications

Client settings are in `BepInEx/config/sighsorry.ServerManager.cfg`.
Example for a dedicated server:

```ini
[1 - Client]
Server Address = play.example.com:2456
Server Password =
Server Button Text = Start Modded Valheim Server
Logo Path =
Show Event Notifications = true
```

- **Server Address:** dedicated IP/DNS name, or `steam:<host Steam64 ID>` for a
  local-host server. Use the host’s account ID, not a lobby ID. The default
  dedicated port is 2456. An empty address keeps the normal Start flow.
- **Server Password:** optional. Leave empty for the normal password prompt.
  If set, it is stored as plain text in the client CFG.
- **Server Button Text:** label for both Start buttons. Players still choose
  their character before connecting.
- **Logo Path:** PNG path relative to `BepInEx`, such as
  `plugins/MyModpack/logo.png`, or a direct HTTPS PNG URL—not an album/page URL.
  Leave empty for the vanilla logo. Images must be at most 8 MiB and 4096 × 4096.
- **Show Event Notifications:** shows ServerManager’s top-center announcements,
  deaths and boss messages. Turning it off does not hide vanilla messages or
  stop logs and webhooks.

Hold **Alt while clicking Start** to open the normal world/server selection.
Restart after editing the CFG. The notification toggle also applies immediately
when changed through Configuration Manager.

### Optional-mod preview

The upper-left menu panel shows optional mods from the configured server.
Already loaded, allowed versions are hidden. The preview refreshes about once a
minute; it does not replace the exact DLL-hash check when joining.

- Dedicated servers: the Steam query port must be reachable, usually UDP 2457
  for game port 2456. DNS must resolve to IPv4.
- Local hosts: use `steam:<host Steam64 ID>`. The host must be a Steam friend,
  have a Steam server open, and have visible Valheim lobby information.

A failed preview never blocks Start. Both sides need the same current
ServerManager version. No extra listener is added for local-host previews.

### Translations

English and Korean player messages are included.
Put `ServerManager.<Language>.yml` anywhere under `BepInEx`; use the packaged
`ServerManager.English.yml` as a template. A copy directly in `BepInEx/config`
takes priority over a distributed copy. Avoid duplicate distributed files and
restart after changes. For custom Discord languages, install the translation
on the server too.

## Logs

- `events-audit.log`: server events, commands, security findings and rejected
  connections. Uses UTC timestamps and rotates by size.
- `events-chat.log`: game chat, including optional Clan integration. Uses UTC
  timestamps and rotates by size.
- `logs/<Steam64>/<name>_<PlayerID>_<date>.log`: player activity using the server’s
  local date and time.

Player logs include positions, full inventories with custom data, skill changes,
damage and deaths. Regular position entries are five minutes apart, with join
and logout entries as well. Other events can include coordinates between those
updates. Damage is recorded before resistances, with one decimal place and
separate sent/received cooldowns.

Full inventory details are checked every five minutes. Identical contents are
not repeated, but the first snapshot after joining or on a new local date is
always recorded. Item-count change lines (`Inv:`) still record each accepted
change; character transfers and saves are unaffected.

Today's active player log stays plain text. Completed size-split parts and past
dates are compressed in the background as `.log.01.gz` and `.log.gz`. Existing
past logs are processed gradually after startup and at the local date change.
Extract a `.gz` file with an archive tool to read the original log. Compression
is verified before removing the plain copy; failures leave the original intact.
When late records arrive, the existing archive becomes a numbered part and new records use the plain active log.

Up to 30 player-log files are kept per Steam account, including compressed and
rotated parts. This is a file limit, not a 30-day limit. Server audit/chat logs
are unchanged. Logs can contain private player data; share them carefully.
