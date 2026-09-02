using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace HttpTraceAnalyser.Model
{
    /// <summary>A single element found under the SOAP &lt;Header&gt; block, flattened for display.</summary>
    public sealed class SoapHeaderEntry
    {
        public SoapHeaderEntry(string name, string value)
        {
            Name = name;
            Value = value;
        }

        /// <summary>Local name of the header element (e.g. "ExchangeImpersonation").</summary>
        public string Name { get; }

        /// <summary>Flattened text content / summary of the header element.</summary>
        public string Value { get; }
    }

    /// <summary>Result of analysing a request as a SOAP call.</summary>
    public sealed class SoapRequestAnalysis
    {
        public static readonly SoapRequestAnalysis None = new(
            isSoap: false, method: null, headers: Array.Empty<SoapHeaderEntry>(), anchorMailbox: null);

        public SoapRequestAnalysis(bool isSoap, string? method, IReadOnlyList<SoapHeaderEntry> headers, string? anchorMailbox)
        {
            IsSoap = isSoap;
            Method = method;
            Headers = headers;
            AnchorMailbox = anchorMailbox;
        }

        /// <summary>True if the request payload parses as a SOAP envelope.</summary>
        public bool IsSoap { get; }

        /// <summary>The SOAP operation/method name (first child element of the Body), if found.</summary>
        public string? Method { get; }

        /// <summary>Elements found under the SOAP &lt;Header&gt; block (e.g. ExchangeImpersonation for EWS).</summary>
        public IReadOnlyList<SoapHeaderEntry> Headers { get; }

        /// <summary>Value of the X-AnchorMailbox HTTP header, if present (routing hint used by EWS/Graph).</summary>
        public string? AnchorMailbox { get; }
    }

    /// <summary>A human-readable summary of one entity (folder, item, etc.) returned by a SOAP response.</summary>
    public sealed class SoapOverviewEntry
    {
        public SoapOverviewEntry(string title, IReadOnlyList<KeyValuePair<string, string>> properties)
        {
            Title = title;
            Properties = properties;
        }

        /// <summary>Display title for the entity, e.g. a folder/item display name, or its element type.</summary>
        public string Title { get; }

        /// <summary>Flattened property name/value pairs describing the entity.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Properties { get; }
    }

    /// <summary>Human-readable summary of a single &lt;*ResponseMessage&gt; element.</summary>
    public sealed class SoapResponseMessageOverview
    {
        public SoapResponseMessageOverview(string? responseClass, string? responseCode, IReadOnlyList<SoapOverviewEntry> entries)
        {
            ResponseClass = responseClass;
            ResponseCode = responseCode;
            Entries = entries;
        }

        /// <summary>The ResponseClass attribute value (e.g. "Success", "Error", "Warning"), if present.</summary>
        public string? ResponseClass { get; }

        /// <summary>The ResponseCode element value (e.g. "NoError"), if present.</summary>
        public string? ResponseCode { get; }

        /// <summary>Entities (folders/items/etc.) found within this response message.</summary>
        public IReadOnlyList<SoapOverviewEntry> Entries { get; }
    }

    /// <summary>Result of analysing a response for SOAP fault content.</summary>
    public sealed class SoapResponseAnalysis
    {
        public static readonly SoapResponseAnalysis None = new(
            isSoap: false, isFault: false, faultCode: null, faultReason: null,
            operationName: null, messages: Array.Empty<SoapResponseMessageOverview>());

        public SoapResponseAnalysis(
            bool isSoap,
            bool isFault,
            string? faultCode,
            string? faultReason,
            string? operationName,
            IReadOnlyList<SoapResponseMessageOverview> messages)
        {
            IsSoap = isSoap;
            IsFault = isFault;
            FaultCode = faultCode;
            FaultReason = faultReason;
            OperationName = operationName;
            Messages = messages;
        }

        /// <summary>True if the response payload parses as a SOAP envelope.</summary>
        public bool IsSoap { get; }

        /// <summary>
        /// True if the SOAP body contains a &lt;Fault&gt; element. Note that EWS/SOAP services
        /// frequently return HTTP 200 even when the SOAP payload describes an error, so this
        /// should be checked independently of the HTTP status code.
        /// </summary>
        public bool IsFault { get; }

        /// <summary>SOAP 1.1 faultcode / SOAP 1.2 Fault/Code/Value, if present.</summary>
        public string? FaultCode { get; }

        /// <summary>SOAP 1.1 faultstring / SOAP 1.2 Fault/Reason/Text, if present.</summary>
        public string? FaultReason { get; }

        /// <summary>The response operation name (e.g. "GetFolder"), derived from the Body's response element.</summary>
        public string? OperationName { get; }

        /// <summary>Human-readable overview of each response message (there can be more than one for batch operations).</summary>
        public IReadOnlyList<SoapResponseMessageOverview> Messages { get; }
    }

    /// <summary>
    /// Extracts SOAP-specific information (method, headers, faults) from HTTP request/response
    /// payloads. Detection is based on the payload shape (a SOAP &lt;Envelope&gt; root element)
    /// rather than the trace file format, so it applies equally to EWS traces and any other
    /// SOAP-based capture.
    /// </summary>
    public static class SoapAnalyzer
    {
        private static readonly string[] SoapNamespaces =
        {
            "http://schemas.xmlsoap.org/soap/envelope/",
            "http://www.w3.org/2003/05/soap-envelope",
        };

        public static SoapRequestAnalysis AnalyzeRequest(HttpRequest request)
        {
            if (request is null)
                return SoapRequestAnalysis.None;

            var envelope = TryParseEnvelope(request.Payload, request.Headers);
            if (envelope is null)
                return SoapRequestAnalysis.None;

            var body = GetChild(envelope, "Body");
            var method = body?.Elements().FirstOrDefault()?.Name.LocalName;

            var headerEntries = new List<SoapHeaderEntry>();
            var header = GetChild(envelope, "Header");
            if (header is not null)
            {
                foreach (var element in header.Elements())
                    headerEntries.Add(new SoapHeaderEntry(element.Name.LocalName, Flatten(element)));
            }

            var anchorMailbox = FindHeaderValue(request.Headers, "X-AnchorMailbox");

            return new SoapRequestAnalysis(isSoap: true, method, headerEntries, anchorMailbox);
        }

        // Elements considered "identity"-like: their text becomes part of the entry title
        // rather than a regular property, in priority order.
        private static readonly string[] TitleFieldNames = { "DisplayName", "Subject", "Name" };

        // Container element names whose children are individual response messages/entities,
        // used to walk from e.g. m:Folders -> t:Folder, m:Items -> t:Message/t:CalendarItem/etc.
        private static readonly string[] CollectionContainerNames = { "Folders", "Items" };

        // Element names that should not be flattened into properties because they are either
        // structural (ids handled separately) or too verbose to usefully summarize inline.
        private static readonly string[] SkippedPropertyNames = { "EffectiveRights", "ReplicaList", "PermissionSet" };

        public static SoapResponseAnalysis AnalyzeResponse(HttpResponse? response)
        {
            if (response is null)
                return SoapResponseAnalysis.None;

            var envelope = TryParseEnvelope(response.Payload, response.Headers);
            if (envelope is null)
                return SoapResponseAnalysis.None;

            var body = GetChild(envelope, "Body");
            var operationElement = body?.Elements().FirstOrDefault();
            var operationName = operationElement?.Name.LocalName;

            var fault = body?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Fault", StringComparison.OrdinalIgnoreCase));
            if (fault is not null)
            {
                // SOAP 1.1: <faultcode>/<faultstring> (no namespace on the child element names).
                var faultCode = fault.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "faultcode", StringComparison.OrdinalIgnoreCase))?.Value;
                var faultReason = fault.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "faultstring", StringComparison.OrdinalIgnoreCase))?.Value;

                // SOAP 1.2: <Code><Value>...</Value></Code> / <Reason><Text>...</Text></Reason>
                if (faultCode is null)
                {
                    var codeEl = fault.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Code", StringComparison.OrdinalIgnoreCase));
                    faultCode = codeEl?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Value", StringComparison.OrdinalIgnoreCase))?.Value;
                }
                if (faultReason is null)
                {
                    var reasonEl = fault.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Reason", StringComparison.OrdinalIgnoreCase));
                    faultReason = reasonEl?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Text", StringComparison.OrdinalIgnoreCase))?.Value;
                }

                return new SoapResponseAnalysis(
                    isSoap: true, isFault: true, faultCode?.Trim(), faultReason?.Trim(), operationName,
                    messages: Array.Empty<SoapResponseMessageOverview>());
            }

            var messages = BuildResponseMessageOverviews(operationElement);
            return new SoapResponseAnalysis(
                isSoap: true, isFault: false, faultCode: null, faultReason: null, operationName, messages);
        }

        /// <summary>
        /// Builds a human-readable overview for each response message (e.g. "GetFolderResponseMessage")
        /// found under the operation's response element, extracting any entities in known collection
        /// containers (Folders/Items) into individual summaries.
        /// </summary>
        private static IReadOnlyList<SoapResponseMessageOverview> BuildResponseMessageOverviews(XElement? operationElement)
        {
            if (operationElement is null)
                return Array.Empty<SoapResponseMessageOverview>();

            var responseMessagesContainer = GetChild(operationElement, "ResponseMessages");
            var responseMessageElements = responseMessagesContainer?.Elements()
                ?? (operationElement.Elements().Any(e => e.Name.LocalName.EndsWith("ResponseMessage", StringComparison.OrdinalIgnoreCase))
                    ? operationElement.Elements()
                    : Enumerable.Empty<XElement>());

            var overviews = new List<SoapResponseMessageOverview>();
            foreach (var messageElement in responseMessageElements)
            {
                var responseClass = messageElement.Attribute("ResponseClass")?.Value;
                var responseCode = messageElement.Elements()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "ResponseCode", StringComparison.OrdinalIgnoreCase))?.Value;

                var entries = new List<SoapOverviewEntry>();
                foreach (var container in messageElement.Elements())
                {
                    // Skip the fields already surfaced above.
                    if (string.Equals(container.Name.LocalName, "ResponseCode", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(container.Name.LocalName, "MessageText", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var isCollectionContainer = CollectionContainerNames.Any(n =>
                        string.Equals(container.Name.LocalName, n, StringComparison.OrdinalIgnoreCase));

                    if (isCollectionContainer)
                    {
                        foreach (var entity in container.Elements())
                            entries.Add(BuildOverviewEntry(entity));
                    }
                    else if (container.HasElements || !string.IsNullOrWhiteSpace(container.Value))
                    {
                        // e.g. RootFolder (FindItem/FindFolder) which wraps Items/Folders plus paging attributes.
                        var nestedCollections = container.Elements()
                            .Where(e => CollectionContainerNames.Any(n => string.Equals(e.Name.LocalName, n, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        if (nestedCollections.Count > 0)
                        {
                            foreach (var nested in nestedCollections)
                            foreach (var entity in nested.Elements())
                                entries.Add(BuildOverviewEntry(entity));
                        }
                        else
                        {
                            entries.Add(BuildOverviewEntry(container));
                        }
                    }
                }

                overviews.Add(new SoapResponseMessageOverview(responseClass, responseCode, entries));
            }

            return overviews;
        }

        /// <summary>Builds a titled, flattened property list for a single entity element (folder, item, etc.).</summary>
        private static SoapOverviewEntry BuildOverviewEntry(XElement entity)
        {
            string? title = null;
            var properties = new List<KeyValuePair<string, string>>();

            foreach (var field in entity.Elements())
            {
                var name = field.Name.LocalName;
                if (SkippedPropertyNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (field.HasElements)
                {
                    // Nested complex value (e.g. Sender/Mailbox) - flatten to a compact summary.
                    var flattened = Flatten(field);
                    if (!string.IsNullOrEmpty(flattened))
                        properties.Add(new KeyValuePair<string, string>(name, flattened));
                    continue;
                }

                // Id-carrying elements (FolderId/ItemId/ParentFolderId) expose Id/ChangeKey as attributes.
                var idAttr = field.Attribute("Id")?.Value;
                if (idAttr is not null)
                {
                    properties.Add(new KeyValuePair<string, string>(name, idAttr));
                    continue;
                }

                var value = field.Value?.Trim() ?? string.Empty;
                if (title is null && TitleFieldNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    title = value;
                }
                properties.Add(new KeyValuePair<string, string>(name, value));
            }

            title ??= entity.Name.LocalName;
            return new SoapOverviewEntry(title, properties);
        }

        /// <summary>Attempts to extract just the SOAP method name (used by the trace grid column).</summary>
        internal static string? TryExtractMethod(byte[]? payload, IReadOnlyList<KeyValuePair<string, string>>? headers)
        {
            var envelope = TryParseEnvelope(payload, headers);
            var body = envelope is null ? null : GetChild(envelope, "Body");
            return body?.Elements().FirstOrDefault()?.Name.LocalName;
        }

        private static XElement? TryParseEnvelope(byte[]? payload, IReadOnlyList<KeyValuePair<string, string>>? headers)
        {
            if (payload is null || payload.Length == 0)
                return null;

            var contentType = FindHeaderValue(headers, "Content-Type");
            var trimmedStart = Array.FindIndex(payload, b => b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n');
            bool looksLikeXml = trimmedStart >= 0 && payload[trimmedStart] == (byte)'<';
            bool contentTypeSuggestsSoap = contentType is not null &&
                (contentType.Contains("soap", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Contains("xml", StringComparison.OrdinalIgnoreCase));

            if (!looksLikeXml && !contentTypeSuggestsSoap)
                return null;

            try
            {
                var text = Encoding.UTF8.GetString(payload);
                var doc = XDocument.Parse(text);
                var root = doc.Root;
                if (root is null || !string.Equals(root.Name.LocalName, "Envelope", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (!SoapNamespaces.Contains(root.Name.NamespaceName))
                    return null;
                return root;
            }
            catch
            {
                return null;
            }
        }

        private static XElement? GetChild(XElement parent, string localName)
            => parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Renders an element's descendant text content as a compact single-line summary.</summary>
        private static string Flatten(XElement element)
        {
            var leaves = element.DescendantsAndSelf()
                .Where(e => !e.HasElements && !string.IsNullOrWhiteSpace(e.Value))
                .Select(e => $"{e.Name.LocalName}={e.Value.Trim()}")
                .ToList();
            return leaves.Count > 0 ? string.Join(", ", leaves) : string.Empty;
        }

        private static string? FindHeaderValue(IReadOnlyList<KeyValuePair<string, string>>? headers, string name)
        {
            if (headers is null)
                return null;
            foreach (var h in headers)
            {
                if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                    return h.Value;
            }
            return null;
        }
    }
}
