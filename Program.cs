using System;
using System.Threading.Tasks;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Custom entry point (replaces the WPF-generated Main, disabled via
    /// &lt;StartupObject&gt; in the csproj) so the process can branch between two mutually
    /// exclusive modes before any WPF infrastructure is touched:
    /// - Normal launch: starts the WPF GUI as before.
    /// - "--mcp-stdio" launch: runs only the MCP stdio server (see
    ///   <see cref="Mcp.McpStdioHost"/>), with no window, for use by MCP clients that spawn this
    ///   process directly and communicate over inherited stdin/stdout.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (Array.Exists(args, a => string.Equals(a, "--mcp-stdio", StringComparison.OrdinalIgnoreCase)))
            {
                return Mcp.McpStdioHost.RunAsync(args).GetAwaiter().GetResult();
            }

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
