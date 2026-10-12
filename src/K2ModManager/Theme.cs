using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace K2ModManager
{
    /// <summary>
    /// Follows Windows' light/dark app mode (Settings, Personalization, Colors) for the neutral colours,
    /// and switches live when the user changes it. Blue and gold stay the same in both.
    /// </summary>
    public static class Theme
    {
        public static bool IsDark { get; private set; }

        /// <summary>Windows' "Choose your app mode" setting; light when it cannot be read (Windows 7/8, high contrast).</summary>
        public static bool WindowsUsesDark()
        {
            if (SystemParameters.HighContrast) return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { return false; }
        }

        /// <summary>Applies the Windows setting now and whenever it changes.</summary>
        public static void FollowWindows()
        {
            Apply(WindowsUsesDark());
            SystemEvents.UserPreferenceChanged += (s, e) =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dark = WindowsUsesDark();
                    if (dark != IsDark) Apply(dark);
                }));
        }

        public static void Apply(bool dark)
        {
            IsDark = dark;
            var merged = Application.Current.Resources.MergedDictionaries;
            var palette = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/K2ModManager;component/Themes/{(dark ? "Dark" : "Light")}.xaml")
            };
            if (merged.Count > 0) merged[0] = palette;
            else merged.Add(palette);
            foreach (Window w in Application.Current.Windows) SetTitleBar(w);
        }

        /// <summary>Call from a window's constructor so its title bar matches from the first frame.</summary>
        public static void Track(Window w) => w.SourceInitialized += (s, e) => SetTitleBar(w);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        static void SetTitleBar(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = IsDark ? 1 : 0;
            try
            {
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 2004 and later); 19 on Windows 10 1809-1909
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
}
