using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K2ModManager;
using K2Mods;

// Renders the manager's windows to PNG files without showing any window (layout check).
// UiPreview <game folder> <out dir> [author mod folder] [--dark] [--notices]
// With K2MODS_USERDATA set to a test folder holding loose mod files, also renders the pack dialog (loose.png).
// Also renders the game files window (fingerprint.png) and a comparison with a made-up other player (fingerprint-compare.png).
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // --dark renders the dark theme (Windows dark app mode), whatever this PC uses
        // --notices adds one notice of each kind to the main window
        bool dark = Array.IndexOf(args, "--dark") >= 0, notices = Array.IndexOf(args, "--notices") >= 0;
        args = Array.FindAll(args, x => !x.StartsWith("--"));
        var game = args[0];
        var outDir = args[1];
        var app = new App();
        app.InitializeComponent();
        Theme.Apply(!dark);     // build the windows in the other theme, then switch: checks the live switch reaches everything
        var w = new MainWindow(new string[0]);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(MainWindow).GetField("game", flags).SetValue(w, new GameInstall(game));
        typeof(MainWindow).GetMethod("Refresh", flags).Invoke(w, new object[] { true });
        if (notices)
        {
            var kind = typeof(MainWindow).GetNestedType("Kind", BindingFlags.NonPublic);
            var add = typeof(MainWindow).GetMethod("AddNotice", flags);
            foreach (var k in new[] { "Info", "Warn", "Error" })
                add.Invoke(w, new object[] { $"Sample {k.ToLower()} notice.", Enum.Parse(kind, k), k == "Warn" ? "Open that folder" : null, null });
        }
        // the game files code: worked out here, not in the background, so the render shows it
        var fp = GameFingerprint.Compute(new GameInstall(game));
        ((FingerprintBar)typeof(MainWindow).GetField("FilesBar", flags).GetValue(w)).Display(fp);
        var fw = new FingerprintWindow(new GameInstall(game), fp);
        var fc = new FingerprintWindow(new GameInstall(game), fp);
        fc.ShowComparison(new FingerprintComparison(fp, SampleOtherPlayer(fp)));
        Theme.Apply(dark);
        Save((FrameworkElement)Detach(w), 960, 640, Path.Combine(outDir, "main.png"));
        Save((FrameworkElement)Detach(fw), 720, 600, Path.Combine(outDir, "fingerprint.png"));
        Save((FrameworkElement)Detach(fc), 720, 600, Path.Combine(outDir, "fingerprint-compare.png"));
        var a = new AuthorWindow(new GameInstall(game), w);
        if (args.Length > 2) typeof(AuthorWindow).GetMethod("LoadFolder", flags).Invoke(a, new object[] { args[2] });
        Save((FrameworkElement)Detach(a), 860, 760, Path.Combine(outDir, "author.png"));
        // the pack dialog, when the main window found a hand-installed mod (K2MODS_USERDATA points at a test folder)
        if (typeof(MainWindow).GetField("loose", flags).GetValue(w) is LooseInstall loose && loose.Count > 0)
        {
            var lw = new LooseWindow(loose, (ModLibrary)typeof(MainWindow).GetField("lib", flags).GetValue(w));
            Save((FrameworkElement)Detach(lw), 720, 640, Path.Combine(outDir, "loose.png"));
        }
        return 0;
    }

    /// <summary>Another player's report for the comparison render: one file changed, one missing, one extra.</summary>
    static GameFingerprint SampleOtherPlayer(GameFingerprint mine)
    {
        var theirs = GameFingerprint.ParseReport(mine.ToReport("UiPreview"));
        theirs.Code = "1B2C-3D4E";
        if (theirs.Files.Count > 0) theirs.Files[0].ShortHash = "0123456789ab";
        if (theirs.Files.Count > 1) theirs.Files.RemoveAt(1);
        theirs.Files.Add(new FingerprintFile { Path = "Units/Example/example_unit.tgi", ShortHash = "abcdef012345", Source = @"loose: Documents\Kohan2\data" });
        return theirs;
    }

    static object Detach(Window w)
    {
        var root = (Panel)w.Content;
        w.Content = null;
        root.Background = w.Background;
        TextElement.SetFontSize(root, w.FontSize);
        TextElement.SetForeground(root, w.Foreground);
        return root;
    }

    static void Save(FrameworkElement root, int width, int height, string path)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(path)) enc.Save(f);
        Console.WriteLine("wrote " + path);
    }
}
