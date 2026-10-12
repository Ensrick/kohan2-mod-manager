# Kohan II Mod Manager

One small program for **Kohan II: Kings of War** (Steam): players install and switch mods with
a few clicks, and mod authors turn their work into a package that contains none of the
game's own files.

## Download

**[K2ModManager.exe](https://github.com/Ensrick/kohan2-mod-manager/releases/latest/download/K2ModManager.exe)**
(players), or the [latest release](https://github.com/Ensrick/kohan2-mod-manager/releases/latest)
for the zip with `k2mod.exe` (mod authors). Changes are listed in [CHANGELOG.md](CHANGELOG.md).

## For players

1. Download `K2ModManager.exe` and the mod you want (a `.k2mod` or `.rwd` file). Put them in
   a folder.
2. Double-click `K2ModManager.exe`. It finds Kohan II through Steam and offers to install any
   mod sitting next to it.
3. To add more mods later, click **Add a mod...** or drag the file onto the window. After
   the first run, double-clicking a `.k2mod` file opens it in the manager. A `.rwd` or
   `.k2mod` copied straight into the game's `Mods` folder also shows up in the list.
4. Tick the mods you want and click **Play Kohan II**.

A mod installed by hand (an `.rwd` in `Mods` plus its own `startup\autoexec_<name>.txt`) is
taken over the first time the manager opens: its startup file is moved to `startup\disabled\`
and the mod is ticked in the list instead, so nothing changes in the game.

Windows may show "Windows protected your PC" for a new download: click **More info**, then
**Run anyway**. It needs Windows 10 or 11 (the .NET Framework 4.8 it uses is built in) and
no installation or administrator rights.

- **Several mods at once:** tick them all. If two mods change the same file, the one lower in
  the list wins; the manager shows which files overlap. Mods that say they cannot run
  together are never ticked together: turning one on turns the other off.
- **Older mods** made before packages existed also work: a `.zip` of loose game files (it
  finds the data folder inside and leaves readmes out) or a ready-made `.rwd` archive.
- **Mods installed by hand** (files copied into `Documents\Kohan2\data`, the usual way for
  mods such as Immortals) override every mod in the list; the manager says so and offers
  **Pack it as a mod...**. That puts those files into one `.rwd` mod in the list, ticked, and
  moves the loose files (never deletes them) to `Documents\Kohan2\loose_backup_<date>\`.
  Online, you then match players who use the same packed mod, no longer players who
  installed it by hand. Command line: `k2mod loose`, `k2mod loose pack`.
- **Game updates:** mods built from patches are rebuilt automatically when Steam updates
  the game's data.
- **Multiplayer:** every player needs the same mods ticked, in the same order. The game
  checks this when you connect and refuses mismatched players.
- **Multiplayer autosave (new, not yet tried in a real match):** tick **Autosave multiplayer
  games**. When you host, the match is saved every 5 minutes as "MP Autosave A" and
  "MP Autosave B", taking turns. If a game crashes, host again, choose **Load saved game**
  and pick the newer one. It uses only the game's own script commands, so it changes no
  game data and does not affect the multiplayer version check. Details:
  [docs/MP_AUTOSAVE.md](docs/MP_AUTOSAVE.md).
- **Game plugins (new):** the **Game plugins** list turns DLL plugins on and off. They change how
  the game program itself works and are not part of the multiplayer version check. The first one,
  **Multiplayer Fixes**, is a work in progress (rejoining a match after dropping out); it does not
  change the game yet. Details: [docs/PLUGINS.md](docs/PLUGINS.md).
- **What it changes:** only `<game>\Mods\` and `<game>\startup\autoexec_mods.txt`, plus
  `<game>\user.txt` while multiplayer autosave is on and `<game>\winmm.dll` (the plugin loader)
  while a plugin is on. Old startup scripts it offers to turn
  off are moved to `startup\disabled\`, never deleted. Loose files in
  `Documents\Kohan2\data` override every mod; the manager warns when it finds game files
  there and moves them to a `loose_backup_<date>` folder only when you pack them into a mod.

## For mod authors

1. Make your mod as finished files in a folder laid out like the game's data
   (`Units\`, `Buildings\`, `Organizations\` ...). Edit copies of game files as usual.
2. In the manager, click **For mod authors: make a package...**, pick the folder, fill in
   name and version, and click **Make package**. Or from a command line:
   `k2mod pack <folder> --name "My Mod" --version 1.0`.
3. Share the `.k2mod` file. (**Make .rwd only**, or `k2mod rwd <folder or zip> -o MyMod.rwd`,
   builds the plain archive the game loads; it holds your finished files whole, game text
   included, so prefer the `.k2mod` for sharing.)

The package holds only your changes:

| Your file | In the package |
|---|---|
| A game text file you edited (`.tgi`, `.txt`) | A patch: your lines only. Game text you kept is a reference (file + line range), removed lines are a count and a hash. |
| Game art or another binary file you edited | A delta against the player's copy of that file; noise without the game. |
| A new text file built from a game file | A patch against the most similar game file. |
| New art painted over a game texture | A delta against that texture: found automatically for same-size art, or listed under *More settings* (`"bases"` in `mod.json`). |
| A file that is entirely yours | The file itself. |
| An unchanged game file | Left out. |

When a player installs the package, the manager rebuilds your exact files from their own
`Data.rwd` and checks every result against the hashes you packed. Settings are kept in
`mod.json` inside your folder:

```json
{
  "id": "my-mod",
  "name": "My Mod",
  "version": "1.0",
  "author": "You",
  "description": "What it does.",
  "website": "https://...",
  "requires": [],
  "incompatible": ["other-overhaul"],
  "loadAfter": [],
  "bases": { "Units/Human/MyKnight/MyKnight.dds": "Units/Human/Swordsman/swordsmanbase.dds" }
}
```

`requires` mods are turned on with yours and load first; `incompatible` mods are turned off
when yours is turned on; `loadAfter` puts yours later in the list so your files win.
The full format is in [docs/FORMAT.md](docs/FORMAT.md).

`k2mod.exe` does everything from a command line (`pack`, `check`, `rwd`, `install`, `list`,
`enable`, `disable`, `up`, `down`, `remove`, `refresh`, `loose`, `plugin`); run `k2mod --help`.

`k2mod test` starts the game with the installed mods (minimized, without taking focus),
waits for the main menu or an error, closes it and prints the errors and warnings from
its log; exit code 0 means the main menu was reached. A fatal data error is reported in
about 25 seconds, a clean start in about 30. Steam must be running.

## How the game loads mods

Reverse-engineered from `k2.exe` (Steam build) while making
[Battleborn](https://github.com/Ensrick/kohan2-battleborn):

- At launch the game runs every `startup\autoexec*.txt` in name order, after `autoexec.txt`
  has mounted `Data.rwd`.
- `addmod <archive>` mounts an archive as a mod. Archives at the same priority are searched
  newest first, so a later `addmod` overrides an earlier one, and every mod overrides
  `Data.rwd`. `Documents\Kohan2\data` (priority 1) overrides all of them.
- The multiplayer handshake compares each player's archive stamps and mod list.

The manager writes one `addmod` line per ticked mod into `startup\autoexec_mods.txt`, in
list order. Builds are deterministic, so every player who installs the same package gets
the same archive and stamp.

## Building

Requires the .NET SDK (6 or later) on Windows; the programs target .NET Framework 4.8. The game
plugins in `native\` need Visual Studio 2022 or its Build Tools with the C++ workload.

```
powershell -File build.ps1        # native plugins, then dist\K2ModManager.exe and dist\k2mod.exe
```

`tests\UiPreview` renders the windows to PNG files without showing them (layout checks).

## License

MIT, see [LICENSE](LICENSE). This repository contains no Kohan II game data.
