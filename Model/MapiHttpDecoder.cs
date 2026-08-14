using System;
using System.Collections.Generic;
using System.Text;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Minimal decoder for the MAPI-over-HTTP protocol (MS-OXCMAPIHTTP).
    /// Extracts protocol headers and parses the response meta-tag stream; the remainder of
    /// the payload is exposed as raw bytes for a hex dump. Full ROP/EMSMDB parsing is out
    /// of scope (see Office-Inspectors-for-Fiddler/MAPIInspector for a complete decoder).
    /// </summary>
    public static class MapiHttpDecoder
    {
        // Headers that carry MAPI/HTTP protocol metadata (case-insensitive).
        private static readonly HashSet<string> MapiHeaderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "X-RequestType",
            "X-RequestId",
            "X-ClientInfo",
            "X-ClientApplication",
            "X-ServerApplication",
            "X-ExpirationInfo",
            "X-ResponseCode",
            "X-PendingPeriod",
            "X-ElapsedTime",
            "X-StartTime",
            "Content-Type",
        };

        // Known response meta-tags per MS-OXCMAPIHTTP 2.2.2.2.
        private static readonly HashSet<string> KnownMetaTags = new(StringComparer.Ordinal)
        {
            "PROCESSING",
            "PENDING",
            "DONE",
        };

        /// <summary>Returns true if the message looks like MAPI/HTTP traffic.</summary>
        public static bool IsMapiHttp(HttpMessage? message)
        {
            if (message is null)
                return false;

            foreach (var h in message.Headers)
            {
                if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                    h.Value.IndexOf("mapi-http", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                if (h.Key.StartsWith("X-RequestType", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.StartsWith("X-ResponseCode", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static MapiDecodeResult Decode(HttpMessage message, bool isResponse)
        {
            var result = new MapiDecodeResult { IsResponse = isResponse };

            // Collect MAPI-related headers in original order.
            foreach (var h in message.Headers)
            {
                if (MapiHeaderNames.Contains(h.Key))
                    result.MapiHeaders.Add(h);
            }

            var payload = message.Payload;
            int offset = 0;

            if (isResponse)
            {
                // Response body: sequence of null-terminated ASCII meta-tags followed by the
                // response body (StatusCode + AuxiliaryBufferSize + AuxiliaryBuffer + ResponseBody).
                while (offset < payload.Length)
                {
                    if (!TryReadNullTerminatedAscii(payload, offset, maxLen: 32, out var tag, out var consumed))
                        break;

                    if (!KnownMetaTags.Contains(tag))
                        break;

                    result.MetaTags.Add(tag);
                    offset += consumed;

                    if (tag == "DONE")
                        break;
                }
            }

            result.BodyOffset = offset;
            result.BodyLength = Math.Max(0, payload.Length - offset);
            return result;
        }

        private static bool TryReadNullTerminatedAscii(byte[] buffer, int start, int maxLen, out string value, out int consumed)
        {
            value = string.Empty;
            consumed = 0;
            int end = Math.Min(buffer.Length, start + maxLen);
            for (int i = start; i < end; i++)
            {
                byte b = buffer[i];
                if (b == 0)
                {
                    value = Encoding.ASCII.GetString(buffer, start, i - start);
                    consumed = (i - start) + 1;
                    return true;
                }
                // Meta-tags are uppercase ASCII letters only.
                if (b < 'A' || b > 'Z')
                    return false;
            }
            return false;
        }

        /// <summary>Produces a canonical hex+ASCII dump of the given byte range.</summary>
        public static string HexDump(byte[] buffer, int offset, int length, int bytesPerLine = 16)
        {
            if (length <= 0 || offset >= buffer.Length)
                return string.Empty;

            length = Math.Min(length, buffer.Length - offset);
            var sb = new StringBuilder(length * 4);
            for (int i = 0; i < length; i += bytesPerLine)
            {
                int lineLen = Math.Min(bytesPerLine, length - i);
                sb.AppendFormat("{0:X8}  ", i);

                // Hex bytes.
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (j < lineLen)
                        sb.AppendFormat("{0:X2} ", buffer[offset + i + j]);
                    else
                        sb.Append("   ");
                    if (j == 7)
                        sb.Append(' ');
                }

                sb.Append(' ');

                // ASCII gutter.
                for (int j = 0; j < lineLen; j++)
                {
                    byte b = buffer[offset + i + j];
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    public sealed class MapiDecodeResult
    {
        public bool IsResponse { get; set; }
        public List<KeyValuePair<string, string>> MapiHeaders { get; } = new();
        public List<string> MetaTags { get; } = new();
        public int BodyOffset { get; set; }
        public int BodyLength { get; set; }
    }
}
