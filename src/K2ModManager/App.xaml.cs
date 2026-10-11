using System;
using System.Windows;
using System.Windows.Threading;

namespace K2ModManager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandled;
            new MainWindow(e.Args).Show();
        }

        static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message +
                            "\n\nNothing was changed by the step that failed. If this keeps happening, please report it with this message.",
                            "Kohan II Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
