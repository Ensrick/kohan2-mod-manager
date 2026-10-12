using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace K2Mods
{
    /// <summary>A game plugin: a DLL the plugin loader starts inside k2.exe.</summary>
    public sealed class PluginInfo
    {
        public string Id;
        public string Title;
        public string Version;
        public string Description;
        /// <summary>File name inside Mods\ (the name plugins.txt lists).</summary>
        public string FileName;
        public bool Enabled;
        /// <summary>Shipped inside the manager: written to Mods\ when turned on, deleted when turned off.</summary>
        public bool BuiltIn;
        public string Problem;
    }

    /// <summary>
    /// Game plugins (native/ in the repository). Turning one on:
    ///   &lt;game&gt;\winmm.dll          the plugin loader; k2.exe imports winmm.dll and Windows takes this copy from the game
    ///                             folder first. It passes everything on to the real winmm.dll and starts the plugins.
    ///   &lt;game&gt;\Mods\&lt;id&gt;.dll       the plugin
    ///   &lt;game&gt;\Mods\plugins.txt     the plugins the loader starts, one file name per line
    /// The loader is removed again when no plugin is on. None of these files is part of the multiplayer version check
    /// (it compares data archives), so a plugin never stops anyone from joining. The loader writes what it did to
    /// &lt;game&gt;\Mods\plugins.log.
    /// </summary>
    public static class Plugins
    {
        public const string ListName = "plugins.txt";
        public const string LogName = "plugins.log";
        public const string LoaderName = "winmm.dll";
        public const string LoaderProduct = "Kohan II Plugin Loader";
        /// <summary>The version resource's ProductName that marks a DLL in Mods\ as a plugin.</summary>
        public const string PluginProduct = "Kohan II plugin";
        static readonly string[] BuiltInFiles = { "k2mp.dll" };

        public static string ListPath(GameInstall g) => Path.Combine(g.ModsDir, ListName);
        public static string LogPath(GameInstall g) => Path.Combine(g.ModsDir, LogName);
        public static string LoaderPath(GameInstall g) => Path.Combine(g.Folder, LoaderName);

        /// <summary>Built-in plugins first, then plugins found in Mods\.</summary>
        public static List<PluginInfo> List(GameInstall g)
        {
            var on = new HashSet<string>(ReadList(g), StringComparer.OrdinalIgnoreCase);
            var o = new List<PluginInfo>();
            foreach (var file in BuiltInFiles)
            {
                var bytes = Embedded(file);
                if (bytes == null) continue;   // a build without the native part
                var p = Describe(Cached(file, bytes), file);
                p.BuiltIn = true;
                p.Enabled = on.Contains(file);
                o.Add(p);
            }
            if (Directory.Exists(g.ModsDir))
                foreach (var path in Directory.GetFiles(g.ModsDir, "*.dll").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var file = Path.GetFileName(path);
                    if (o.Any(p => string.Equals(p.FileName, file, StringComparison.OrdinalIgnoreCase))) continue;
                    FileVersionInfo v;
                    try { v = FileVersionInfo.GetVersionInfo(path); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                    if (v.ProductName != PluginProduct) continue;
                    var p = Describe(path, file);
                    p.Enabled = on.Contains(file);
                    o.Add(p);
                }
            var loader = LoaderProblem(g);
            if (loader != null)
                foreach (var p in o) p.Problem = loader;
            return o;
        }

        public static PluginInfo Find(GameInstall g, string idOrFile) =>
            List(g).FirstOrDefault(p => string.Equals(p.Id, idOrFile, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(p.FileName, idOrFile, StringComparison.OrdinalIgnoreCase));

        /// <summary>Turns a plugin on: writes it (built-in) and the loader, and lists it in plugins.txt.</summary>
        public static List<string> Enable(GameInstall g, PluginInfo p)
        {
            if (p.Problem != null) throw new ModException(p.Problem);
            Directory.CreateDirectory(g.ModsDir);
            if (p.BuiltIn) WriteIfDifferent(Path.Combine(g.ModsDir, p.FileName), Embedded(p.FileName));
            InstallLoader(g);
            var list = ReadList(g);
            if (!list.Contains(p.FileName, StringComparer.OrdinalIgnoreCase)) list.Add(p.FileName);
            WriteList(g, list);
            return new List<string>
            {
                $"Turned on {p.Title}. It starts with Kohan II; what it does is written to Mods\\{LogName} in the game folder.",
            };
        }

        /// <summary>Turns a plugin off; removes a built-in plugin's file, and the loader once no plugin is on.</summary>
        public static List<string> Disable(GameInstall g, PluginInfo p)
        {
            var list = ReadList(g).Where(f => !string.Equals(f, p.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
            WriteList(g, list);
            var path = Path.Combine(g.ModsDir, p.FileName);
            if (p.BuiltIn && File.Exists(path)) File.Delete(path);
            var o = new List<string> { $"Turned off {p.Title}." };
            if (list.Count == 0 && RemoveLoader(g)) o.Add($"No plugin is on, so the plugin loader ({LoaderName}) was taken out of the game folder.");
            return o;
        }

        /// <summary>
        /// Brings the files of plugins that are on up to this manager's build (built-in plugins and the loader), and
        /// takes the loader out if nothing is on. Run while the game is closed.
        /// </summary>
        public static List<string> Sync(GameInstall g)
        {
            var o = new List<string>();
            var list = ReadList(g);
            if (list.Count == 0)
            {
                RemoveLoader(g);
                return o;
            }
            if (LoaderProblem(g) != null) return o;
            foreach (var p in List(g).Where(p => p.BuiltIn && p.Enabled))
                if (WriteIfDifferent(Path.Combine(g.ModsDir, p.FileName), Embedded(p.FileName)))
                    o.Add($"Updated {p.Title} to {p.Version}.");
            InstallLoader(g);
            return o;
        }

        // ------------------------------------------------------------------ the loader

        /// <summary>Why plugins cannot be used in this game folder, or null.</summary>
        static string LoaderProblem(GameInstall g)
        {
            if (Embedded(LoaderName) == null) return "This build of the manager does not include the plugin loader.";
            var path = LoaderPath(g);
            if (!File.Exists(path) || IsOurLoader(path)) return null;
            return $"The game folder already has a {LoaderName} from another program, so plugins cannot be turned on. " +
                   "Remove or rename that file first if you no longer use the program it came with.";
        }

        static bool IsOurLoader(string path)
        {
            try { return FileVersionInfo.GetVersionInfo(path).ProductName == LoaderProduct; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        static void InstallLoader(GameInstall g)
        {
            var problem = LoaderProblem(g);
            if (problem != null) throw new ModException(problem);
            WriteIfDifferent(LoaderPath(g), Embedded(LoaderName));
        }

        /// <summary>Deletes the loader when it is ours. True when it was there.</summary>
        static bool RemoveLoader(GameInstall g)
        {
            var path = LoaderPath(g);
            if (!File.Exists(path) || !IsOurLoader(path)) return false;
            File.Delete(path);
            return true;
        }

        // ------------------------------------------------------------------ plugins.txt

        static List<string> ReadList(GameInstall g)
        {
            var path = ListPath(g);
            if (!File.Exists(path)) return new List<string>();
            return File.ReadAllLines(path)
                       .Select(l => l.Trim())
                       .Where(l => l.Length > 0 && !l.StartsWith("#"))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        static void WriteList(GameInstall g, List<string> files)
        {
            var path = ListPath(g);
            if (files.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var lines = new List<string>
            {
                ModLibrary.ScriptMarker + ": the plugins the plugin loader (winmm.dll) starts, one per line.",
                "# Turn plugins on and off in K2ModManager.exe instead of editing this file.",
            };
            lines.AddRange(files);
            Directory.CreateDirectory(g.ModsDir);
            ModLibrary.WriteText(path, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
        }

        // ------------------------------------------------------------------ files

        static PluginInfo Describe(string path, string file)
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var id = string.IsNullOrWhiteSpace(v.InternalName) ? Path.GetFileNameWithoutExtension(file) : v.InternalName.Trim();
            return new PluginInfo
            {
                Id = id,
                Title = string.IsNullOrWhiteSpace(v.FileDescription) ? id : v.FileDescription.Trim(),
                Version = (v.ProductVersion ?? "").Trim(),
                Description = (v.Comments ?? "").Trim(),
                FileName = file,
            };
        }

        static readonly Dictionary<string, byte[]> embedded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A DLL built into this exe (native/build, embedded by the .csproj as "plugins.&lt;file&gt;"), or null.</summary>
        static byte[] Embedded(string file)
        {
            lock (embedded)
            {
                if (embedded.TryGetValue(file, out var b)) return b;
                using (var s = typeof(Plugins).Assembly.GetManifestResourceStream("plugins." + file))
                {
                    if (s != null)
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        b = ms.ToArray();
                    }
                }
                return embedded[file] = b;
            }
        }

        /// <summary>A copy of an embedded DLL in %TEMP%, to read its version resource (FileVersionInfo needs a file).</summary>
        static string Cached(string file, byte[] bytes)
        {
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes), 0, 6).Replace("-", "").ToLowerInvariant();
            var dir = Path.Combine(Path.GetTempPath(), "K2ModManager");
            var path = Path.Combine(dir, hash + "_" + file);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                var tmp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                try { File.Move(tmp, path); }
                catch (IOException) { File.Delete(tmp); }   // another manager wrote it first
            }
            return path;
        }

        /// <summary>Writes the file unless it already holds exactly these bytes. True when it wrote.</summary>
        static bool WriteIfDifferent(string path, byte[] bytes)
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpanEquals(bytes)) return false;
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
    }
}
