using System.IO;
using System.Windows;
using AcePlayer.Engine;

namespace AcePlayer
{
    public partial class App : Application
    {
        private void OnAppStartup(object sender, StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "aceplayer_error.log"),
                        args.Exception.ToString());
                }
                catch { }
                MessageBox.Show(args.Exception.Message, "AcePlayer — error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            FFmpegLoader.Initialize();
            new MainWindow().Show();
        }
    }
}
