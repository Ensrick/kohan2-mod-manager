# Formats

## `.k2mod` package

A zip file (or the same layout in a folder):

```
mod.json
files/<game path>              whole files that belong to the mod
patches/<game path>.bbpatch    text changes against the player's own game file
patches/<game path>.bbdelta    binary changes against a game file
```

`<game path>` is the path inside the game's data, with `/` separators
(`Units/Human/pikeman.tgi`). Names are not case-sensitive and must be unique across the
three kinds. Installing builds one `.rwd` archive from all of them; patches and deltas
read the player's `Data.rwd` and every result is checked against the hash the author packed.

### `mod.json`

| Field | Meaning |
|---|---|
| `id` | 1-48 characters of `a-z 0-9 - _`. Folder name and the name other mods use. |
| `name`, `version`, `author`, `description`, `website` | Shown to players. |
| `requires` | Mod ids that must be installed and on. Turned on with this mod; load before it. |
| `incompatible` | Mod ids that are turned off when this mod is turned on (either side may declare it). |
| `loadAfter` | Mod ids this mod loads after when both are present. |
| `bases` | Author tool only: `{ "<new art path>": "<game file it was painted over>" }`. |

## `.bbpatch` (text patch)

UTF-8, LF line endings, one directive per line:

```
# comment
base <game path> sha256=<hex>        the file being changed (absent for a new file)
ref <game path> sha256=<hex>         every other game file referenced below
result sha256=<hex> bytes=<n>        the finished file, checked after applying
bom yes|no                           UTF-8 byte-order mark on the result
eol crlf | lf | crlf*120 lf*3 none*1 line endings of the result, run-length

@@ <line> -<count>[:<hash8>] +<count> @@ <section, informational>
+<text>                              one of the mod's own lines
= <game path> <first>-<last>         lines first..last (1-based) of a game file
```

At base line `<line>`, `-count` lines are removed (their joined text must hash to
`hash8`), then `+count` lines are produced from `+` and `=` items. Lines are compared
without their endings; the result's endings come from `eol`. Same format as
kohan2-battleborn's `tools/bbpatch.py`; the C# port produces identical patches.

## `.bbdelta` (binary delta)

```
BBDELTA1\n
{"base": "<game path>", "base_sha256": "...", "bytes": <n>, "result_sha256": "..."}\n
<zlib stream of (result XOR base repeated to length)>
```

## `.rwd` archive (TimeGate "TGCK")

Little-endian. `"TGCK"`, u32 2, u32 3 sections; Header (u32 2, u16 2 + UTF-16 `"K2"`,
u32 0, u32 stamp); Files (contents back to back, in load-index order); Footer (u32 count;
per file u16 length + UTF-16 name, u64 offset from the Files section, u64 size, u32 0,
u32 load index); then 3 × 96-byte section records (UTF-16 name[32], u64 offset, u64 size,
2 × u32, u64 size).

Archives built here sort files by lower-cased name and use as stamp the CRC32 of the
SHA-256 over (lower-cased name, NUL, bytes) for each file, so identical content always
gives an identical archive.

## Installed layout

```
<game>\Mods\<id>.rwd                    the archive the game mounts
<game>\Mods\<id>.k2mod                  the package (kept to rebuild after game updates)
<game>\Mods\mods.json                   load order, on/off, the Data.rwd each mod was built
                                        against, and each mod's mod.json ("manifest")
<game>\startup\autoexec_mods.txt        "addmod Mods/<id>.rwd" per ticked mod, in order
<game>\startup\disabled\                other startup scripts the player turned off
```

A `.k2mod` or `.rwd` put in `Mods\` by hand (or left after `mods.json` is lost) shows up in
the list, turned off, and is renamed to `<id>.k2mod` / `<id>.rwd` (unless that id is taken).
A package brings its mod.json and is built on the next start; an archive alone takes its name
and version from its file name (`Immortals_V2.1.rwd` is Immortals 2.1, id `immortals`). Manager builds
before 2026-10-11 kept each mod in `Mods\<id>\` with its own `mod.json` (mods.json format 1);
the manager moves those up into `Mods\` the next time it starts while the game is closed.
