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
    /// Starts an in-process MCP server (HTTP transport, localhost-only) that exposes the
    /// currently running <see cref="MainWindow"/> for control by external MCP clients such
    /// as the GitHub Copilot CLI. Tools are discovered from <see cref="Mcp.TraceMcpTools"/>.
    /// </summary>
    internal static class McpHostManager
    {
        /// <summary>Port the MCP HTTP endpoint listens on. Bound to loopback only.</summary>
        public const int Port = 5088;

        public static async Task<IHost> StartAsync()
        {
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
            return app;
        }
    }
}
