using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using K2Mods;
using Microsoft.Win32;

namespace K2ModManager
{
    /// <summary>Per-user settings in %APPDATA%\K2ModManager\settings.json.</summary>
    static class Settings
    {
        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "K2ModManager", "settings.json");

        static Dictionary<string, object> Read()
        {
            try { return File.Exists(FilePath) ? Json.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8)) : new Dictionary<string, object>(); }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException) { return new Dictionary<string, object>(); }
        }

        static void Write(string key, string value)
        {
            try
            {
                var d = Read();
                d[key] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, Json.Write(d) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        public static string GameFolder
        {
            get => Json.Str(Read(), "gameFolder");
            set => Write("gameFolder", value);
        }

        public static string AuthorFolder
        {
            get => Json.Str(Read(), "authorFolder");
            set => Write("authorFolder", value);
        }

        public static string ExePath => Process.GetCurrentProcess().MainModule.FileName;

        public static bool RunningFromTemp =>
            ExePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        const string IconVersion = "2";

        /// <summary>Double-clicking a .k2mod file opens it in this program (current user only, no admin needed).</summary>
        public static void RegisterFileType()
        {
            if (RunningFromTemp) return;
            try
            {
                var command = $"\"{ExePath}\" \"%1\"";
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\K2ModManager.Package\shell\open\command"))
                using (var cls = Registry.CurrentUser.CreateSubKey(@"Software\Classes\K2ModManager.Package"))
                {
                    // IconVersion: bumped when the icon changes, so Explorer is told to redraw .k2mod files once
                    if (k.GetValue("") as string == command && cls.GetValue("IconVersion") as string == IconVersion) return;
                    k.SetValue("", command);
                    cls.SetValue("", "Kohan II mod");
                    cls.SetValue("IconVersion", IconVersion);
                }
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\K2ModManager.Package\DefaultIcon"))
                    k.SetValue("", $"\"{ExePath}\",0");
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.k2mod"))
                    k.SetValue("", "K2ModManager.Package");
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException) { }
        }
    }
}
