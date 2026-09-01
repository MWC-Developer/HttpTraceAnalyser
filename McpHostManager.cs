using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Starts/stops an in-process MCP server (HTTP transport, localhost-only) that exposes the
    /// currently running <see cref="MainWindow"/> for control by external MCP clients such
    /// as the GitHub Copilot CLI. Tools are discovered from <see cref="Mcp.TraceMcpTools"/>.
    /// Lifetime is controlled explicitly (e.g. via the "MCP Server" ribbon toggle) rather than
    /// starting automatically with the application.
    /// </summary>
    internal static class McpHostManager
    {
        /// <summary>Port the MCP HTTP endpoint listens on. Bound to loopback only.</summary>
        public const int Port = 5088;

        private static IHost? _host;

        /// <summary>Whether the MCP server is currently running.</summary>
        public static bool IsRunning => _host is not null;

        /// <summary>Starts the MCP server. No-op if already running.</summary>
        public static async Task StartAsync()
        {
            if (_host is not null)
                return;

            var builder = WebApplication.CreateBuilder();

            builder.WebHost.ConfigureKestrel(options =>
            {
                // Loopback-only: this app should not be reachable from other machines.
                options.Listen(IPAddress.Loopback, Port);
            });

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly(typeof(McpHostManager).Assembly);

            var app = builder.Build();
            app.MapMcp();

            await app.StartAsync().ConfigureAwait(false);
            _host = app;
        }

        /// <summary>Stops the MCP server, if running. No-op otherwise.</summary>
        public static async Task StopAsync()
        {
            var host = _host;
            if (host is null)
                return;

            _host = null;
            await host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            host.Dispose();
        }
    }
}

