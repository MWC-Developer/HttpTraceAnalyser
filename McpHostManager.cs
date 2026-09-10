using System;
using HttpTraceAnalyser.Mcp;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Starts/stops the in-process named-pipe bridge (see <see cref="Mcp.TraceBridgeServer"/>)
    /// that lets a separate "HttpTraceAnalyser.exe --mcp-stdio" process (the actual MCP stdio
    /// server, spawned by an MCP client such as GitHub Copilot CLI) reach the currently running
    /// <see cref="MainWindow"/> and operate on its live, in-memory trace data. Lifetime is
    /// controlled explicitly (e.g. via the "MCP Server" ribbon toggle) rather than starting
    /// automatically with the application. The bridge is local-only and restricted to the current
    /// Windows user; no network exposure and no elevation is required.
    /// </summary>
    internal static class McpHostManager
    {
        private static TraceBridgeServer? _server;

        /// <summary>
        /// Optional suffix distinguishing this instance's pipe from other running copies of the
        /// app. Configurable via the Settings dialog before the server is started.
        /// </summary>
        public static string? PipeNameSuffix { get; set; }

        /// <summary>Whether the MCP bridge is currently running.</summary>
        public static bool IsRunning => _server is not null;

        /// <summary>Starts the MCP bridge. No-op if already running.</summary>
        public static System.Threading.Tasks.Task StartAsync()
        {
            if (_server is not null)
                return System.Threading.Tasks.Task.CompletedTask;

            var server = new TraceBridgeServer(PipeNameSuffix);
            server.Start();
            _server = server;
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>Stops the MCP bridge, if running. No-op otherwise.</summary>
        public static System.Threading.Tasks.Task StopAsync()
        {
            var server = _server;
            if (server is null)
                return System.Threading.Tasks.Task.CompletedTask;

            _server = null;
            server.Dispose();
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
