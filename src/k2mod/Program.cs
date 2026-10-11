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
  k2mod rwd <package|folder> -o <file.rwd>      Build the .rwd archive the game loads.
  k2mod check <package>                         Build it in memory against this game; report.
  k2mod install <package|.rwd|.zip|folder>      Install and turn on.
  k2mod list | enable <id> | disable <id> | remove <id> | up <id> | down <id>
  k2mod refresh                                 Rebuild mods after a game update changed Data.rwd.

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
                        var pkg = ModPackage.Load(a[1]);
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
                        var loose = ModLibrary.LooseOverrides(Data());
                        if (loose.Count > 0) Console.WriteLine($"  {loose.Count} loose game file(s) in {GameInstall.UserDataDir} override every mod");
                        return 0;
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
