using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Loads a Fiddler Session Archive (.saz) file.
    /// A .saz is a zip archive containing, per session, three files under <c>raw/</c>:
    /// <c>NNN_c.txt</c> (raw client request), <c>NNN_s.txt</c> (raw server response)
    /// and <c>NNN_m.xml</c> (session metadata including timers).
    /// </summary>
    public sealed class SazTraceFile : HttpTraceFile
    {
        public SazTraceFile(string filePath) : base(filePath)
        {
            using var zip = ZipFile.OpenRead(filePath);
            Load(zip);
        }

        private void Load(ZipArchive zip)
        {
            // Group entries by session id (leading numeric portion before "_c/_s/_m").
            var sessions = zip.Entries
                .Where(e => e.FullName.StartsWith("raw/", StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => GetSessionId(e.FullName), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in sessions)
            {
                var clientEntry = group.FirstOrDefault(e => e.Name.EndsWith("_c.txt", StringComparison.OrdinalIgnoreCase));
                if (clientEntry is null)
                    continue;

                var serverEntry = group.FirstOrDefault(e => e.Name.EndsWith("_s.txt", StringComparison.OrdinalIgnoreCase));
                var metaEntry = group.FirstOrDefault(e => e.Name.EndsWith("_m.xml", StringComparison.OrdinalIgnoreCase));

                var (requestTimestamp, responseTimestamp) = ReadTimestamps(metaEntry);

                var request = ParseRequest(clientEntry, requestTimestamp);
                var response = serverEntry is null ? null : ParseResponse(serverEntry, responseTimestamp);

                AddRow(request, response);
            }
        }

        private static string GetSessionId(string fullName)
        {
            // "raw/001_c.txt" -> "001"
            var name = Path.GetFileName(fullName);
            var underscore = name.IndexOf('_');
            return underscore <= 0 ? string.Empty : name.Substring(0, underscore);
        }

        private static (DateTimeOffset? request, DateTimeOffset? response) ReadTimestamps(ZipArchiveEntry? metaEntry)
        {
            if (metaEntry is null)
                return (null, null);

            try
            {
                using var stream = metaEntry.Open();
                var doc = XDocument.Load(stream);
                var timers = doc.Descendants("SessionTimers").FirstOrDefault();
                if (timers is null)
                    return (null, null);

                return (
                    ParseTime(timers.Attribute("ClientBeginRequest")?.Value),
                    ParseTime(timers.Attribute("ServerDoneResponse")?.Value));
            }
            catch
            {
                return (null, null);
            }
        }

        private static DateTimeOffset? ParseTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                return dto;
            }
            return null;
        }

        private static HttpRequest ParseRequest(ZipArchiveEntry entry, DateTimeOffset? timestamp)
        {
            var raw = ReadAll(entry);
            var (headerText, payload) = SplitHeadersAndBody(raw);
            var lines = SplitHeaderLines(headerText);

            var method = string.Empty;
            string target = string.Empty;
            if (lines.Count > 0)
            {
                var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    method = parts[0];
                    target = parts[1];
                }
            }

            var headers = ParseHeaders(lines);
            var url = BuildUrl(method, target, headers);

            return new HttpRequest(timestamp, headers, payload, method, url);
        }

        private static HttpResponse ParseResponse(ZipArchiveEntry entry, DateTimeOffset? timestamp)
        {
            var raw = ReadAll(entry);
            var (headerText, payload) = SplitHeadersAndBody(raw);
            var lines = SplitHeaderLines(headerText);
            var headers = ParseHeaders(lines);

            int? statusCode = null;
            string? reason = null;
            if (lines.Count > 0)
            {
                // Status line: "HTTP/1.1 200 OK"
                var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    statusCode = code;
                    if (parts.Length == 3)
                        reason = parts[2];
                }
            }

            return new HttpResponse(timestamp, headers, payload, statusCode, reason);
        }

        private static byte[] ReadAll(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static (string headers, byte[] body) SplitHeadersAndBody(byte[] raw)
        {
            // Locate CRLF CRLF (or LF LF) separating headers from body.
            for (int i = 0; i < raw.Length - 1; i++)
            {
                if (raw[i] == '\r' && i + 3 < raw.Length &&
                    raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
                {
                    var headers = Encoding.ASCII.GetString(raw, 0, i);
                    var body = new byte[raw.Length - (i + 4)];
                    Buffer.BlockCopy(raw, i + 4, body, 0, body.Length);
                    return (headers, body);
                }
                if (raw[i] == '\n' && raw[i + 1] == '\n')
                {
                    var headers = Encoding.ASCII.GetString(raw, 0, i);
                    var body = new byte[raw.Length - (i + 2)];
                    Buffer.BlockCopy(raw, i + 2, body, 0, body.Length);
                    return (headers, body);
                }
            }
            return (Encoding.ASCII.GetString(raw), Array.Empty<byte>());
        }

        private static List<string> SplitHeaderLines(string headerText)
        {
            return headerText
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();
        }

        private static List<KeyValuePair<string, string>> ParseHeaders(List<string> lines)
        {
            var result = new List<KeyValuePair<string, string>>(Math.Max(0, lines.Count - 1));
            for (int i = 1; i < lines.Count; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                result.Add(new KeyValuePair<string, string>(name, value));
            }
            return result;
        }

        private static Uri BuildUrl(string method, string target, IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            if (string.IsNullOrEmpty(target))
                return new Uri("about:blank");

            // CONNECT uses authority-form: "host:port". Turn it into an absolute URI
            // so Host/Path surface correctly in the UI.
            if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase) &&
                !target.Contains("://", StringComparison.Ordinal))
            {
                if (Uri.TryCreate($"https://{target}", UriKind.Absolute, out var connectUri))
                    return connectUri;
            }

            if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
                return absolute;

            var host = headers.FirstOrDefault(h =>
                string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase)).Value;

            if (!string.IsNullOrWhiteSpace(host))
            {
                var path = target.StartsWith('/') ? target : "/" + target;
                if (Uri.TryCreate($"http://{host}{path}", UriKind.Absolute, out var built))
                    return built;
            }

            return new Uri(target, UriKind.RelativeOrAbsolute);
        }
    }
}
