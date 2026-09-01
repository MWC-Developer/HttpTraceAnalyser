using System;
using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Hosting;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private Task<IHost>? _mcpHostTask;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Globally disable link detection in all AvalonEdit controls to prevent
            // regex catastrophic backtracking that causes UI hangs on large files
            AvalonEditHelper.DisableLinkDetectionGlobally();

            ThemeManager.Apply(ThemeManager.GetSystemTheme());

            _mcpHostTask = McpHostManager.StartAsync();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Run the shutdown off the UI thread and with ConfigureAwait(false): StopAsync's
            // internal continuations must not need to resume on the WPF dispatcher, which is
            // already tearing down at this point and would otherwise deadlock the call below.
            if (_mcpHostTask is not null)
            {
                try
                {
                    Task.Run(async () =>
                    {
                        var host = await _mcpHostTask.ConfigureAwait(false);
                        await host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        host.Dispose();
                    }).Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Startup may have failed, already been torn down, or timed out;
                    // fall through and force-exit the process regardless.
                }
            }

            base.OnExit(e);

            // Kestrel can leave background threads (e.g. from the HTTP listener) alive even
            // after the host reports stopped, which would otherwise keep the process running
            // after the last window closes. Force the process to end now that cleanup is done.
            Environment.Exit(0);
        }
    }

}
