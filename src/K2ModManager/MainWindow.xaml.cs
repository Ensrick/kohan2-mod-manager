using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using K2Mods;

namespace K2ModManager
{
    public sealed class ModRow
    {
        public ModRow(InstalledMod mod, string status, Brush brush)
        {
            Mod = mod;
            Status = status;
            StatusBrush = brush;
        }

        public InstalledMod Mod { get; }
        public bool Enabled => Mod.Enabled;
        public string Title => Mod.Manifest.Title;
        public string Version => Mod.Manifest.Version;
        public string Status { get; }
        public Brush StatusBrush { get; }
    }

    public partial class MainWindow : Window
    {
        enum Kind { Info, Warn, Error }

        static readonly Brush OkBrush = Brushes(0x3C7A3A), OffBrush = Brushes(0x8A7E6A), WarnBrush = Brushes(0x9A6510), BadBrush = Brushes(0xA8322A);

        readonly string[] args;
        GameInstall game;
        ModLibrary lib;
        List<FileConflict> conflicts = new List<FileConflict>();
        readonly List<string> messages = new List<string>();
        Kind messageKind = Kind.Info;
        List<string> looseOverrides = new List<string>();

        public MainWindow(string[] args)
        {
            InitializeComponent();
            this.args = args;
            Loaded += OnLoaded;
        }

        static Brush Brushes(int rgb)
        {
            var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            b.Freeze();
            return b;
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.RegisterFileType();
            var folder = Settings.GameFolder;
            if (!GameInstall.IsGameFolder(folder)) folder = GameInstall.Find();
            if (GameInstall.IsGameFolder(folder)) game = new GameInstall(folder);
            Refresh(scanLoose: true);
            if (game == null) return;
            if (!GameInstall.IsGameRunning())
                await Run("Checking your mods...", p => WithData(d => ModLibrary.Locked(() => ModLibrary.Load(game).RebuildStale(d, p))));
            foreach (var a in args.Where(a => File.Exists(a) || Directory.Exists(a)))
                await InstallFrom(a);
            await OfferPackagesNextToExe();
        }

        // ------------------------------------------------------------------ helpers

        T WithData<T>(Func<GameData, T> f)
        {
            using (var d = new GameData(game.DataRwd)) return f(d);
        }

