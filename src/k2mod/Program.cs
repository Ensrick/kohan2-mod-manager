using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using K2Mods;

namespace K2ModCli
{
    /// <summary>Command-line twin of K2ModManager.exe for mod authors and scripts.</summary>
    static class Program
    {
        const string Usage = @"k2mod - Kohan II mod packaging and install tool

  k2mod pack <folder> [-o <file.k2mod>] [--name N] [--version V] [--author A] [--id ID]
        Package a folder of finished mod files (laid out like the game's data). Files the
        mod changes are stored as patches/deltas against the player's own game, so the
        package contains none of the game's files. Settings are kept in <folder>\mod.json.
        A folder already in package layout (mod.json + files\ / patches\) is zipped as is.
  k2mod rwd <package|zip|folder> -o <file.rwd>  Build the .rwd archive the game loads, from a package
        or from a zip or folder of finished files laid out like the game's data.
  k2mod check <package>                         Build it in memory against this game; report.
  k2mod install <package|.rwd|.zip|folder>      Install and turn on.
  k2mod list | enable <id> | disable <id> | remove <id> | up <id> | down <id>
  k2mod refresh                                 Rebuild mods after a game update changed Data.rwd.
  k2mod loose                                   List a mod installed by hand: loose game files in
        Documents\Kohan2\data (or the game's data\ or Userdata\ folder), which override every mod.
  k2mod loose pack [--name N] [--version V] [--id ID]  Pack those files into one .rwd mod in Mods\,
        turn it on, and move the loose files to a loose_backup_<date> folder next to them (never
        deleted). After this you can only play online with players who use the same packed mod,
        not with players who installed it by hand.
  k2mod autosave [on|off] [--minutes N]         Multiplayer autosave: whoever hosts saves the match every
        N minutes (default 5) into ""MP Autosave A"" and ""B"", so a crashed game can be resumed with
        Load saved game. Starts with the game's next launch. No argument: show whether it is on.
  k2mod plugin [list | on <id> | off <id>]      Game plugins: DLLs that the plugin loader (winmm.dll in
        the game folder) starts inside k2.exe. Not part of the multiplayer version check.
  k2mod fingerprint [--report <file>] [--compare <file>]  Game files code: the game data this install
        loads (k2.exe, Data.rwd, Music.rwd, and every file mods and loose folders such as
        Documents\Kohan2\data add or change). Players whose codes match load the same data; the
        game's own join check does not see loose files. --report writes the list to send to others,
        --compare lists what differs from another player's report: exit code 0 = same, 1 = different,
        2 = that report could not be read.
  k2mod test [--timeout S] [--visible]          Start the game with the current mods (minimized,
        without taking focus), wait for the main menu or an error, close it, report the
        errors and warnings from its log. Exit code 0 = main menu reached. Needs Steam.

Options: --game ""<Kohan II folder>"" (default: found through Steam)
         --data ""<Data.rwd>""        (rwd/check/pack without a game folder)";

        static int Main(string[] args)
        {
            try
            {
                return Run(args.ToList());
            }
            catch (ModException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is InvalidDataException)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        static string Opt(List<string> a, string name)
        {
            int i = a.IndexOf(name);
            if (i < 0) return null;
            if (i + 1 >= a.Count) throw new ModException($"{name} needs a value");
            var v = a[i + 1];
            a.RemoveRange(i, 2);
            return v;
        }

        static int Run(List<string> a)
        {
            var gameOpt = Opt(a, "--game");
            var dataOpt = Opt(a, "--data");
            var outOpt = Opt(a, "-o");
            var nameOpt = Opt(a, "--name");
            var versionOpt = Opt(a, "--version");
            var authorOpt = Opt(a, "--author");
            var idOpt = Opt(a, "--id");
            var timeoutOpt = Opt(a, "--timeout");
            var minutesOpt = Opt(a, "--minutes");
            var reportOpt = Opt(a, "--report");
            var compareOpt = Opt(a, "--compare");
            bool visible = a.Remove("--visible");
            if (a.Count == 0 || a[0] == "-h" || a[0] == "--help" || a[0] == "/?")
            {
                Console.WriteLine(Usage);
                return a.Count == 0 ? 2 : 0;
            }

            GameInstall Game()
            {
                var g = gameOpt ?? GameInstall.Find();
                if (!GameInstall.IsGameFolder(g)) throw new ModException("Kohan II was not found. Pass --game \"<folder with Data.rwd>\".");
                return new GameInstall(g);
            }
            GameData data = null;
            GameData Data() => data ?? (data = new GameData(dataOpt ?? Game().DataRwd));

            try
            {
                switch (a[0])
                {
                    case "pack":
                    {
                        Need(a, 2);
                        var folder = Path.GetFullPath(a[1]);
                        var mf = Path.Combine(folder, "mod.json");
                        var man = File.Exists(mf) ? ModManifest.Read(mf) : new ModManifest();
                        if (nameOpt != null) man.Name = nameOpt;
                        if (versionOpt != null) man.Version = versionOpt;
                        if (authorOpt != null) man.Author = authorOpt;
                        if (idOpt != null) man.Id = idOpt;
                        if (string.IsNullOrWhiteSpace(man.Id)) man.Id = ModManifest.MakeId(man.Name);
                        man.Validate();
                        ModPackage pkg;
                        if (Directory.Exists(Path.Combine(folder, "files")) || Directory.Exists(Path.Combine(folder, "patches")))
                        {
                            pkg = ModPackage.Load(folder);
                            pkg.Manifest = man;
                            Console.WriteLine($"package layout: {pkg.Files.Count} files, {pkg.Patches.Count} patches, {pkg.Deltas.Count} deltas");
                        }
                        else
                        {
                            var report = new ModPacker.Report();
                            var files = ModPacker.CollectLoose(ModPackage.FolderEntries(folder), Data(), strict: false, report);
                            pkg = ModPacker.FromFiles(man, files, Data(), report);
                            Console.WriteLine(report);
                            foreach (var n in report.Notes) Console.WriteLine("  note: " + n);
                            if (report.Skipped.Count > 0) Console.WriteLine($"  left out {report.Skipped.Count} work file(s) (GIMP files, backups, documents, mod.json)");
                        }
                        File.WriteAllText(mf, man.ToJsonText());
                        var outPath = outOpt ?? Path.Combine(Path.GetDirectoryName(folder.TrimEnd('\\')), $"{man.Id}-{(man.Version.Length > 0 ? man.Version : "dev")}.k2mod");
                        pkg.Save(outPath);
                        if (pkg.NeedsGameData) pkg.Build(Data());
                        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length:N0} bytes); it rebuilds cleanly against this game");
                        return 0;
                    }
                    case "rwd":
                    {
                        Need(a, 2);
                        if (outOpt == null) throw new ModException("rwd needs -o <file.rwd>");
                        var notes = new List<string>();
                        var pkg = a[1].EndsWith(".k2mod", StringComparison.OrdinalIgnoreCase) || ModLibrary.IsPackageSource(a[1])
                            ? ModPackage.Load(a[1])
                            : ModLibrary.LoosePackage(a[1], Data(), notes);   // a zip or folder of finished files
                        foreach (var n in notes) Console.WriteLine("  " + n);
                        var files = pkg.Build(pkg.NeedsGameData ? Data() : null);
                        uint stamp = RwdArchive.Write(outOpt, files);
                        Console.WriteLine($"wrote {outOpt}: {files.Count} files, stamp {stamp:x8}");
                        return 0;
                    }
                    case "check":
                    {
                        Need(a, 2);
                        var pkg = ModPackage.Load(a[1]);
                        var files = pkg.Build(pkg.NeedsGameData ? Data() : null);
                        Console.WriteLine($"{pkg.Manifest.Title} {pkg.Manifest.Version}: builds {files.Count} files " +
                                          $"({pkg.Files.Count} whole, {pkg.Patches.Count} patched, {pkg.Deltas.Count} deltas), stamp {RwdArchive.DefaultStamp(files):x8}");
                        return 0;
                    }
                    case "install":
                    {
                        Need(a, 2);
                        var g = Game();
                        Refuse(g);
                        var r = ModLibrary.Locked(() => ModLibrary.Load(g).Install(Path.GetFullPath(a[1]), Data, s => Console.WriteLine(s)));
                        Console.WriteLine($"{(r.Upgraded ? "updated" : "installed")} {r.Mod.Manifest.Title} {r.Mod.Manifest.Version} -> {r.Mod.RwdPath}");
                        foreach (var m in r.Messages) Console.WriteLine("  " + m);
                        return 0;
                    }
                    case "list":
                    {
                        var g = Game();
                        var lib = ModLibrary.Load(g);
                        Console.WriteLine($"game: {g.Folder}");
                        int i = 0;
                        foreach (var m in lib.Mods)
                            Console.WriteLine($"{++i,3}. [{(m.Enabled ? "x" : " ")}] {m.Id,-24} {m.Manifest.Version,-10} {m.Manifest.Title}{(m.Problem != null ? "  PROBLEM: " + m.Problem : "")}");
                        foreach (var c in lib.FileConflicts())
                            Console.WriteLine($"  {c.Files.Count} file(s) changed by both {c.Loser.Id} and {c.Winner.Id}; {c.Winner.Id} wins");
                        foreach (var p in lib.Problems()) Console.WriteLine("  problem: " + p);
                        foreach (var s in lib.ExternalScripts().Where(s => !s.Disabled))
                            Console.WriteLine($"  outside the manager: startup\\{s.Name} mounts {string.Join(", ", s.Mounts)}");
                        var loose = LooseMods.Find(g, Data(), lib.Mods);
                        if (loose.Count > 0)
                            Console.WriteLine($"  hand-installed mod: {loose.Count} loose game file(s) in {loose.Where}; see k2mod loose");
                        return 0;
                    }
                    case "loose":
                    {
                        var g = Game();
                        if (a.Count > 1 && a[1] != "pack") throw new ModException("loose takes no argument or pack");
                        var lib = ModLibrary.Load(g);
                        var loose = LooseMods.Find(g, Data(), lib.Mods);
                        if (loose.Count == 0)
                        {
                            Console.WriteLine("no hand-installed mod files found in " + string.Join(", ", LooseMods.Depots(g).Select(d => d.Folder)));
                            return 0;
                        }
                        if (a.Count == 1)
                        {
                            foreach (var d in loose.Depots)
                            {
                                Console.WriteLine($"{d.Folder}: {loose.Files.Count(f => f.Depot == d)} file(s)");
                                foreach (var f in loose.Files.Where(f => f.Depot == d))
                                    Console.WriteLine($"  {(f.Replaces ? "replaces" : "new     ")}  {f.Rel}");
                            }
                            Console.WriteLine($"{loose.Count} loose game file(s): {loose.Replacing} replace game files, {loose.Adding} new");
                            Console.WriteLine($"suggested: --name \"{loose.SuggestedName}\" --version \"{loose.SuggestedVersion}\"" +
                                              (loose.SuggestedFrom != null ? $" (from {loose.SuggestedFrom})" : ""));
                            Console.WriteLine("k2mod loose pack [--name N] [--version V] packs them into one mod in Mods\\ and moves them to a backup folder.");
                            Console.WriteLine(LooseMods.MultiplayerWarning);
                            return 0;
                        }
                        Refuse(g);
                        var man = LooseMods.ManifestFor(loose, nameOpt ?? loose.SuggestedName, versionOpt ?? loose.SuggestedVersion);
                        if (idOpt != null) man.Id = idOpt;
                        if (authorOpt != null) man.Author = authorOpt;
                        var r = ModLibrary.Locked(() =>
                        {
                            var l = ModLibrary.Load(g);
                            return LooseMods.Pack(l, LooseMods.Find(g, Data(), l.Mods), man, s => Console.WriteLine(s));
                        });
                        Console.WriteLine($"{(r.Replaced ? "replaced" : "installed")} {r.Mod.Manifest.Title} {r.Mod.Manifest.Version} -> {r.Mod.RwdPath}: {r.Files} files, stamp {r.Stamp:x8}, turned on");
                        foreach (var m in r.Messages) Console.WriteLine("  " + m);
                        foreach (var b in r.Backups) Console.WriteLine("  loose files moved to " + b);
                        foreach (var f in r.NotMoved) Console.WriteLine("  could not move (still loose, still overrides the mod): " + f);
                        Console.WriteLine("  " + LooseMods.MultiplayerWarning);
                        return r.NotMoved.Count == 0 ? 0 : 1;
                    }
                    case "refresh":
                    {
                        var g = Game();
                        Refuse(g);
                        var msgs = ModLibrary.Locked(() => ModLibrary.Load(g).RebuildStale(Data(), s => Console.WriteLine(s)));
                        foreach (var m in msgs) Console.WriteLine("  " + m);
                        Console.WriteLine(msgs.Count == 0 ? "every mod is up to date" : "ok");
                        return 0;
                    }
                    case "autosave":
                    {
                        var g = Game();
                        if (a.Count > 1 && a[1] == "on")
                            MpAutosave.Enable(g, minutesOpt != null ? int.Parse(minutesOpt) : MpAutosave.Minutes(g) ?? MpAutosave.DefaultMinutes);
                        else if (a.Count > 1 && a[1] == "off")
                            MpAutosave.Disable(g);
                        else if (a.Count > 1)
                            throw new ModException("autosave takes on or off");
                        var min = MpAutosave.Minutes(g);
                        Console.WriteLine(min == null ? "multiplayer autosave: off" : $"multiplayer autosave: on, every {min} min (from the next game start)");
                        var last = MpAutosave.Latest();
                        if (last != null) Console.WriteLine($"  newest: {Path.GetFileNameWithoutExtension(last.Name)}, {last.LastWriteTime:yyyy-MM-dd HH:mm}");
                        return 0;
                    }
                    case "plugin":
                    case "plugins":
                    {
                        var g = Game();
                        var verb = a.Count > 1 ? a[1] : "list";
                        if (verb == "on" || verb == "off")
                        {
                            Need(a, 3);
                            Refuse(g);
                            var msgs = ModLibrary.Locked(() =>
                            {
                                var p = Plugins.Find(g, a[2]) ?? throw new ModException($"no plugin is called \"{a[2]}\" (k2mod plugin list)");
                                return verb == "on" ? Plugins.Enable(g, p) : Plugins.Disable(g, p);
                            });
                            foreach (var m in msgs) Console.WriteLine("  " + m);
                        }
                        else if (verb != "list")
                            throw new ModException("plugin takes list, on <id> or off <id>");
                        var all = Plugins.List(g);
                        if (all.Count == 0) Console.WriteLine("no plugins (this build of k2mod does not include any)");
                        foreach (var p in all)
                            Console.WriteLine($"[{(p.Enabled ? "x" : " ")}] {p.Id,-12} {p.Version,-14} {p.Title}{(p.BuiltIn ? "" : "  (Mods\\" + p.FileName + ")")}{(p.Problem != null ? "  PROBLEM: " + p.Problem : "")}");
                        if (File.Exists(Plugins.LogPath(g)))
                            Console.WriteLine($"  last game start: Mods\\{Plugins.LogName}, {File.GetLastWriteTime(Plugins.LogPath(g)):yyyy-MM-dd HH:mm}");
                        return 0;
                    }
                    case "fingerprint":
                        if (a.Count > 1) throw new ModException("fingerprint takes no argument: use --report <file> or --compare <file>");
                        return Fingerprint(Game(), reportOpt, compareOpt);
                    case "test":
                    {
                        var g = Game();
                        int timeout = timeoutOpt != null ? int.Parse(timeoutOpt) : 300;
                        var r = LaunchTest.Run(g, timeout, visible, s => Console.WriteLine(s));
                        Console.WriteLine($"result: {r.Outcome} after {r.Seconds:0} s; log {r.LogPath ?? "(none)"}");
                        foreach (var e in r.Errors) Console.WriteLine("  ERROR: " + e);
                        foreach (var w in r.Warnings) Console.WriteLine("  warning: " + w);
                        return r.Ok ? 0 : 1;
                    }
                    case "enable":
                    case "disable":
                    case "remove":
                    case "up":
                    case "down":
                    {
                        Need(a, 2);
                        var g = Game();
                        Refuse(g);
                        var msgs = ModLibrary.Locked(() =>
                        {
                            var lib = ModLibrary.Load(g);
                            var m = lib.Find(a[1]) ?? throw new ModException($"no installed mod has the id \"{a[1]}\"");
                            var o = new List<string>();
                            if (a[0] == "enable") o = lib.Enable(m);
                            else if (a[0] == "disable") o = lib.Disable(m);
                            else if (a[0] == "up") lib.Move(m, -1);
                            else if (a[0] == "down") lib.Move(m, 1);
                            if (a[0] == "remove") lib.Remove(m);
                            else lib.Save();
                            return o;
                        });
                        foreach (var m in msgs) Console.WriteLine("  " + m);
                        Console.WriteLine("ok");
                        return 0;
                    }
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            finally
            {
                data?.Dispose();
            }
        }

        /// <summary>k2mod fingerprint: prints the game files code; writes a report; compares with another player's report.</summary>
        static int Fingerprint(GameInstall g, string reportPath, string comparePath)
        {
            GameFingerprint theirs = null;
            if (comparePath != null)
            {
                try
                {
                    if (new FileInfo(comparePath).Length > GameFingerprint.MaxReportBytes) throw new ModException($"{comparePath} is too big to be a game files report");
                    theirs = GameFingerprint.ParseReport(File.ReadAllText(comparePath, System.Text.Encoding.UTF8));
                }
                catch (Exception ex) when (ex is ModException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"cannot compare with {comparePath}: {ex.Message}");
                    return 2;
                }
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var fp = GameFingerprint.Compute(g);
            clock.Stop();
            Console.WriteLine($"game files: {fp.Code}");
            Console.WriteLine("  " + fp.Summary());
            Console.WriteLine($"  checked in {clock.Elapsed.TotalSeconds:0.00} s");
            if (reportPath != null)
            {
                File.WriteAllText(reportPath, fp.ToReport("k2mod " + GameFingerprint.ToolVersion()), new System.Text.UTF8Encoding(false));
                Console.WriteLine($"wrote {reportPath}");
            }
            if (theirs == null) return 0;
            var c = new FingerprintComparison(fp, theirs);
            Console.WriteLine(c.Headline);
            foreach (var line in c.Lines()) Console.WriteLine(line);
            return c.Same ? 0 : 1;
        }

        static void Need(List<string> a, int n)
        {
            if (a.Count < n) throw new ModException($"{a[0]}: missing argument. Run k2mod --help.");
        }

        static void Refuse(GameInstall g)
        {
            if (GameInstall.IsGameRunning()) throw new ModException("Kohan II is running. Close it before changing mods.");
        }
    }
}
