# ServerManager

Run a Steam multiplayer Valheim server with an approved modpack and server-side characters. Back up players, review activity, manage the server from Discord, or schedule routine work. It works on a dedicated server or a world opened with **Start Server**. Discord and scheduled jobs are optional.

![Valheim main menu showing the server's optional mods](https://i.ibb.co/v4mhmYYV/Screenshot-2026-09-09-110138.png)

Players can see which optional mods they haven't installed before joining.

![Discord chat relay and a server command reply](https://i.ibb.co/sv59THTf/Screenshot-2026-09-09-105951.png)

Admins can chat and run commands from Discord without opening an RCON port.

## Set up your first server

1. **Install on both sides.** Install BepInExPack Valheim (5.4.2360) and the same ServerManager version on the server and every client. For a manual install, put `ServerManager.dll` in `BepInEx/plugins`. Use matching game versions and **turn off crossplay**. If you already use a server-character mod, back up its saves before switching; do not run both.
2. **Start the world once.** Start your dedicated server, or select **Start Server** when opening a world in-game. Look for the new `ServerManager` folder in the **Valheim save directory**, not in `BepInEx/config`. On Windows the default is `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\ServerManager`; with `-savedir` it is `<savedir>/ServerManager`. Give independent servers separate save directories.
3. **Approve the modpack.** In that folder, copy required plugin and preloader patcher DLLs into `required/` and allowed extras into `optional/`. These are reference copies: install the actual mods separately. ServerManager includes itself automatically. Empty folders do **not** allow every mod.
4. **Choose who owns character progress.** In `ServerManager.yml`, under `serverSettings`, leave `loadServerCharacterOnJoin: true` to restore the server's character on each visit. Ask new players to create a fresh character, or [import their existing save](https://github.com/sighsorry1029/ServerManager/blob/main/docs/operator-guide.md#bring-an-existing-character). A progressed local character with no matching server save is refused, not overwritten. Choose `false` only if players should bring their local progress on **every** visit.
5. **Test before inviting players.** Join with the modpack as a **non-admin** so admin exemptions don't hide a mod mismatch. Reconnect with the same character. In the server console, run `sm:status` to check the world and `sm:players` to see who's online. If joining fails, check the server's BepInEx log and the affected client's `BepInEx/LogOutput.log`.

That's enough to play. Use the settings below to choose what else your server needs.

## Choose your server rules

Edit the generated **`ServerManager.yml`** on the server. Most edits reload while the world is running. If an edit is rejected, read the warning before assuming it took effect.

| Decision | Default and what to change |
| --- | --- |
| Player slots | Under `serverSettings`, leave `maxPlayers: 10` to let the game or another mod decide. Set **11–250** to override it next time the server starts or the host reopens the world. |
| Character slots and backups | `maxCharactersPerAccount: 3` and `backupsPerProfile: 30` under `serverSettings`. Lowering the character limit does not lock out existing characters. Verified admins and the local host bypass the creation limit. |
| Starting gear | `startItems` replaces the **whole** starting inventory for new server characters. The default equips a midsummer crown and rag armor and adds a torch. Use exact prefab names, e.g. `Wood, 5`; use `[]` to start with nothing. No repeat grants on reconnect or import. |
| Item and stat rules | `forbiddenItems` lists banned item prefabs. `statCaps` covers health, stamina, eitr, weight and damage; its default action is `log`. The damage cap defaults to **50000** and can block excessive damage even in log mode. |
| Cheat detection | Under `cheatDetection`, `action` defaults to `kick`. Choose `log`, `kick` or `ban`. A detector diagnostic is not proof of cheating and does not mean DLL checks were skipped. |

**How mod approval works:** Ordinary players need an exact approved SHA-256 match for every required DLL, and for every optional DLL they install. Unlisted plugins and patchers are rejected. Plain managed libraries such as `YamlDotNet.dll` are skipped. Verified admins may use extra mods or mismatched optional mods; **required hashes still apply**. Update the reference copies when your pack changes. The policy reloads for new connections; players still restart after changing patchers.

## Save, restore and administer

ServerManager keeps accepted character changes in memory, including the host's. It writes character checkpoints after a successful world save; it does **not** shorten Valheim's autosave interval. For a planned shutdown, run `save` and wait for **`WorldCharacterCheckpointCompleted ... pending=0`** in the server log. A “world saved” webhook alone is not enough.

To restore one character without stopping the server, have that player **log out and stay offline**. Save and wait for the checkpoint. Run `sm:characterbackups MyHero`, then `sm:characterrestore MyHero <backupId>` with the ID the first command returns. This restores the character, not the world. Never replace a live `.fch` by hand: server memory can overwrite it. For a missing primary save or the host's own character, use the [stopped-server recovery steps](https://github.com/sighsorry1029/ServerManager/blob/main/docs/operator-guide.md#restore-a-backup).

To make someone an in-game admin, run `sm:adminadd <Steam64>` in the server console. This uses Valheim's `adminlist.txt`; Discord admin IDs are separate. Use `sm:help` in F5 or the server console, `/sm:help` in game chat, or `/help` in Discord. Admins can manage players, access lists, items, skills, health, teleports, raids, world keys, announcements and backups.

```text
sm:giveitem "Some Player" SwordIron 1 4
sm:skillset "Some Player" Swords 50 notify=false
```

An online target normally gets **one private chat line** after an item grant or skill change. Add `notify=false` (Discord: `notify:false`) to skip just that message. The result and audit policy stay the same. For custom item data, save a preset in `Itemdata.yml` and pass its ID to `giveitem`. If a grant or restore times out, check the result before repeating it.

## Set up optional features

### Discord: notifications, chat and commands

In `discord.yml`, enable only what you need. The generated examples start disabled.

- **Game → Discord:** Create a webhook in the destination channel and paste its URL into a route. For a public feed, select `events: [player.connection]`: all remote joins and leaves, no Steam IDs by default. For an admin feed, use `events: [player.first_join]` and `include_steam_id: true`. “First” means a new **server character**, not a new Steam account. You don't need a bot for either feed.
- **Discord → game:** Enable the bot, set its token, and list the Discord guild, admin users and channels. Enable **Message Content Intent** for ordinary chat. Try `/status`. Use `/giveitem` for mod commands and `/rcon command:save` for server-console commands. Keep tokens and webhook URLs private.

[Webhook and bot setup, with copyable examples](https://github.com/sighsorry1029/ServerManager/blob/main/docs/operator-guide.md#discord)

### Schedules and logs

- **Run a job:** Add a command to `jobs` in `cron.yml`. For example, `command: 'announce Good evening, Vikings!'` with `schedule: '0 21 * * *'` announces at 21:00 daily. For a recurring `broadcast center` tip, set `allowClientHide: true` so players can hide it. Don't edit `cron_last.yml`: it records progress. [Scheduling examples](https://github.com/sighsorry1029/ServerManager/blob/main/docs/operator-guide.md#scheduled-commands)
- **Choose what to record:** Edit the `audit`, `player` and `chat` lists in `log.yml`. Player logs can show position, inventory, skills, damage and deaths. Omit a list for defaults or use `[]` to disable that category. The default audit records failed jobs, not every successful tip. Files are under `logs/`; older player logs are compressed. Keep inventory details, whispers and clan chat private. [Log files and filters](https://github.com/sighsorry1029/ServerManager/blob/main/docs/operator-guide.md#log-files)

### Client menu and language

On each client, open `BepInEx/config/sighsorry.ServerManager.cfg` → `[1 - Client]`. Set **Server Address** to a dedicated address such as `play.example.com:2456` or to `steam:<host Steam64 ID>` for a friend's hosted world. Leave the password blank to use the game's prompt. Set **Server Button Text** and **Logo Path** to customize the menu. **Alt + Start** still opens the normal world list.

**Show Event Notifications** controls top-center announcements, deaths and boss kills. **Show Optional Broadcasts** controls hideable cron tips. Neither controls private command receipts. English and Korean are included; you can add a `ServerManager.<Language>.yml` translation for players or for a webhook. [Logo paths, optional-mod preview and translation setup](https://github.com/sighsorry1029/ServerManager/blob/main/docs/operator-guide.md#client-menu-and-translations)

## Compatibility

- **ServerCharacters:** do not run both character managers at once. You can import native character saves; keep a copy of the originals. **InventorySlots:** use 1.4.5 or newer if installed.
- **Player limits:** if MaxPlayerCount, NetworkPerformanceSystem or Valheim Plus is installed, ServerManager leaves capacity to that mod. Authentication and character management still run.
- **Cron, Upgrade World and FreshWorld:** don't run the same schedules through NewCron/CronJob and ServerManager. Install Upgrade World to run supported maintenance jobs. With a compatible FreshWorld, tracked jobs share its lock to avoid overlap; they do **not** wait for a particular reset to succeed or add extra saves.
- **Clan and CustomMainMenu:** compatible Clan chat can be recorded privately. With CustomMainMenu, look in **Server Info** for join guidance and optional mods.
- **Security:** `printseeds` and `freefly` are restricted for non-admins on managed servers. DLL approval and client checks are safeguards, not proof of an unmodified client.

## Reference

- [Operator guide: files, recovery, command list, Discord setup, schedules and log filters](https://github.com/sighsorry1029/ServerManager/blob/main/docs/operator-guide.md)
- [Upgrade World and FreshWorld: coordination and recovery](https://github.com/sighsorry1029/ServerManager/blob/main/docs/freshworld-maintenance.md)
- [Server integration API for mod authors: events, player status, saves and moderation](https://github.com/sighsorry1029/ServerManager/blob/main/Events/IntegrationApi.cs)
