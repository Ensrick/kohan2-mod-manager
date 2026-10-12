using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using K2Mods;

namespace K2ModManager
{
    /// <summary>Asks for a name and version before packing a hand-installed mod, and says what packing changes for online play.</summary>
    public partial class LooseWindow : Window
    {
        readonly LooseInstall loose;
        readonly ModLibrary lib;

        /// <summary>The mod.json for the pack, set when the player clicks "Pack and turn on".</summary>
        public ModManifest Manifest { get; private set; }

        public LooseWindow(LooseInstall loose, ModLibrary lib)
        {
            InitializeComponent();
            Theme.Track(this);
            this.loose = loose;
            this.lib = lib;
            Intro.Text =
                $"{loose.Count} game file(s) were copied by hand into {loose.Where}: {loose.Replacing} replace the game's own files, {loose.Adding} are new. " +
                "Loose files like these override every mod in your list. Packing puts them into one mod file (.rwd) that you turn on and off here like any other mod.";
            var warning = LooseMods.MultiplayerWarning;
            int colon = warning.IndexOf(": ", StringComparison.Ordinal);
            MpWarning.Inlines.Add(new Bold(new Run(warning.Substring(0, colon + 1))));
            MpWarning.Inlines.Add(new Run(warning.Substring(colon + 1)));
            var list = new StringBuilder();
            foreach (var d in loose.Depots)
            {
                if (loose.Depots.Count > 1) list.AppendLine("In " + d.Label + ":");
                foreach (var f in loose.Files.Where(f => f.Depot == d))
                    list.AppendLine((f.Replaces ? "replaces  " : "new       ") + f.Rel);
            }
            FileList.Text = list.ToString().TrimEnd();
            NameBox.Text = loose.SuggestedName;
            VersionBox.Text = loose.SuggestedVersion;
        }

        ModManifest Read() => LooseMods.ManifestFor(loose, NameBox.Text, VersionBox.Text);

        void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var man = Read();
            ErrorText.Visibility = Visibility.Collapsed;
            if (man.Name.Length == 0)
            {
                IdNote.Text = "Give the mod a name.";
                PackButton.IsEnabled = false;
                return;
            }
            try
            {
                var existing = LooseMods.ExistingFor(lib, man);
                IdNote.Text = $"Saved as Mods\\{man.Id}.rwd." +
                              (existing != null ? $" It replaces {existing.Manifest.Title}{(existing.Manifest.Version.Length > 0 ? " " + existing.Manifest.Version : "")} in your list." : "");
                PackButton.IsEnabled = true;
            }
            catch (ModException ex)
            {
                IdNote.Text = ex.Message;
                PackButton.IsEnabled = false;
            }
        }

        void OnPack(object sender, RoutedEventArgs e)
        {
            var man = Read();
            try
            {
                man.Validate();
                LooseMods.ExistingFor(lib, man);
            }
            catch (ModException ex)
            {
                ErrorText.Text = ex.Message;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            Manifest = man;
            DialogResult = true;
        }

        void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try { Process.Start("explorer.exe", "\"" + loose.Depots[0].Folder + "\""); }
            catch (Exception) { }
        }
    }
}
