# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

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
