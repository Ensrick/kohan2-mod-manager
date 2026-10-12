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
        Theme.Apply(dark);
        Save((FrameworkElement)Detach(w), 960, 640, Path.Combine(outDir, "main.png"));
        var a = new AuthorWindow(new GameInstall(game), w);
        if (args.Length > 2) typeof(AuthorWindow).GetMethod("LoadFolder", flags).Invoke(a, new object[] { args[2] });
        Save((FrameworkElement)Detach(a), 860, 760, Path.Combine(outDir, "author.png"));
        return 0;
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
