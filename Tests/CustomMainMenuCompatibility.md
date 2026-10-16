# CustomMainMenu compatibility

The original integration was reviewed against Custom Main Menu 1.2.4
(`rdmods.custommainmenu`), SHA-256
`9D1DEE80E48C6CDCA7B7078D23FA1EC17C83425C73EE55DAD1E2D16CC9667407`,
and the preserved original Valheim 1.0.12 client menu code.

ServerManager reads the installed plugin's General/Enabled setting without binding
or saving external settings. When active, CustomMainMenu owns changelog, clutter
visibility and logo. ServerManager retains quick connect and exposes Guide and
Allowed Mods through a Server Info dialog. No CustomMainMenu binary reference is
required. The original two-column presentation remains when the integration is inactive.

The 1.2.2 button-style fix was additionally reviewed against CustomMainMenu 1.6.0,
SHA-256 `9F9741FC87C962581188DB5FFEA9ECB48E7CD491A1008FDFB235411948CBAEE4`,
and original Valheim 1.0.20 client menu code. CustomMainMenu clones the Start button
for its Discord link after ServerManager's SetupGui hook. ServerManager therefore
changes only label text/localization while that integration is active, leaving
the template's typography unchanged. Without it, long-label auto-sizing remains.
Style restoration includes fontSize and only restores values ServerManager changed;
the baseline is captured when styling starts, not during text-only use.

The dialog owns input while open; closing restores the previous UI selection.
Its temporary canvas sorts above menu content and blocks background pointer input.
Closing/scene changes pause catalog queries; disposal releases only owned UI.
Layout is refreshed on size/text changes. Integration detection uses the installed
plugin/config dictionaries, not filesystem searches or per-frame reflection.

## Automated verification

- Debug build with DeployToGame=true, including final DLL merge.
- ClientMenuBrandingSmoke.ps1 against original game assemblies: existing endpoint,
  reflection, Harmony, bounded logo, layout and lifecycle checks, plus optional
  dependency/configuration and guarded shared-UI contracts. Button-style checks
  cover CMM/ordinary branches, one-time capture, owned restoration and updates to
  existing labels when compatibility changes.
- Compare built and installed final DLL SHA-256.

These are compilation and isolated/metadata checks, not Unity scene execution.

For 1.2.2, Debug/Release builds and the menu, runtime-member-access and plugin
incompatibility smoke checks passed. The deployed Debug DLL matched its build.
The Release logo URL harness passed 697 checks using fake HTTP only. Its Debug
run was blocked while initializing Harmony by the installed Mono.Cecil.dll's
Windows download-zone restriction; no security settings or game DLLs were changed.

## Pending in-game checks

- No CustomMainMenu, General/Enabled=false, and enabled: original layout versus
  Server Info button; enable/disable between menu visits.
- Restart Valheim with CustomMainMenu enabled and quick connect configured:
  normal-sized Discord title/link, correct Start text and a working Discord click.
  Check long Start text, character selection, language changes and cinematic return.
  Existing Discord clones are not restyled by ServerManager. Restart after updating;
  live CMM activation can create its clone before ServerManager's next UI update,
  so the fix does not guarantee repair of a button created during that transition.
- Custom changelog AutoOpen on/off, empty/nonempty text and live text updates;
  collapse/reopen, settings/language/cinematics and return from a world.
- Both logo settings populated, including a pending ServerManager HTTPS download
  when compatibility activates. No late logo takeover or destroyed foreign sprite.
- 1280x720, 1920x1080, ultrawide, window resize, long translated guide/catalog.
- Guide/Allowed Mods/Close, Escape, controller cancel and horizontal navigation;
  no activation or scrolling of background controls. No endpoint disables Mods
  while preserving navigation between Guide and Close.
- Endpoint changes, unavailable catalog, repeated open/close: no overlapping
  panels, scroll resets each frame, duplicate queries or leaked UI on menu recreation.
- Quick connect, Alt bypass, Steam invite, rejected connection and menu recovery.
- Remote-client graceful logout with pending character save. CustomMainMenu's
  ContinueLogout postfix does not check __runOriginal, so it may change menu/music
  state during ServerManager's save deferral. The external DLL is unchanged;
  reproduce before adding a targeted external fix. Do not remove save deferral.

The automatic Debug deployment targets the ordinary Steam BepInEx/plugins folder,
not the separate Gale customtest profile. Use the built DLL in the intended test
profile before testing that combination.
