using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Loads HTTP requests exported by the EdgeCapture browser extension. The extension captures
    /// request metadata and bodies, but does not currently export response data.
    /// </summary>
    public sealed class EdgeCaptureTraceFile : HttpTraceFile
    {
        public EdgeCaptureTraceFile(string filePath) : base(filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("traces", out var traces) ||
                traces.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The EdgeCapture export must contain a traces array.");
            }

            foreach (var trace in traces.EnumerateArray())
            {
                if (trace.ValueKind != JsonValueKind.Object)
                    continue;

                var method = GetString(trace, "method");
                var urlText = GetString(trace, "url");
                if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(urlText))
                    continue;

                var request = new HttpRequest(
                    GetTimestamp(trace),
                    ParseHeaders(trace),
                    ParseBody(trace),
                    method,
                    ParseUrl(urlText, GetString(trace, "pageUrl") ?? GetString(trace, "frameUrl")));

                AddRow(request, null);
            }
        }

        public static bool LooksLikeEdgeCapture(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var document = JsonDocument.Parse(stream);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("traces", out var traces) &&
                    traces.ValueKind == JsonValueKind.Array;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static DateTimeOffset? GetTimestamp(JsonElement trace)
        {
            if (trace.TryGetProperty("timestamp", out var timestamp) &&
                timestamp.ValueKind == JsonValueKind.Number &&
                timestamp.TryGetInt64(out var milliseconds))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            return null;
        }

        private static List<KeyValuePair<string, string>> ParseHeaders(JsonElement trace)
        {
            var headers = new List<KeyValuePair<string, string>>();
            if (!trace.TryGetProperty("headers", out var headerEntries) ||
                headerEntries.ValueKind != JsonValueKind.Array)
            {
                return headers;
            }

            foreach (var header in headerEntries.EnumerateArray())
            {
                if (header.ValueKind != JsonValueKind.Array || header.GetArrayLength() < 2)
                    continue;

                var name = GetArrayString(header, 0);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                headers.Add(new KeyValuePair<string, string>(name, GetArrayString(header, 1) ?? string.Empty));
            }

            return headers;
        }

        private static byte[] ParseBody(JsonElement trace)
        {
            if (!trace.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
                return Array.Empty<byte>();

            var type = GetString(body, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                return GetUtf8Bytes(GetString(body, "text"));

            if (string.Equals(type, "arraybuffer", StringComparison.OrdinalIgnoreCase))
                return DecodeBase64(GetString(body, "dataBase64"));

            if (string.Equals(type, "formdata", StringComparison.OrdinalIgnoreCase))
                return GetFormDataPayload(body);

            return Array.Empty<byte>();
        }

        private static byte[] GetFormDataPayload(JsonElement body)
        {
            if (!body.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return Array.Empty<byte>();

            var formData = new StringBuilder();
            foreach (var entry in entries.EnumerateArray())
            {
                var key = GetString(entry, "key");
                if (string.IsNullOrEmpty(key) || !entry.TryGetProperty("value", out var value) ||
                    value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var text = GetString(value, "text") ?? GetString(value, "dataBase64") ?? string.Empty;
                if (formData.Length > 0)
                    formData.AppendLine();
                formData.Append(key).Append('=').Append(text);
            }

            return GetUtf8Bytes(formData.ToString());
        }

        private static Uri ParseUrl(string urlText, string? baseUrl)
        {
            var trimmedUrl = urlText.Trim();
            if (Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var absolute))
                return absolute;

            if (!string.IsNullOrWhiteSpace(baseUrl) &&
                Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, trimmedUrl, out var relative))
            {
                return relative;
            }

            return Uri.TryCreate(trimmedUrl, UriKind.Relative, out var relativeOnly)
                ? relativeOnly
                : new Uri("about:blank");
        }

        private static byte[] DecodeBase64(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return Array.Empty<byte>();

            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return GetUtf8Bytes(value);
            }
        }

        private static byte[] GetUtf8Bytes(string? value)
            => string.IsNullOrEmpty(value) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);

        private static string? GetArrayString(JsonElement array, int index)
            => array[index].ValueKind == JsonValueKind.String ? array[index].GetString() : null;

        private static string? GetString(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }
}
