using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace HttpTraceAnalyser.Mcp
{
    /// <summary>
    /// Runs the MCP server over stdio (the official transport that MCP clients such as GitHub
    /// Copilot CLI spawn and inherit stdin/stdout from) when the app is launched with
    /// "--mcp-stdio". This process never shows a WPF window and, per MCP/security requirements:
    /// - Writes ONLY valid MCP protocol frames to stdout (via the SDK's stdio transport).
    /// - Sends all diagnostics/logs to stderr, never stdout.
    /// - Runs as a normal, unprivileged user process (no elevation, no admin requirement).
    /// Tool calls are served by <see cref="BridgedTraceMcpTools"/>, which forwards them to the
    /// already-running GUI process's live trace state over a local named pipe.
    /// </summary>
    public static class McpStdioHost
    {
        /// <summary>
        /// Optional pipe-name suffix, allowing this stdio process to target a specific GUI
        /// instance's bridge when more than one is running. Set from the "--mcp-pipe=&lt;name&gt;"
        /// command-line argument; null selects the default/well-known pipe name.
        /// </summary>
        public static string? PipeNameSuffix { get; private set; }

        /// <summary>
        /// Parses "--mcp-stdio"-mode arguments and runs the stdio MCP server until stdin closes.
        /// Returns the process exit code.
        /// </summary>
        public static async Task<int> RunAsync(string[] args)
        {
            PipeNameSuffix = ParsePipeNameSuffix(args);

            var builder = Host.CreateApplicationBuilder();

            // Diagnostics/logs must never be written to stdout - only stderr - since stdout is
            // reserved exclusively for MCP protocol frames written by the stdio transport.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

            // Register only the tool methods declared on BridgedTraceMcpTools, built explicitly
            // via McpServerTool.Create(MethodInfo, ...). The Type-based WithTools(IEnumerable<Type>)
            // and WithToolsFromAssembly overloads were found (by manual JSON-RPC testing) to either
            // silently fail to register any tools, or to pull in TraceMcpTools' in-process-only
            // tool methods from the same assembly (which duplicate every tool name and cause
            // clients to reject/drop the whole tools/list response) - building the list explicitly
            // avoids both failure modes.
            var tools = typeof(BridgedTraceMcpTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(m => McpServerTool.Create(m, target: null, options: null))
                .ToList();

            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools(tools);

            using var host = builder.Build();
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }

        private static string? ParsePipeNameSuffix(string[] args)
        {
            const string prefix = "--mcp-pipe=";
            foreach (var arg in args)
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = arg[prefix.Length..].Trim();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }

            return null;
        }
    }
}
