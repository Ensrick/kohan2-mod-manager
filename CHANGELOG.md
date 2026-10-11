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
