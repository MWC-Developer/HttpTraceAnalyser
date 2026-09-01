using System;
using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Globally disable link detection in all AvalonEdit controls to prevent
            // regex catastrophic backtracking that causes UI hangs on large files
            AvalonEditHelper.DisableLinkDetectionGlobally();

            ThemeManager.Apply(ThemeManager.GetSystemTheme());
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Run the shutdown off the UI thread and with ConfigureAwait(false): StopAsync's
            // internal continuations must not need to resume on the WPF dispatcher, which is
            // already tearing down at this point and would otherwise deadlock the call below.
            try
            {
                Task.Run(() => McpHostManager.StopAsync()).Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Server may not have been running, or already been torn down/timed out;
                // fall through and force-exit the process regardless.
            }

            base.OnExit(e);

            // Kestrel can leave background threads (e.g. from the HTTP listener) alive even
            // after the host reports stopped, which would otherwise keep the process running
            // after the last window closes. Force the process to end now that cleanup is done.
            Environment.Exit(0);
        }
    }

}
