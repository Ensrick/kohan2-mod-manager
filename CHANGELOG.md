# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [0.1.0-beta.5] - unreleased

### Added
- Game files code (fingerprint): a short code such as `7F3A-91C2` for the game data an install
  actually loads, so a group can check before a multiplayer match that everyone's game loads the
  same data. The game's join check compares archive stamps only, never loose files in
  `Documents\Kohan2\data`, so players with different hand-installed files could join each other
  and desync at the start. The code is made from the SHA-256 of `k2.exe`, the identity of
  `Data.rwd` and `Music.rwd` (stamp, length, SHA-256 of the directory; never hashed whole), and
  the SHA-256 of the copy the game uses of every file mods and folder depots add or change, with
  the load order worked out from the game's own startup scripts (priority, then add order, last
  wins). Saves, films, screenshots, settings, logs and readmes are left out by the same rules as
  hand-installed mod detection, and so are files byte-identical to the game's own copy, so the
  same mod gives the same code installed loose, packed into an `.rwd`, or from a `.k2mod`.
  Launcher: "Game files: XXXX-XXXX" under the game folder with **Copy** and **Details...** (save
  a report, compare with another player's report in plain words), worked out again in the
  background after every change and when the window comes back to the front. CLI:
  `k2mod fingerprint [--report <file>] [--compare <file>]` (exit code 0 same, 1 different, 2
  unreadable report). Reports hold no absolute paths or user names. Definition in
  `docs/FINGERPRINT.md`; `tests/fingerprint.ps1` checks it on fake game folders (loose, packed,
  `.rwd` and `.k2mod` installs give the same code; one changed byte changes it and is named;
  mod order counts only for files two mods both change with different content; the player's
  own files do not count). On real data, a copy of a hand-installed Immortals 2.1 matches the
  packed archive in every file but `AVars_version.tgi`, which the Steam game's `Warchest.rwd`
  outranks in an `addmod` archive but not loose (ranking of `addlocaledepot` unverified).

## [0.1.0-beta.4] - unreleased

### Added
- Mods installed by hand: the manager finds loose game files in the game's folder depots
  (`Documents\Kohan2\data`, and the game's own `data\` and `Userdata\` folders), both files
  that replace game files and new ones, and leaves out saves, films, maps, screenshots,
  synclogs, settings, readmes and the `UI/Shared/options_dialog.tgi` the Steam game ships in
  `data\`. A notice names the mod (from its `AVars_version.tgi`, "Version = Immortals 2.1", or
  an installed mod with the same files) with **Pack it as a mod...**: a dialog asks for name and
  version, says that a packed mod no longer matches hand installs online ("Bad Depot
  Checksums"), then builds a deterministic `Mods\<id>.rwd`, ticks it, and moves the loose
  files to `loose_backup_<date>\` next to their folder (never deleted, with a README on how
  to put them back). Refused while the game runs, and when the name is taken by a mod built
  from a package. `k2mod loose` lists, `k2mod loose pack [--name N] [--version V]` packs. A
  copy of the hand-installed Immortals 2.1 packs byte-identical to the archive built from its
  zip (211 files, stamp e4747efb), so players who pack it match players who installed the zip
  through the manager. The `K2MODS_USERDATA` environment variable points the manager at
  another user data folder (tests only).

## [0.1.0-beta.3] - unreleased

### Added
- Game plugins: DLLs that run inside `k2.exe`, listed under **Game plugins** in the manager with an
  on/off box, and `k2mod plugin list | on <id> | off <id>`. Turning one on writes the plugin loader
  (`<game>\winmm.dll`, removed again when no plugin is on), the plugin (`Mods\<id>.dll`) and
  `Mods\plugins.txt`; the loader logs to `Mods\plugins.log`. The loader waits for the game's own
  code to run (the Steam exe decrypts itself after start-up) before starting plugins, and the
  manager never replaces a `winmm.dll` another program put there. Plugins are not part of the
  multiplayer version check. Source and tests in `native/`, details in `docs/PLUGINS.md`.
- First plugin, Multiplayer Fixes (`k2mp.dll` 0.1.0-dev.1), work in progress: it checks that it
  recognises `k2.exe` 1.3.68 where it will patch (verified against a decrypted copy of the exe
  mapped at a relocated address) and changes nothing in the game yet. Goal: rejoin a multiplayer
  match after dropping out.

## [0.1.0-beta.2] - unreleased

### Added
- Multiplayer autosave (#3): a tick box in the manager and `k2mod autosave on|off
  [--minutes N]`. While it is on, `<game>\user.txt` (run by the game after start-up) starts
  `Mods\mp_autosave.txt`, a loop in the game's own script language: wait for a match, wait
  N minutes (default 5), `GameSave` into "MP Autosave A", the same into "B", repeat. Only
  the host's save goes through, so every player can leave it on. No change to `k2.exe` or
  game data, nothing in the multiplayer checksums. Anything else in `user.txt` is kept, in
  its own encoding. Not yet tried in a real match; see `docs/MP_AUTOSAVE.md` for the test
  plan.

## [0.1.0-beta.1] - 2026-10-11

First public build.

### Added
- `K2ModManager.exe` (players): finds Kohan II through Steam; installs `.k2mod` packages,
  `.zip` files of loose mod files and `.rwd` archives (button, drag and drop, double-click,
  or packages next to the exe); tick to turn mods on, reorder, remove; shows files two mods
  both change and which one wins; turns off mods declared incompatible; rebuilds mods when
  a game update changes `Data.rwd`; warns about loose overrides in `Documents\Kohan2\data`
  and other startup scripts that mount data; Play button.
- Mod author window and `k2mod.exe pack`: turns a folder of finished files into a package
  with only the author's changes (patches and deltas against the player's game). Produces
  the same patches as kohan2-battleborn's `tools/bbpatch.py` (all 127 Battleborn patches
  identical) and builds Battleborn byte-identical to its Python build.
- One managed startup script, `startup\autoexec_mods.txt`, with an `addmod` line per mod.
- Flat layout (2026-10-11): each mod's `.rwd` sits straight in `Mods\` (`Mods\<id>.rwd`,
  next to its `Mods\<id>.k2mod`), no folder per mod; mod details move into `Mods\mods.json`
  (format 2). Mods in the old `Mods\<id>\` folders are moved up automatically while the game
  is closed. A `.k2mod` or `.rwd` dropped into `Mods\` by hand shows up in the list, off,
  under any file name: it is renamed to its id, a package is built on the next start, and an
  archive takes its name and version from its file name (`Immortals_V2.1.rwd` -> Immortals 2.1).
- `.rwd` archives for players (2026-10-11): an `.rwd` next to the manager is offered for
  install like a `.k2mod`; a raw archive takes its name and version from its file name
  everywhere; a copy installed by hand with its own `startup\autoexec_<name>.txt` is taken
  over (that script moves to `startup\disabled\` and the mod is ticked, so the game loads the
  same thing); the game's own `Data.rwd` / `Music.rwd` are refused as mods.
- `k2mod rwd` (2026-10-11) also takes a zip or folder of loose finished files, like
  `install` does. Immortals V2.1's zip packs to 211 files identical to the zip's (its two
  Word documents left out).
- Icon (2026-10-11): gold-rimmed Kohan II blue shield with gold "K2" ("MODS" ribbon from
  48 px up), on the exe, both windows and `.k2mod` files. Drawn by `art/make_icon.py`
  (Pillow, Georgia Bold / Segoe UI Black); the `.k2mod` registration re-announces itself
  once per icon version so Explorer redraws files registered before the icon changed.
- Kohan II colours (2026-10-11): blue buttons with gold lettering (the main button gets a
  gold edge), navy header with a gold title, sampled from the game's own menu textures
  (`UI/1280/Default/PushButton`, `Panel`). Replaces the green and beige buttons.
- Light and dark mode (2026-10-11): backgrounds, text, fields, lines, scroll bars, list
  highlight, status and notice colours follow Windows' app mode (Settings, Personalization,
  Colors) and switch live when it changes; the title bar goes dark too. Blue and gold stay
  the same in both. Palettes in `src/K2ModManager/Themes/{Light,Dark}.xaml`, switching in
  `Theme.cs`. `tests/UiPreview` takes `--dark` and `--notices`, and builds the windows in the
  other theme first so every render also checks the live switch.
- `k2mod test` (2026-10-11): unattended start-up test. Starts k2.exe minimized with the
  Steam app id in its environment (nothing written to the game folder), reads the live log:
  any `ERROR:` line (clean starts log none) is a failure, "(init time) command line
  scripts" plus 10 quiet seconds is the main menu (the engine buffers "connect
  MainMenuInterface" until exit). A private desktop was tried first and does not work: the
  renderer finds no adapter there ("No adapters available").

[0.1.0-beta.1]: https://github.com/Ensrick/kohan2-mod-manager/releases/tag/v0.1.0-beta.1
