using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using K2Mods;

namespace K2ModManager
{
    /// <summary>
    /// The game files code with its report: save mine to send to the other players, or compare another player's report with
    /// my game and list what differs in plain words. Works the code out again before saving or comparing, so both use the
    /// files as they are now.
    /// </summary>
    public partial class FingerprintWindow : Window
    {
        readonly GameInstall game;
        GameFingerprint mine;

        public FingerprintWindow(GameInstall game, GameFingerprint known)
        {
            InitializeComponent();
            Theme.Track(this);
            this.game = game;
            if (known != null) Display(known);
            else ShowChecking();
            Loaded += async (s, e) => await Check();
        }

        /// <summary>Works the code out again (quick: the session cache rereads only changed files). Null when it failed (shown).</summary>
        async Task<GameFingerprint> Check()
        {
            SetBusy(true);
            try
            {
                var fp = await Task.Run(() => GameFingerprint.Compute(game, FingerprintBar.Cache));
                Display(fp);
                return fp;
            }
            catch (Exception ex)   // the code is information only: any failure is shown, never fatal
            {
                ShowProblem(ex.GetBaseException().Message);
                return null;
            }
            finally
            {
                SetBusy(false);
            }
        }

        void SetBusy(bool busy)
        {
            SaveButton.IsEnabled = CompareButton.IsEnabled = !busy;
            CopyButton.IsEnabled = !busy && mine != null;
        }

        void ShowChecking()
        {
            CodeText.Text = "checking...";
            Summary.Text = "";
        }

        /// <summary>Shows a worked-out code (also used by tests/UiPreview).</summary>
        public void Display(GameFingerprint fp)
        {
            mine = fp;
            CodeText.Text = fp.Code;
            Summary.Text = fp.Summary();
            CopyButton.IsEnabled = true;
        }

        void ShowProblem(string why)
        {
            mine = null;
            CodeText.Text = "could not be checked";
            Summary.Text = why;
            CopyButton.IsEnabled = false;
        }

        /// <summary>Shows the result of comparing with another player's report (also used by tests/UiPreview).</summary>
        public void ShowComparison(FingerprintComparison c)
        {
            ShowResult(c.Same, c.Headline, string.Join(Environment.NewLine, c.Lines()));
        }

        void ShowResult(bool good, string title, string text)
        {
            ResultTitle.Text = title;
            ResultTitle.SetResourceReference(ForegroundProperty, good ? "StatusOk" : "StatusBad");
            ResultTitle.Visibility = Visibility.Visible;
            Result.Text = text;
            Result.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void OnCopy(object sender, RoutedEventArgs e)
        {
            if (mine != null && FingerprintBar.CopyText(mine.Code)) Note.Text = "Copied " + mine.Code + ".";
        }

        async void OnSave(object sender, RoutedEventArgs e)
        {
            var fp = await Check();
            if (fp == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save your game files report",
                FileName = fp.ReportFileName,
                DefaultExt = ".txt",
                AddExtension = true,
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, fp.ToReport("Kohan II Mod Manager " + GameFingerprint.ToolVersion()), new UTF8Encoding(false));
                Note.Text = $"Saved {Path.GetFileName(dlg.FileName)}. Send it to the other players: they open it here with \"Compare with another player's report...\".";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Note.Text = "The report could not be saved: " + ex.Message;
            }
        }

        async void OnCompare(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open another player's game files report",
                Filter = "Game files reports (*.txt)|*.txt|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            GameFingerprint theirs;
            try
            {
                if (new FileInfo(dlg.FileName).Length > GameFingerprint.MaxReportBytes)
                    throw new ModException("That file is too big to be a game files report.");
                theirs = GameFingerprint.ParseReport(File.ReadAllText(dlg.FileName, Encoding.UTF8));
            }
            catch (Exception ex) when (ex is ModException || ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowResult(false, "That file could not be compared", ex.Message);
                return;
            }
            var fp = await Check();
            if (fp == null) return;
            ShowComparison(new FingerprintComparison(fp, theirs));
            Note.Text = "Compared with " + Path.GetFileName(dlg.FileName) + ".";
        }
    }
}
