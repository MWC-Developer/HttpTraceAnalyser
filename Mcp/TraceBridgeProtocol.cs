using System.Text.Json.Serialization;

namespace HttpTraceAnalyser.Mcp
{
    /// <summary>
    /// Wire contract used between the stdio MCP server process (started via
    /// "HttpTraceAnalyser.exe --mcp-stdio") and the already-running WPF GUI process that owns
    /// the live in-memory trace state. Transported as newline-delimited JSON over a local,
    /// current-user-only named pipe (see <see cref="TraceBridgeServer"/>/<see cref="TraceBridgeClient"/>).
    /// This is intentionally a minimal, fixed schema (tool name + string-keyed arguments) rather
    /// than a general-purpose RPC mechanism, so every field is validated/typed at the call site
    /// in <see cref="TraceMcpTools"/> - the bridge itself performs no dynamic code execution.
    /// </summary>
    public sealed class TraceBridgeRequest
    {
        /// <summary>Name of the tool to invoke (must match a method on <see cref="TraceMcpTools"/>).</summary>
        [JsonPropertyName("tool")]
        public string Tool { get; set; } = string.Empty;

        /// <summary>Arguments for the tool, keyed by parameter name, serialized as JSON values.</summary>
        [JsonPropertyName("args")]
        public Dictionary<string, System.Text.Json.JsonElement> Args { get; set; } = new();
    }

    /// <summary>Response returned for a <see cref="TraceBridgeRequest"/>.</summary>
    public sealed class TraceBridgeResponse
    {
        /// <summary>The tool's string result, when the call succeeded.</summary>
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        /// <summary>Set when the call failed (bridge-level error, not a tool-reported failure string).</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