        void Say(IEnumerable<string> lines, Kind kind = Kind.Info)
        {
            messages.Clear();
            messages.AddRange(lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            messageKind = kind;
        }

        bool CanChange()
        {
            if (game == null)
            {
                Say(new[] { "Choose your Kohan II folder first." }, Kind.Error);
                Refresh();
                return false;
            }
            if (GameInstall.IsGameRunning())
            {
                Say(new[] { "Kohan II is running. Close the game first, then try again." }, Kind.Error);
                Refresh();
                return false;
            }
            return true;
        }

        static string Explain(Exception ex)
        {
            switch (ex)
            {
                case ModException m: return m.Message;
                case UnauthorizedAccessException _:
                    return "Windows did not allow the manager to change files in the game folder.\n" + ex.Message +
                           "\nClose Kohan II if it is open. If this keeps happening, right-click K2ModManager.exe and choose \"Run as administrator\".";
                case IOException _: return "A file could not be read or written:\n" + ex.Message;
                case FormatException _: return "A mod file is damaged:\n" + ex.Message;
                case InvalidDataException _: return "A mod file is damaged:\n" + ex.Message;
                default: return null;
            }
        }

        /// <summary>Runs work off the window thread behind a "please wait" panel; shows its messages.</summary>
        async Task<bool> Run(string title, Func<Action<string>, List<string>> work)
        {
            Busy.Visibility = Visibility.Visible;
            BusyText.Text = title;
            BusyDetail.Text = "";
            void Progress(string s) => Dispatcher.BeginInvoke(new Action(() => BusyDetail.Text = s));
            try
            {
                var msgs = await Task.Run(() => work(Progress));
                if (msgs != null && msgs.Count > 0) Say(msgs);
                return true;
            }
            catch (Exception ex) when (Explain(ex) != null)
            {
                Say(new[] { Explain(ex) }, Kind.Error);
                return false;
            }
            finally
            {
                Busy.Visibility = Visibility.Collapsed;
                Refresh(scanLoose: true);
            }
        }

        /// <summary>Quick changes (on/off, order) run directly.</summary>
        void Do(Func<ModLibrary, List<string>> change)
        {
            if (!CanChange()) return;
            try
            {
                var msgs = ModLibrary.Locked(() =>
                {
                    var l = ModLibrary.Load(game);
                    var o = change(l) ?? new List<string>();
                    l.Save();
                    return o;
                });
                if (msgs.Count > 0) Say(msgs);
            }
            catch (Exception ex) when (Explain(ex) != null)
            {
                Say(new[] { Explain(ex) }, Kind.Error);
            }
            Refresh();
        }

        // ------------------------------------------------------------------ display

        void Refresh(bool scanLoose = false)
        {
            var selectedId = (ModList.SelectedItem as ModRow)?.Mod.Id;
            NoticePanel.Children.Clear();
            if (messages.Count > 0)
                AddNotice(string.Join("\n", messages), messageKind, "OK", () =>
                {
                    messages.Clear();
                    Refresh();
                });
            if (Settings.RunningFromTemp)
                AddNotice("You opened the manager straight from the downloaded zip. Extract the zip first (right-click it, Extract All), then run K2ModManager.exe from the extracted folder.", Kind.Warn);
            if (game == null)
            {
                GameText.Text = "Kohan II was not found";
                AddNotice("Kohan II: Kings of War was not found on this computer. Click \"Find the game\" and pick k2.exe in your Kohan II folder.",
                          Kind.Warn, "Find the game...", ChooseGame);
                ModList.ItemsSource = null;
                EmptyText.Visibility = Visibility.Visible;
                UpdateButtons();
                ShowDetails();
                return;
            }
            GameText.Text = "Game folder: " + game.Folder;
            try
            {
                lib = ModLibrary.Load(game);
                conflicts = lib.FileConflicts();
                if (scanLoose) looseOverrides = WithData(ModLibrary.LooseOverrides);
            }
            catch (Exception ex) when (Explain(ex) != null)
            {
                AddNotice(Explain(ex), Kind.Error);
                return;
            }

            var rows = lib.Mods.Select(m => new ModRow(m, StatusOf(m, out var brush), brush)).ToList();
            ModList.ItemsSource = rows;
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ModList.SelectedItem = rows.FirstOrDefault(r => r.Mod.Id == selectedId) ?? rows.FirstOrDefault();

            foreach (var p in lib.Problems().Where(p => lib.Mods.All(m => m.Problem == null || !p.Contains(m.Problem))))
                AddNotice(p, Kind.Warn);
            foreach (var s in lib.ExternalScripts().Where(x => !x.Disabled))
            {
                var script = s;
                AddNotice($"The startup file \"{s.Name}\" also loads {string.Join(", ", s.Mounts)} outside this manager. " +
                          "If that is an older install of a mod in your list, turn it off so the mod is not loaded twice.",
                          Kind.Warn, "Turn it off", () => Do(l =>
                          {
                              l.SetExternal(script, false);
                              return new List<string> { $"Turned off {script.Name} (moved to startup\\disabled, so it can be put back)." };
                          }));
            }
            if (looseOverrides.Count > 0)
                AddNotice($"{looseOverrides.Count} loose game file(s) in Documents\\Kohan2\\data replace the game's own files and override every mod in this list. " +
                          "They are usually left over from installing a mod by hand.",
                          Kind.Warn, "Open that folder", () => Process.Start("explorer.exe", "\"" + GameInstall.UserDataDir + "\""));
            UpdateButtons();
            ShowDetails();
        }

        string StatusOf(InstalledMod m, out Brush brush)
        {
            if (m.Problem != null)
            {
                brush = BadBrush;
                return "Cannot be used: " + m.Problem.Split('\n')[0];
            }
            if (!m.Enabled)
            {
                brush = OffBrush;
                return "Off";
            }
            var missing = m.Manifest.Requires.Where(r => lib.Find(r)?.Enabled != true).ToList();
            if (missing.Count > 0)
            {
                brush = WarnBrush;
                return "On, but it needs: " + string.Join(", ", missing.Select(r => lib.Find(r)?.Manifest.Title ?? r));
            }
            var lost = conflicts.Where(c => c.Loser == m).ToList();
            if (lost.Count > 0)
            {
                brush = WarnBrush;
                return "On; " + string.Join("; ", lost.Select(c => $"{c.Files.Count} of its files are replaced by {c.Winner.Manifest.Title}"));
            }
            brush = OkBrush;
            return "On";
        }

        void AddNotice(string text, Kind kind, string button = null, Action action = null)
        {
            var colors = kind == Kind.Error ? (0xF8DDD6, 0xD9A196) : kind == Kind.Warn ? (0xFBF0CC, 0xE2C67A) : (0xDDEBD7, 0xA9C79E);
            var border = new Border
            {
                Background = Brushes(colors.Item1),
                BorderBrush = Brushes(colors.Item2),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var dock = new DockPanel();
            if (button != null)
            {
                var b = new Button { Content = button, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                b.Click += (s, e) => action();
                DockPanel.SetDock(b, Dock.Right);
                dock.Children.Add(b);
            }
            dock.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            border.Child = dock;
            NoticePanel.Children.Add(border);
        }

        void UpdateButtons()
        {
            var row = ModList.SelectedItem as ModRow;
            int i = row == null || lib == null ? -1 : lib.Mods.FindIndex(m => m.Id == row.Mod.Id);
            UpButton.IsEnabled = i > 0;
            DownButton.IsEnabled = i >= 0 && i < lib.Mods.Count - 1;
            RemoveButton.IsEnabled = row != null;
            AddButton.IsEnabled = game != null;
            PlayButton.IsEnabled = game != null;
        }

        void ShowDetails()
        {
            var m = (ModList.SelectedItem as ModRow)?.Mod;
            Details.Visibility = m == null ? Visibility.Hidden : Visibility.Visible;
            if (m == null) return;
            var man = m.Manifest;
            DTitle.Text = man.Title;
            var by = new List<string>();
            if (man.Version.Length > 0) by.Add("Version " + man.Version);
            if (man.Author.Length > 0) by.Add("by " + man.Author);
            DByline.Text = string.Join(", ", by);
            Uri uri = null;
            if (man.Website.Length > 0 && Uri.TryCreate(man.Website, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) uri = u;
            DWebsite.Visibility = uri == null ? Visibility.Collapsed : Visibility.Visible;
            DLink.NavigateUri = uri;
            DLinkText.Text = uri?.ToString() ?? "";
            DDescription.Text = man.Description.Length > 0 ? man.Description : "(No description.)";
            var facts = new List<string> { $"Changes or adds {m.FileNames.Count} game file(s)." };
            if (m.HasPackage && m.BuiltAgainst != null) facts.Add("Built from your own copy of the game; rebuilt automatically if a game update changes it.");
            if (man.Requires.Count > 0) facts.Add("Needs: " + string.Join(", ", man.Requires.Select(r => lib.Find(r)?.Manifest.Title ?? r)));
            if (man.Incompatible.Count > 0) facts.Add("Cannot be used with: " + string.Join(", ", man.Incompatible.Select(r => lib.Find(r)?.Manifest.Title ?? r)));
            DFacts.Text = string.Join("\n", facts);
            var lines = new List<string>();
            if (m.Problem != null) lines.Add(m.Problem);
            foreach (var c in conflicts.Where(c => c.Loser == m))
                lines.Add($"{c.Files.Count} file(s) are also changed by {c.Winner.Manifest.Title}, which is lower in the list, so its version is used.");
            foreach (var c in conflicts.Where(c => c.Winner == m))
                lines.Add($"{c.Files.Count} file(s) are also changed by {c.Loser.Manifest.Title}; this mod's version is used.");
            DConflicts.Text = string.Join("\n", lines);
        }

        // ------------------------------------------------------------------ actions

        void OnSelect(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
            ShowDetails();
        }

        void OnToggle(object sender, RoutedEventArgs e)
        {
            var row = (ModRow)((CheckBox)sender).DataContext;
            bool on = ((CheckBox)sender).IsChecked == true;
            ModList.SelectedItem = row;
            if (on)
            {
                var clash = lib.Mods.Where(m => m != row.Mod && m.Enabled && ModLibrary.Incompatible(m, row.Mod)).ToList();
                if (clash.Count > 0)
                {
                    var names = string.Join(", ", clash.Select(c => c.Manifest.Title));
                    if (MessageBox.Show(this, $"{row.Title} cannot be used together with {names}.\n\nTurn off {names} and use {row.Title}?",
                            "Kohan II Mod Manager", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        Refresh();
                        return;
                    }
                }
            }
            Do(l =>
            {
                var m = l.Find(row.Mod.Id) ?? throw new ModException($"{row.Title} is no longer installed");
                return on ? l.Enable(m) : l.Disable(m);
            });
        }

        void OnUp(object sender, RoutedEventArgs e) => MoveSelected(-1);

        void OnDown(object sender, RoutedEventArgs e) => MoveSelected(1);

        void MoveSelected(int delta)
        {
            if (!(ModList.SelectedItem is ModRow row)) return;
            Do(l =>
            {
                var m = l.Find(row.Mod.Id);
                if (m != null) l.Move(m, delta);
                return null;
            });
        }

        void OnRemove(object sender, RoutedEventArgs e)
        {
            if (!(ModList.SelectedItem is ModRow row)) return;
            if (MessageBox.Show(this, $"Remove {row.Title}?\n\nIts files are deleted from the game folder. You can install it again later from the mod file you downloaded.",
                    "Kohan II Mod Manager", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Do(l =>
            {
                var m = l.Find(row.Mod.Id);
                if (m != null) l.Remove(m);
                return new List<string> { $"Removed {row.Title}." };
            });
        }

        async void OnAdd(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the mod you downloaded",
                Filter = "Kohan II mods (*.k2mod, *.zip, *.rwd)|*.k2mod;*.zip;*.rwd|All files (*.*)|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FileNames) await InstallFrom(f);
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        async void OnDrop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] paths)) return;
            foreach (var p in paths) await InstallFrom(p);
        }

        async Task InstallFrom(string path)
        {
            if (!CanChange()) return;
            string id = null;
            await Run("Installing " + Path.GetFileName(path.TrimEnd('\\', '/')) + "...", p =>
            {
                GameData data = null;
                try
                {
                    var r = ModLibrary.Locked(() => ModLibrary.Load(game).Install(path, () => data ?? (data = new GameData(game.DataRwd)), p));
                    id = r.Mod.Id;
                    var o = new List<string>
                    {
                        $"{(r.Upgraded ? "Updated" : "Installed")} {r.Mod.Manifest.Title}" +
                        $"{(r.Mod.Manifest.Version.Length > 0 ? " " + r.Mod.Manifest.Version : "")}. It is ticked, so it will be used the next time you play.",
                    };
                    o.AddRange(r.Messages);
                    return o;
                }
                finally
                {
                    data?.Dispose();
                }
            });
            if (id != null) ModList.SelectedItem = (ModList.ItemsSource as IEnumerable<ModRow>)?.FirstOrDefault(r => r.Mod.Id == id);
        }

        /// <summary>Mods shipped next to the manager (a download containing both) are offered for install.</summary>
        async Task OfferPackagesNextToExe()
        {
            string dir;
            try { dir = Path.GetDirectoryName(Settings.ExePath); }
            catch (Exception) { return; }
            foreach (var f in Directory.GetFiles(dir, "*.k2mod").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                ModManifest man;
                try { man = ModPackage.ReadManifest(f); }
                catch (Exception ex) when (Explain(ex) != null) { continue; }
                var have = lib?.Find(man.Id);
                if (have != null && have.Manifest.Version == man.Version) continue;
                var q = have == null
                    ? $"{man.Title} {man.Version} is in the same folder as the manager.\n\nInstall it now?"
                    : $"{man.Title} {man.Version} is in the same folder as the manager (you have version {have.Manifest.Version}).\n\nUpdate it now?";
                if (MessageBox.Show(this, q, "Kohan II Mod Manager", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await InstallFrom(f);
            }
        }

        void OnPlay(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/" + GameInstall.SteamAppId) { UseShellExecute = true });
            }
            catch (Exception)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(game.Folder, "k2.exe")) { WorkingDirectory = game.Folder, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Say(new[] { "Could not start Kohan II: " + ex.Message }, Kind.Error);
                    Refresh();
                }
            }
        }

        void OnChangeGame(object sender, RoutedEventArgs e) => ChooseGame();

        void ChooseGame()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Find k2.exe in your Kohan II folder",
                Filter = "Kohan II (k2.exe)|k2.exe|All files (*.*)|*.*",
                InitialDirectory = game?.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            if (dlg.ShowDialog(this) != true) return;
            var folder = Path.GetDirectoryName(dlg.FileName);
            if (!GameInstall.IsGameFolder(folder))
            {
                Say(new[] { $"{folder} does not look like a Kohan II folder (it needs k2.exe, Data.rwd and a startup folder)." }, Kind.Error);
                Refresh();
                return;
            }
            game = new GameInstall(folder);
            Settings.GameFolder = game.Folder;
            messages.Clear();
            Refresh(scanLoose: true);
        }

        void OnAuthor(object sender, RoutedEventArgs e)
        {
            if (game == null)
            {
                Say(new[] { "Choose your Kohan II folder first: packaging reads the game's own files to leave them out of your package." }, Kind.Error);
                Refresh();
                return;
            }
            new AuthorWindow(game, this).Show();
        }

        /// <summary>Called by the author window after "Make and install for testing".</summary>
        public Task InstallForTesting(string package) => InstallFrom(package);

        void OnLink(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception) { }
            e.Handled = true;
        }
    }
}
