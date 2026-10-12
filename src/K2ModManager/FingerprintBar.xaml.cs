using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using K2Mods;

namespace K2ModManager
{
    /// <summary>
    /// "Game files: 7F3A-91C2  Copy  Details..." under the game folder line. Works the code out in the background after every
    /// change the launcher makes (MainWindow.Refresh calls Recompute) and whenever the window comes back to the front (the
    /// player may have copied files by hand meanwhile). The code is for information only: a failure shows as text, it never
    /// stops the launcher.
    /// </summary>
    public partial class FingerprintBar : UserControl
    {
        /// <summary>File hashes for the whole session, shared with the details window: a recheck reads only changed files.</summary>
        internal static readonly FingerprintCache Cache = new FingerprintCache();

        GameInstall game;
        GameFingerprint current;
        Task<GameFingerprint> pending;
        CancellationTokenSource cancel;
        int round;
        bool hooked;

        public FingerprintBar()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            Loaded += (s, e) => FollowWindow();
        }

        void FollowWindow()
        {
            var w = Window.GetWindow(this);
            if (hooked || w == null) return;
            hooked = true;
            w.Activated += (s, e) =>
            {
                if (game != null) Recompute(game);
            };
        }

        /// <summary>Works the code out again in the background; null hides the line (no game folder).</summary>
        public void Recompute(GameInstall install)
        {
            cancel?.Cancel();
            bool sameGame = game != null && install != null && string.Equals(game.Folder, install.Folder, StringComparison.OrdinalIgnoreCase);
            game = install;
            int mine = ++round;
            if (install == null)
            {
                current = null;
                pending = null;
                Visibility = Visibility.Collapsed;
                return;
            }
            Visibility = Visibility.Visible;
            if (!sameGame)
            {
                current = null;
                ShowChecking();
            }
            var stop = cancel = new CancellationTokenSource();
            var task = pending = Task.Run(() => GameFingerprint.Compute(install, Cache, stop.Token), stop.Token);
            // results come back through the dispatcher, never through an awaited context, so nothing touches the window off its thread
            task.ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => Finished(mine, t))), TaskScheduler.Default);
            // a slow check (first run, big mods) says so instead of showing the old code for long
            Task.Delay(400).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (mine == round && !task.IsCompleted) ShowChecking();
            })), TaskScheduler.Default);
        }

        void Finished(int mine, Task<GameFingerprint> t)
        {
            var failure = t.Exception;   // read even when superseded, so no failure goes unobserved
            if (mine != round || t.IsCanceled) return;
            if (failure != null)
            {
                var ex = failure.GetBaseException();
                if (ex is OperationCanceledException) return;
                current = null;
                ShowProblem(ex.Message);
                return;
            }
            Display(t.Result);
        }

        /// <summary>Shows a worked-out code (also used by tests/UiPreview).</summary>
        public void Display(GameFingerprint fp)
        {
            current = fp;
            Visibility = Visibility.Visible;
            CodeText.Text = fp.Code;
            CodeLine.ToolTip = "Players whose codes match load exactly the same game data. If a multiplayer match goes out of sync right away, compare codes first.\n\n" + fp.Summary();
            CopyButton.Visibility = Visibility.Visible;
            DetailsButton.Visibility = Visibility.Visible;
        }

        void ShowChecking()
        {
            CodeText.Text = "checking...";
            CopyButton.Visibility = Visibility.Collapsed;
        }

        void ShowProblem(string why)
        {
            CodeText.Text = "could not be checked";
            CodeLine.ToolTip = why;
            CopyButton.Visibility = Visibility.Collapsed;
            DetailsButton.Visibility = Visibility.Visible;
        }

        /// <summary>The newest code: waits for a check still running, so Copy never copies a code that is about to change.</summary>
        async Task<GameFingerprint> Latest()
        {
            var p = pending;
            if (p != null && !p.IsCompleted)
            {
                try { return await p; }
                catch (Exception) { return null; }   // shown by Finished
            }
            return current;
        }

        async void OnCopy(object sender, RoutedEventArgs e)
        {
            var fp = await Latest();
            if (fp == null || !CopyText(fp.Code)) return;
            CopyButton.Content = "Copied";
            await Task.Delay(1500);
            CopyButton.Content = "Copy";
        }

        void OnDetails(object sender, RoutedEventArgs e)
        {
            if (game == null) return;
            var w = new FingerprintWindow(game, current) { Owner = Window.GetWindow(this) };
            w.ShowDialog();
        }

        /// <summary>Puts text on the clipboard; another program holding the clipboard is retried briefly.</summary>
        internal static bool CopyText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(50);
                }
            }
            return false;
        }
    }
}
