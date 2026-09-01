using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// One segment of a decomposed REST URL path, pairing a resource "collection" name
    /// (e.g. "users", "mailFolders") with the identifier that follows it, when present.
    /// </summary>
    public sealed class RestPathSegment
    {
        public RestPathSegment(string collection, string? identifier, bool identifierIsWellKnown)
        {
            Collection = collection;
            Identifier = identifier;
            IdentifierIsWellKnown = identifierIsWellKnown;
        }

        /// <summary>The resource collection/entity name, e.g. "users", "mailFolders".</summary>
        public string Collection { get; }

        /// <summary>The identifier following the collection name (id, alias, or well-known value), if any.</summary>
        public string? Identifier { get; }

        /// <summary>True when <see cref="Identifier"/> is a recognised well-known value such as "me".</summary>
        public bool IdentifierIsWellKnown { get; }
    }

    /// <summary>
    /// Result of analysing a request's URL as a REST API call (e.g. Microsoft Graph style).
    /// </summary>
    public sealed class RestAnalysisResult
    {
        public RestAnalysisResult(
            bool isRest,
            string? apiVersion,
            IReadOnlyList<RestPathSegment> segments,
            IReadOnlyList<KeyValuePair<string, string>> queryParameters)
        {
            IsRest = isRest;
            ApiVersion = apiVersion;
            Segments = segments;
            QueryParameters = queryParameters;
        }

        /// <summary>True if the request appears to be a REST API call worth analysing.</summary>
        public bool IsRest { get; }

        /// <summary>API version segment, if detected (e.g. "v1.0", "beta", "v1").</summary>
        public string? ApiVersion { get; }

        /// <summary>Ordered breakdown of the URL path into collection/identifier pairs.</summary>
        public IReadOnlyList<RestPathSegment> Segments { get; }

        /// <summary>Query string parameters (e.g. OData $select, $filter).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> QueryParameters { get; }
    }

    /// <summary>
    /// Analyses HTTP requests for REST API structure, decomposing resource-style URLs
    /// (as used by Microsoft Graph and similar APIs) into their component parts.
    /// </summary>
    public static class RestAnalyzer
    {
        // Well-known non-identifier tokens that can appear in place of an id (aliases, verbs).
        private static readonly HashSet<string> WellKnownIdentifiers = new(StringComparer.OrdinalIgnoreCase)
        {
            "me",
        };

        // Segments that represent an OData/Graph action or function rather than an identifier
        // (start with $ or are a bare verb such as $count, $ref, $value).
        private static readonly HashSet<string> ODataSegments = new(StringComparer.OrdinalIgnoreCase)
        {
            "$ref", "$count", "$value", "$batch", "$metadata",
        };

        private static readonly Regex GuidRegex = new(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        private static readonly Regex ApiVersionRegex = new(
            @"^(v\d+(\.\d+)?|beta)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Attempts to decompose the given request URL as a REST resource path.
        /// </summary>
        public static RestAnalysisResult Analyze(Uri? url)
        {
            if (url is null)
                return new RestAnalysisResult(false, null, Array.Empty<RestPathSegment>(), Array.Empty<KeyValuePair<string, string>>());

            string path = url.IsAbsoluteUri ? url.AbsolutePath : url.OriginalString.Split('?')[0];
            var rawSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (rawSegments.Length == 0)
                return new RestAnalysisResult(false, null, Array.Empty<RestPathSegment>(), Array.Empty<KeyValuePair<string, string>>());

            int start = 0;
            string? apiVersion = null;
            if (ApiVersionRegex.IsMatch(rawSegments[0]))
            {
                apiVersion = rawSegments[0];
                start = 1;
            }

            var segments = new List<RestPathSegment>();
            for (int i = start; i < rawSegments.Length; i++)
            {
                string collection = Uri.UnescapeDataString(rawSegments[i]);

                string? identifier = null;
                bool wellKnown = false;

                if (i + 1 < rawSegments.Length)
                {
                    string next = Uri.UnescapeDataString(rawSegments[i + 1]);
                    if (ODataSegments.Contains(next))
                    {
                        // Treat as its own pseudo-segment on the next loop iteration.
                    }
                    else if (LooksLikeIdentifier(next))
                    {
                        identifier = next;
                        wellKnown = WellKnownIdentifiers.Contains(next);
                        i++; // consume the identifier segment
                    }
                }

                segments.Add(new RestPathSegment(collection, identifier, wellKnown));
            }

            var query = new List<KeyValuePair<string, string>>();
            if (url.IsAbsoluteUri && !string.IsNullOrEmpty(url.Query))
            {
                var q = url.Query.TrimStart('?');
                foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = pair.IndexOf('=');
                    if (idx >= 0)
                        query.Add(new KeyValuePair<string, string>(
                            Uri.UnescapeDataString(pair[..idx]),
                            Uri.UnescapeDataString(pair[(idx + 1)..])));
                    else
                        query.Add(new KeyValuePair<string, string>(Uri.UnescapeDataString(pair), string.Empty));
                }
            }

            bool isRest = segments.Count > 0;
            return new RestAnalysisResult(isRest, apiVersion, segments, query);
        }

        /// <summary>
        /// Heuristic to decide whether a path segment is an identifier/value for the
        /// preceding collection segment rather than a nested collection name itself.
        /// Recognises GUIDs, well-known aliases (e.g. "me"), user-principal-name-like
        /// values (containing '@'), and base64url-ish opaque ids.
        /// </summary>
        private static bool LooksLikeIdentifier(string segment)
        {
            if (segment.StartsWith('$'))
                return false;

            if (WellKnownIdentifiers.Contains(segment))
                return true;

            if (GuidRegex.IsMatch(segment))
                return true;

            if (segment.Contains('@'))
                return true; // userPrincipalName

            // Long opaque tokens (e.g. mailFolder / message ids) are typically base64url,
            // mixed-case alphanumeric with '-' and '_' and noticeably longer than typical
            // collection names.
            if (segment.Length >= 16 && Regex.IsMatch(segment, @"^[A-Za-z0-9\-_=]+$") &&
                Regex.IsMatch(segment, @"[0-9]") && Regex.IsMatch(segment, @"[A-Za-z]"))
                return true;

            return false;
        }
    }
}
