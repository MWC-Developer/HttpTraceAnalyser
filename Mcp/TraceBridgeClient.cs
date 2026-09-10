using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HttpTraceAnalyser.Mcp
{
    /// <summary>
    /// Used by the "--mcp-stdio" console-mode process to reach the live trace state owned by an
    /// already-running HttpTraceAnalyser GUI instance, over the named pipe hosted by
    /// <see cref="TraceBridgeServer"/>. A new connection is made per call (simple, avoids shared
    /// mutable connection state); the pipe only accepts local, current-user connections.
    ///
    /// Messages are framed as a 4-byte little-endian length prefix followed by UTF-8 JSON, written
    /// and read directly via the pipe's async Stream APIs (see <see cref="BridgeFraming"/>).
    /// <see cref="System.IO.StreamWriter"/>/<see cref="System.IO.StreamReader"/> are deliberately
    /// NOT used here: wrapping a <see cref="NamedPipeClientStream"/> opened with
    /// <see cref="PipeOptions.Asynchronous"/> in those types was found (by direct testing) to
    /// cause an indefinite hang - their internal buffering does not correctly interoperate with
    /// overlapped (async) pipe handles.
    /// </summary>
    public sealed class TraceBridgeClient
    {
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private readonly string _pipeName;
        private readonly TimeSpan _connectTimeout;
        private readonly TimeSpan _callTimeout;

        public TraceBridgeClient(string? pipeNameSuffix = null, TimeSpan? connectTimeout = null, TimeSpan? callTimeout = null)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeNameSuffix)
                ? TraceBridgeServer.DefaultPipeName
                : $"{TraceBridgeServer.DefaultPipeName}.{pipeNameSuffix}";
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
            // Bounds the full request/response round-trip (not just the initial connect), so a
            // GUI process whose UI thread is busy/blocked (e.g. a modal dialog, or a long-running
            // synchronous operation on the dispatcher) can never hang this call indefinitely, even
            // when the caller passes CancellationToken.None. Most tool calls complete in
            // milliseconds; this generously covers slower ones (e.g. loading a large trace file).
            _callTimeout = callTimeout ?? TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Invokes <paramref name="tool"/> on the GUI process's live trace state, returning its
        /// string result. Throws <see cref="InvalidOperationException"/> if the GUI's bridge isn't
        /// running/reachable, if the call doesn't complete within the call timeout, or if the tool
        /// call itself reports a bridge-level error.
        /// </summary>
        public async Task<string?> InvokeAsync(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken cancellationToken)
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    "Could not reach the HttpTraceAnalyser GUI. Ensure the app is running and the MCP bridge toggle is enabled.");
            }

            var request = new TraceBridgeRequest { Tool = tool };
            foreach (var (key, value) in args)
                request.Args[key] = JsonSerializer.SerializeToElement(value, JsonOptions);

            using var timeoutCts = new CancellationTokenSource(_callTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            byte[]? responseBytes;
            try
            {
                var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions));
                await BridgeFraming.WriteFrameAsync(pipe, requestBytes, linkedCts.Token).ConfigureAwait(false);
                responseBytes = await BridgeFraming.ReadFrameAsync(pipe, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Timed out after {_callTimeout.TotalSeconds:0}s waiting for the HttpTraceAnalyser GUI to respond to '{tool}'. " +
                    "The GUI's UI thread may be busy (e.g. a modal dialog is open) or unresponsive.");
            }

            if (responseBytes is null)
                throw new InvalidOperationException("The HttpTraceAnalyser GUI closed the connection without responding.");

            var response = JsonSerializer.Deserialize<TraceBridgeResponse>(responseBytes, JsonOptions)
                ?? throw new InvalidOperationException("Received an empty response from the HttpTraceAnalyser GUI.");

            if (response.Error is not null)
                throw new InvalidOperationException(response.Error);

            return response.Result;
        }
    }
}
