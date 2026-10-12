using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using K2Mods;

namespace K2ModManager
{
    public partial class AuthorWindow : Window
    {
        readonly GameInstall game;
        readonly MainWindow main;
        GameData data;

        public AuthorWindow(GameInstall game, MainWindow main)
        {
            InitializeComponent();
            Theme.Track(this);
            this.game = game;
            this.main = main;
            if (main.IsVisible) Owner = main;
            Closed += (s, e) => data?.Dispose();
            var last = Settings.AuthorFolder;
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) LoadFolder(last);
            Write("Game: " + game.Folder);
        }

        void Write(string line)
        {
            Log.AppendText(line + "\n");
            Log.ScrollToEnd();
        }

        void OnBrowse(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose the folder with your finished mod files (laid out like the game's data)",
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(FolderBox.Text) ? FolderBox.Text : "",
            })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) LoadFolder(dlg.SelectedPath);
            }
        }

        void OnFolderTyped(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(FolderBox.Text) && File.Exists(Path.Combine(FolderBox.Text, "mod.json"))) LoadFolder(FolderBox.Text);
        }

        void LoadFolder(string folder)
        {
            FolderBox.Text = folder;
            var mf = Path.Combine(folder, "mod.json");
            ModManifest m;
            try { m = File.Exists(mf) ? ModManifest.Read(mf) : new ModManifest { Name = Path.GetFileName(folder.TrimEnd('\\')) }; }
            catch (Exception ex) when (ex is FormatException || ex is IOException)
            {
                Write("mod.json could not be read: " + ex.Message);
                m = new ModManifest { Name = Path.GetFileName(folder.TrimEnd('\\')) };
            }
            NameBox.Text = m.Name;
            VersionBox.Text = m.Version;
            AuthorBox.Text = m.Author;
            WebsiteBox.Text = m.Website;
            DescriptionBox.Text = m.Description;
            IdBox.Text = m.Id;
            IncompatibleBox.Text = string.Join(", ", m.Incompatible);
            RequiresBox.Text = string.Join(", ", m.Requires);
            LoadAfterBox.Text = string.Join(", ", m.LoadAfter);
            BasesBox.Text = string.Join("\r\n", m.Bases.Select(kv => kv.Key + " = " + kv.Value));
            Settings.AuthorFolder = folder;
        }

        static List<string> Ids(string text) =>
            text.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();

        ModManifest Manifest()
        {
            var m = new ModManifest
            {
                Name = NameBox.Text.Trim(),
                Version = VersionBox.Text.Trim(),
                Author = AuthorBox.Text.Trim(),
                Website = WebsiteBox.Text.Trim(),
                Description = DescriptionBox.Text.Trim(),
                Id = IdBox.Text.Trim().ToLowerInvariant(),
                Incompatible = Ids(IncompatibleBox.Text),
                Requires = Ids(RequiresBox.Text),
                LoadAfter = Ids(LoadAfterBox.Text),
            };
            if (m.Id.Length == 0) m.Id = ModManifest.MakeId(m.Name);
            foreach (var line in BasesBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) throw new ModException($"\"{line}\": write it as  your/file.dds = game/file.dds");
                m.Bases[line.Substring(0, eq).Trim().Replace('\\', '/')] = line.Substring(eq + 1).Trim().Replace('\\', '/');
            }
            m.Validate();
            return m;
        }

        void OnPack(object sender, RoutedEventArgs e) => _ = Make(Output.Package);

        void OnPackAndInstall(object sender, RoutedEventArgs e) => _ = Make(Output.Install);

        void OnRwd(object sender, RoutedEventArgs e) => _ = Make(Output.Rwd);

        enum Output { Package, Install, Rwd }

        async Task Make(Output what)
        {
            var folder = FolderBox.Text.Trim();
            if (!Directory.Exists(folder))
            {
                Write("Choose your mod folder first.");
                return;
            }
            ModManifest man;
            try { man = Manifest(); }
            catch (ModException ex)
            {
                Write(ex.Message);
                return;
            }
            var parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\')) ?? folder;
            var baseName = $"{man.Id}-{(man.Version.Length > 0 ? man.Version : "dev")}";
            string outPath;
            if (what == Output.Install)
                outPath = Path.Combine(Path.GetTempPath(), "K2ModManager", baseName + ".k2mod");
            else
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = what == Output.Rwd ? "Save the .rwd archive" : "Save the mod package",
                    InitialDirectory = parent,
                    FileName = baseName + (what == Output.Rwd ? ".rwd" : ".k2mod"),
                    Filter = what == Output.Rwd ? "Kohan II archive (*.rwd)|*.rwd" : "Kohan II mod package (*.k2mod)|*.k2mod",
                };
                if (dlg.ShowDialog(this) != true) return;
                outPath = dlg.FileName;
            }

            Busy.Visibility = Visibility.Visible;
            BusyText.Text = "Packaging " + man.Title + "...";
            void Progress(string s) => Dispatcher.BeginInvoke(new Action(() => BusyDetail.Text = s));
            var lines = new List<string>();
            bool ok = false;
            try
            {
                File.WriteAllText(Path.Combine(folder, "mod.json"), man.ToJsonText(), new UTF8Encoding(false));
                await Task.Run(() =>
                {
                    if (data == null)
                    {
                        Progress("Reading the game's files");
                        data = new GameData(game.DataRwd);
                    }
                    ModPackage pkg;
                    if (Directory.Exists(Path.Combine(folder, "files")) || Directory.Exists(Path.Combine(folder, "patches")))
                    {
                        pkg = ModPackage.Load(folder);
                        pkg.Manifest = man;
                        lines.Add($"Folder is already in package layout: {pkg.Files.Count} files, {pkg.Patches.Count} patches, {pkg.Deltas.Count} deltas.");
                    }
                    else
                    {
                        var report = new ModPacker.Report();
                        Progress("Finding your files");
                        var files = ModPacker.CollectLoose(ModPackage.FolderEntries(folder), data, strict: false, report);
                        Progress("Indexing the game's text files (first time only)");
                        _ = data.Index;
                        pkg = ModPacker.FromFiles(man, files, data, report, Progress);
                        lines.Add(report.ToString() + ".");
                        lines.AddRange(report.Notes.Select(n => "  note: " + n));
                        if (report.Skipped.Count > 0)
                            lines.Add($"  left out {report.Skipped.Count} work file(s): GIMP files, backups, documents and mod.json");
                        if (report.NewArt.Count > 0)
                        {
                            lines.Add("  These new art files are shipped whole. If one was painted over a game file, add it under");
                            lines.Add("  \"More settings > New art painted over a game file\" so only your changes are shipped:");
                            lines.AddRange(report.NewArt.Select(n => "    " + n));
                        }
                    }
                    Progress("Checking it rebuilds");
                    var built = pkg.Build(pkg.NeedsGameData ? data : null);
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                    if (what == Output.Rwd)
                    {
                        uint stamp = RwdArchive.Write(outPath, built);
                        lines.Add($"Wrote {outPath}: {built.Count} files, stamp {stamp:x8}.");
                        lines.Add("Note: an .rwd contains the finished files, including any game text you changed. Share the .k2mod instead.");
                    }
                    else
                    {
                        pkg.Save(outPath);
                        lines.Add($"Wrote {outPath} ({new FileInfo(outPath).Length:N0} bytes, {built.Count} files when installed).");
                    }
                });
                ok = true;
            }
            catch (Exception ex) when (ex is ModException || ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is InvalidDataException)
            {
                lines.Add("Could not make the package: " + ex.Message);
            }
            finally
            {
                Busy.Visibility = Visibility.Collapsed;
            }
            foreach (var l in lines) Write(l);
            if (ok && what == Output.Install)
            {
                Write("Installing it in the mod list...");
                await main.InstallForTesting(outPath);
                Write("Done: see the main window.");
            }
        }
    }
}
