# CustomMainMenu compatibility

Reviewed against Custom Main Menu 1.2.4 (`rdmods.custommainmenu`), SHA-256
`9D1DEE80E48C6CDCA7B7078D23FA1EC17C83425C73EE55DAD1E2D16CC9667407`,
and the preserved original Valheim 1.0.12 client menu code.

ServerManager reads the installed plugin's General/Enabled setting without binding
or saving external settings. When active, CustomMainMenu owns changelog, clutter
visibility and logo. ServerManager retains quick connect and exposes Guide and
Allowed Mods through a Server Info dialog. No CustomMainMenu binary reference is
required. The original two-column presentation remains when the integration is inactive.

The dialog owns input while open; closing restores the previous UI selection.
Its temporary canvas sorts above menu content and blocks background pointer input.
Closing/scene changes pause catalog queries; disposal releases only owned UI.
Layout is refreshed on size/text changes. Integration detection uses the installed
plugin/config dictionaries, not filesystem searches or per-frame reflection.

## Automated verification

- Debug build with DeployToGame=true, including final DLL merge.
- ClientMenuBrandingSmoke.ps1 against original game assemblies: existing endpoint,
  reflection, Harmony, bounded logo, layout and lifecycle checks, plus optional
  dependency/configuration and guarded shared-UI contracts.
- Compare built and installed final DLL SHA-256.

These are compilation and isolated/metadata checks, not Unity scene execution.

## Pending in-game checks

- No CustomMainMenu, General/Enabled=false, and enabled: original layout versus
  Server Info button; enable/disable between menu visits.
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
