# Game files code (fingerprint)

A Kohan II multiplayer match is lockstep: every PC simulates the same world from its own copy of
the game data, and any difference makes the match go out of sync. When a player joins, the game
compares only data *archives* (`Data.rwd`, `Music.rwd` and `addmod` mods, by their stamps). It
never looks at loose files in folder depots such as `Documents\Kohan2\data`, where most players
install mods by hand. So two players with different loose files can join each other and then
desync, often within seconds of the start.

The game files code is a short code (`7F3A-91C2`) for the game data one install actually loads.
Players whose codes match load exactly the same data. It shows in the launcher's header
("Game files: ..."), with **Copy** and **Details...** (save a report, compare with another
player's report), and on the command line:

```
k2mod fingerprint                      print the code
k2mod fingerprint --report <file>      also write the report to send to other players
k2mod fingerprint --compare <file>     list what differs from another player's report
                                       exit code 0 = same, 1 = different, 2 = report unreadable
```

The code depends only on what the game loads, not on how it was installed: the same files copied
loose into `Documents\Kohan2\data`, packed into an `.rwd` by the launcher, or installed from a
`.k2mod` package give the same code (`tests/fingerprint.ps1` checks this).

The implementation is `src/Core/Fingerprint.cs`. This page is its exact definition; change both
together, and change the format tag whenever the definition changes.

## Load order

The depots and their order come from the game's own startup scripts, read the way `k2.exe` runs
them (the game's log prints every script and command in this order):

1. `startup\autoexec.txt`, then every other `startup\autoexec*.txt` in name order (ordinal,
   ignoring case). The launcher's `autoexec_mods.txt` and the Steam game's `autoexec_wc.txt` are
   among them; scripts in `startup\disabled\` are not run.
2. Each `adddepot <path> [priority]`, `addmod <path> [priority]` and `addlocaledepot <path> [priority]`
   adds one depot: a folder of loose files (path ends in `/`, or is a folder) or an `.rwd` archive.
   Priority is 0 when not given. A path is relative to the game folder; `%USERDATA%` is
   `Documents\Kohan2`. Depots that do not exist are left out (the engine only scans what is there).
3. Depots are ranked by priority, ties in the order they were added; the highest rank wins a file.
   So at equal priority the depot added last wins, and `Documents\Kohan2\data` (priority 1) beats
   every priority-0 archive. This rule was reverse-engineered from `k2.exe`.

With the Steam game's scripts and the launcher's mods this gives, lowest rank first:
`Data.rwd`, `<game>\data`, `Music.rwd`, the launcher's mods in list order, `Warchest\Warchest.rwd`
(and the `Warchest\` folder), then `Documents\Kohan2\data` and `<game>\Userdata` (priority 1).

**Unverified:** how the engine ranks `addlocaledepot` depots. The launcher ranks them like
`adddepot`. Evidence for this: the game's log scans the folder depots in the order `data/`,
`Warchest/`, `Documents\Kohan2\data`, the same order as this ranking. The game also leaves locale
depots out of its join checksums. One visible effect: `Warchest.rwd` carries its own
`AVars_version.tgi` (the version label and intro videos), which outranks that file in a mod
loaded with `addmod`, but not a loose copy in `Documents\Kohan2\data`. So a mod that ships
`AVars_version.tgi` (Immortals does) gives a different code installed loose than packed, and the
comparison names that one file.

## What the code is made from

The code is the first 8 hex digits (upper case, shown as `XXXX-XXXX`) of the SHA-256 of this text,
UTF-8, one item per line, LF line endings, TAB between fields:

```
k2fp 1
exe	<SHA-256 of k2.exe, or "none">
base	<label>	<stamp>	<bytes>	<directory SHA-256>          one line per base archive, in load order
file	<path>	<SHA-256>                                  one line per counted file, sorted by path
```

- **Format tag:** `k2fp 1`.
- **`exe`:** SHA-256 of the whole `k2.exe` (lower-case hex). The program decides how the data is
  used, so different game versions (a Steam beta, a changed exe) give different codes.
- **`base`:** `Data.rwd` and `Music.rwd` in the game folder, the Steam game's own archives. They are
  too big to hash on every check (about 470 MB together), so each is identified by its label
  (lower case, `data.rwd`), its stamp (8 hex digits, the value the join check compares), its length
  in bytes, and the SHA-256 of its directory: one line `name TAB offset TAB size LF` per entry, in
  the archive's own order, names as stored. Files that only a base archive provides are covered by
  this line.
- **`file`:** every game file that a mod archive (any other `.rwd`, including `Warchest.rwd`) or a
  folder depot provides, with the SHA-256 of the copy the game uses (the highest-ranked one). Paths
  are lower case with `/` separators, sorted ordinally.

A file is **left out** when:

- its path is not game data, by the same rules `LooseMods.IsModFile` uses for hand-installed mods,
  applied to archives and folders alike (so a loose install and a packed one count the same files):
  - anything under the player's own folders at the top of a depot: `Save`, `saves`, `CampaignSave`,
    `DynamicCampaignSave`, `Films`, `Maps`, `RandomMap`, `Screenshots`, `synclogs`, `User`
    (settings such as `User\UVars.tgi`);
  - files the game writes for the player: `.rsg`, `.rfm`, `.rcs`, `.rup`, `.log`, `.dmp`;
  - archives inside a folder depot (`.rwd`, `.rti`);
  - work files: documents (`.doc`, `.docx`, `.pdf`, `.md`, `.rtf`), image editor and 3D files,
    backups, archives (`.zip`, `.rar`, `.7z`, `.k2mod`), `mod.json`, `thumbs.db`, `desktop.ini`,
    and names starting with a dot;
  - files that are not in `Data.rwd`, not `.tgi`, and not inside one of the game's data folders
    (`Units\`, `Buildings\`, ...), such as a `readme.txt` next to the mod's folders.

  These are the player's own files or not game data: they differ between players without changing
  the match.
- a base archive outranks every other copy (the base line covers it);
- the copy the game uses is byte-identical to the base archive's copy. A hand install often carries
  unchanged game files, a `.k2mod` package never does; both load the same data.

The game folder's own `data\` files are counted (the Steam game ships
`UI/Shared/options_dialog.tgi` there): they are game data the game loads.

## Not covered

- **Game plugins** (`winmm.dll`, `Mods\*.dll`): they change the program, not the data, and a
  plugin need not run on every PC.
- **`user.txt` scripts** (multiplayer autosave) and **settings** outside the depots.
- **An archive changed in place** in a way that keeps its stamp, length and every entry's name,
  offset and size (`Data.rwd`, `Music.rwd` only; every other file is hashed whole).
- **`.rwd` files dropped inside a folder depot**: not counted, like `LooseMods`. Whether the engine
  mounts them is unverified.

## Report

Plain text, UTF-8, CRLF, about 80 bytes per file. No absolute paths and no user names: depots
appear by their labels (`Documents\Kohan2\data`, `<game>\data`, `Mods/immortals.rwd`).

```
# Kohan II game files report, made by Kohan II Mod Manager 0.1.0-beta.5.
# ...
format: k2fp 1
code: 7F3A-91C2
k2.exe: <SHA-256>
archive: Data.rwd | stamp dbf9c0e8 | 386300748 bytes | directory <SHA-256>
archive: Music.rwd | stamp 89317623 | 86960244 bytes | directory <SHA-256>
files: 212
# first 12 digits of the file's SHA-256 | file | where the game takes it from
642f4812c383 | Audio/sound_death_k2.tgi | loose: Documents\Kohan2\data
7863c23dbae5 | Fonts/font_large.tgi | Warchest/Warchest.rwd
```

Lines starting with `#` are comments. Paths keep the game's own spelling. A report with another
format tag, a damaged header, or a `files:` count that does not match its lines is refused.

## Comparing

Comparing my install with another player's report lists, in plain words: a different `k2.exe`;
a base archive that differs or that only one side loads; files whose short hashes differ; files
only my game adds or changes (the other game uses the game's own copy or has no such file); files
only theirs does. The codes decide: equal codes mean the same data.

## Speed

`k2.exe` is hashed whole (about 6.6 MB), base archives only by their directories. File hashes are
cached for the launcher's session by (path, length, last write time), so a check after a change
reads only what changed. The launcher works the code out in the background after every change it
makes and whenever its window comes back to the front.
