using System;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HttpTraceAnalyser.Mcp
{
    /// <summary>
    /// Runs inside the WPF GUI process. Listens on a local, current-user-only named pipe and
    /// dispatches incoming <see cref="TraceBridgeRequest"/>s to the matching public static method
    /// on <see cref="TraceMcpTools"/> (the same tool implementations exposed in-process). This is
    /// what lets a separate "HttpTraceAnalyser.exe --mcp-stdio" process act as the MCP stdio
    /// transport while still operating on the already-loaded, in-memory trace owned by the GUI.
    ///
    /// Least-privilege notes: the pipe only accepts connections from the same local user (no
    /// network exposure, no elevation required to host or connect), every request is validated by
    /// resolving it to one of a fixed, known set of methods via reflection (no arbitrary code
    /// execution), and argument values are deserialized to the target parameter's declared type
    /// (enum/int/bool/string) rather than interpreted as code or shell input.
    /// </summary>
    public sealed class TraceBridgeServer : IDisposable
    {
        /// <summary>Base name for the named pipe; suffixed per-instance so multiple GUI copies don't collide.</summary>
        public const string DefaultPipeName = "HttpTraceAnalyser.McpBridge";

        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;

        public TraceBridgeServer(string? pipeNameSuffix = null)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeNameSuffix)
                ? DefaultPipeName
                : $"{DefaultPipeName}.{pipeNameSuffix}";
        }

        /// <summary>The full pipe name external "--mcp-stdio" processes should connect to.</summary>
        public string PipeName => _pipeName;

        public void Start()
        {
            if (_acceptLoop is not null)
                return;

            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort shutdown; the pipe server task observes cancellation internally.
            }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = CreatePipeServer();
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    _ = HandleConnectionAsync(server, token);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    // Diagnostics only - must never be written to stdout (this process is the GUI,
                    // not the MCP stdio transport, but keep the discipline consistent).
                    System.Diagnostics.Debug.WriteLine($"[TraceBridgeServer] Accept failed: {ex}");
                    server?.Dispose();
                }
            }
        }

        private NamedPipeServerStream CreatePipeServer()
        {
            // A named pipe created without an explicit, broadened security descriptor defaults to
            // allowing only the creating user (and admins) to connect - sufficient for this
            // local, current-user-only bridge. No network exposure is possible for named pipes.
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        /// <summary>
        /// Upper bound on how long a single request is allowed to take to dispatch (including the
        /// dispatcher round-trip into the WPF UI thread). Guards against a stuck/blocked UI thread
        /// (e.g. a modal dialog left open) hanging a bridge connection - and therefore the calling
        /// "--mcp-stdio" process - indefinitely.
        /// </summary>
        private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(25);

        private static async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            try
            {
                using (pipe)
                {
                    while (pipe.IsConnected && !token.IsCancellationRequested)
                    {
                        var requestBytes = await BridgeFraming.ReadFrameAsync(pipe, token).ConfigureAwait(false);
                        if (requestBytes is null)
                            break;

                        var requestJson = Encoding.UTF8.GetString(requestBytes);
                        var response = await DispatchWithTimeoutAsync(requestJson).ConfigureAwait(false);
                        var responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, JsonOptions));
                        await BridgeFraming.WriteFrameAsync(pipe, responseBytes, token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TraceBridgeServer] Connection failed: {ex}");
            }
        }

        private static async Task<TraceBridgeResponse> DispatchWithTimeoutAsync(string requestJson)
        {
            // Dispatch() calls into TraceMcpTools, which synchronously blocks on
            // Dispatcher.Invoke(...). Run it on a background thread and race it against a timeout
            // so a stuck UI thread degrades to a clear error instead of hanging the pipe forever.
            var dispatchTask = Task.Run(() => Dispatch(requestJson));
            var completed = await Task.WhenAny(dispatchTask, Task.Delay(DispatchTimeout)).ConfigureAwait(false);

            if (completed != dispatchTask)
            {
                return new TraceBridgeResponse
                {
                    Error = $"Timed out after {DispatchTimeout.TotalSeconds:0}s waiting for the HttpTraceAnalyser UI thread to respond. " +
                            "It may be busy (e.g. a modal dialog is open) or unresponsive.",
                };
            }

            return await dispatchTask.ConfigureAwait(false);
        }

        private static TraceBridgeResponse Dispatch(string requestJson)
        {
            TraceBridgeRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<TraceBridgeRequest>(requestJson, JsonOptions);
            }
            catch (JsonException ex)
            {
                return new TraceBridgeResponse { Error = $"Malformed request: {ex.Message}" };
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Tool))
                return new TraceBridgeResponse { Error = "Request must specify a tool name." };

            // Only ever resolve against the fixed, known set of [McpServerTool] methods - never
            // arbitrary type/member names supplied by the caller.
            var method = typeof(TraceMcpTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, request.Tool, StringComparison.Ordinal) &&
                    m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null);

            if (method is null)
                return new TraceBridgeResponse { Error = $"Unknown tool '{request.Tool}'." };

            try
            {
                var parameters = method.GetParameters();
                var callArgs = new object?[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (request.Args.TryGetValue(p.Name!, out var element))
                    {
                        callArgs[i] = DeserializeArg(element, p.ParameterType);
                    }
                    else if (p.HasDefaultValue)
                    {
                        callArgs[i] = p.DefaultValue;
                    }
                    else
                    {
                        return new TraceBridgeResponse { Error = $"Missing required argument '{p.Name}' for tool '{request.Tool}'." };
                    }
                }

                var invokeResult = method.Invoke(null, callArgs);
                var resultString = invokeResult switch
                {
                    Task<string> task => task.GetAwaiter().GetResult(),
                    string s => s,
                    null => null,
                    _ => invokeResult.ToString(),
                };

                return new TraceBridgeResponse { Result = resultString };
            }
            catch (TargetInvocationException ex)
            {
                return new TraceBridgeResponse { Error = ex.InnerException?.Message ?? ex.Message };
            }
            catch (Exception ex)
            {
                return new TraceBridgeResponse { Error = ex.Message };
            }
        }

        private static object? DeserializeArg(JsonElement element, Type targetType)
        {
            if (element.ValueKind == JsonValueKind.Null)
                return null;

            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            return JsonSerializer.Deserialize(element.GetRawText(), underlying, JsonOptions);
        }
    }
}
