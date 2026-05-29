using System.Configuration;
using System.Data;
using System.Windows;
using blink_o_mat.Models;
using blink_o_mat.Services;

namespace blink_o_mat
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            var options = CommandLineOptions.Parse(e.Args);

            if (options.IsHeadless)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var code = await new HeadlessRunner().RunAsync(options, CancellationToken.None);
                Shutdown(code);
                return;
            }

            try
            {
                var window = new MainWindow();
                MainWindow = window;
                window.Show();
            }
            catch
            {
                var backedUpSettings = AppSettingsService.TryBackupPersistedSettings();
                if (backedUpSettings)
                {
                    try
                    {
                        var window = new MainWindow();
                        MainWindow = window;
                        window.Show();

                        System.Windows.MessageBox.Show(
                            window,
                            "Your saved settings were incompatible with this version and were reset. A backup of the previous settings was kept in LocalAppData\\Rejector.",
                            "Rejector",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                    catch
                    {
                    }
                }

                Shutdown(-1);
            }
        }
    }

}
