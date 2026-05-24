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

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
    }

}
