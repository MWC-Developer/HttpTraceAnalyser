using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace HttpTraceAnalyser.Model
{
    internal sealed record EasDiagnostic(string Severity, string Message);

    internal sealed record EasStatus(string Value, string Location);

    internal sealed record EasAnalysis(
        bool IsEas,
        string? Command,
        IReadOnlyList<EasStatus> ResponseStatuses,
        IReadOnlyList<EasDiagnostic> Diagnostics);

    internal static class EasAnalyzer
    {
        public static EasAnalysis Analyze(HttpRequest request, HttpResponse? response)
        {
            var isEas = IsEas(request) || IsEas(response);
            if (!isEas)
                return new EasAnalysis(false, null, Array.Empty<EasStatus>(), Array.Empty<EasDiagnostic>());

            var diagnostics = new List<EasDiagnostic>();
            var command = GetCommand(request);
            AddRequestDiagnostics(request, command, diagnostics);
            AddResponseDiagnostics(request, response, command, diagnostics);

            return new EasAnalysis(
                true,
                command,
                GetResponseStatuses(response),
                diagnostics);
        }

        private static bool IsEas(HttpRequest? request)
            => request is not null &&
               (request.DecodedEasWbxml is not null ||
                request.Url.AbsolutePath.Contains("microsoft-server-activesync", StringComparison.OrdinalIgnoreCase) ||
                HasWbxmlContentType(request.Headers));

        private static bool IsEas(HttpResponse? response)
            => response is not null && (response.DecodedEasWbxml is not null || HasWbxmlContentType(response.Headers));

        private static bool HasWbxmlContentType(IReadOnlyList<KeyValuePair<string, string>> headers)
            => headers.Any(header => string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                && header.Value.Contains("application/vnd.ms-sync.wbxml", StringComparison.OrdinalIgnoreCase));

        private static string? GetCommand(HttpRequest request)
        {
            var queryCommand = request.Url.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part.Length == 2 && string.Equals(part[0], "Cmd", StringComparison.OrdinalIgnoreCase));
            if (queryCommand is { Length: 2 })
                return Uri.UnescapeDataString(queryCommand[1]);

            return GetRootElementName(request.DecodedEasWbxml);
        }

        private static void AddRequestDiagnostics(HttpRequest request, string? command, List<EasDiagnostic> diagnostics)
        {
            if (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new EasDiagnostic("Error", "Exchange ActiveSync supports OPTIONS and POST requests; GET cannot carry an EAS command."));
                return;
            }

            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new EasDiagnostic("Info", "OPTIONS discovers server-supported ActiveSync protocol versions and commands; an empty request body is expected."));
                return;
            }

            if (request.Payload.Length == 0)
            {
                var message = command?.ToUpperInvariant() switch
                {
                    "PING" => "Empty Ping request: the server uses the cached Ping configuration.",
                    "SYNC" => "Empty Sync request: the server uses the cached synchronization state.",
                    _ => "The EAS request body is empty.",
                };
                diagnostics.Add(new EasDiagnostic("Info", message));
            }
        }

        private static void AddResponseDiagnostics(HttpRequest request, HttpResponse? response, string? command, List<EasDiagnostic> diagnostics)
        {
            if (response is null)
            {
                diagnostics.Add(new EasDiagnostic("Warning", "No HTTP response was captured."));
                return;
            }

            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new EasDiagnostic("Info", $"OPTIONS response. Supported protocol versions: {GetHeader(response.Headers, "MS-ASProtocolVersions") ?? "not provided"}. Supported commands: {GetHeader(response.Headers, "MS-ASProtocolCommands") ?? "not provided"}."));
                return;
            }

            if (response.Payload.Length == 0)
            {
                var message = command?.ToUpperInvariant() switch
                {
                    "SYNC" => "Empty Sync response: this generally indicates that the server found no changes.",
                    "SENDMAIL" => "Empty SendMail response: the message was sent successfully.",
                    _ => "The EAS response body is empty. This can be valid for some commands.",
                };
                diagnostics.Add(new EasDiagnostic("Info", message));
            }

            var diagnosticServer = GetHeader(response.Headers, "X-DiagInfo")
                ?? GetHeader(response.Headers, "X-FEServer")
                ?? "unknown server";
            var status = response.StatusCode;
            switch (status)
            {
                case 200:
                case 204:
                    return;
                case 301:
                case 302:
                    diagnostics.Add(new EasDiagnostic("Error", $"HTTP {status} redirect to {GetHeader(response.Headers, "Location") ?? "an unknown location"}."));
                    return;
                case 401:
                    diagnostics.Add(new EasDiagnostic("Error", "HTTP 401 Unauthorized. This can be part of an authentication handshake, but repeated responses indicate an authentication problem."));
                    return;
                case 404:
                    var casError = GetHeader(response.Headers, "X-CasErrorCode");
                    diagnostics.Add(new EasDiagnostic("Error", casError?.Contains("MailboxGuidWithDomainNotFound", StringComparison.OrdinalIgnoreCase) == true
                        ? $"HTTP 404 from {diagnosticServer}: MailboxGuidWithDomainNotFound. Verify that the mailbox exists and is licensed."
                        : $"HTTP 404 Not Found from {diagnosticServer}."));
                    return;
                case 456:
                    diagnostics.Add(new EasDiagnostic("Error", $"HTTP 456 Unauthorized from {diagnosticServer}. The account may not have completed its first sign-in or may be locked."));
                    return;
                case 500:
                    diagnostics.Add(new EasDiagnostic("Error", $"HTTP 500 Internal Server Error from {diagnosticServer}. Review the response body and diagnostic headers."));
                    return;
                case 503:
                    diagnostics.Add(new EasDiagnostic("Error", $"HTTP 503 Service Unavailable from {diagnosticServer}. {Describe503(response)}"));
                    return;
                case not null:
                    diagnostics.Add(new EasDiagnostic("Warning", $"HTTP {status} {response.ReasonPhrase}."));
                    return;
            }
        }

        private static string Describe503(HttpResponse response)
        {
            var failureContext = GetHeader(response.Headers, "X-FailureContext");
            return string.IsNullOrWhiteSpace(failureContext)
                ? "Review X-CasErrorCode and X-FailureContext headers when available."
                : $"X-FailureContext: {failureContext}";
        }

        private static IReadOnlyList<EasStatus> GetResponseStatuses(HttpResponse? response)
        {
            if (string.IsNullOrWhiteSpace(response?.DecodedEasWbxml))
                return Array.Empty<EasStatus>();

            try
            {
                var document = XDocument.Parse(response.DecodedEasWbxml);
                return document.Descendants()
                    .Where(element => element.Name.LocalName.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    .Select(element => new EasStatus(
                        element.Value.Trim(),
                        string.Join("/", element.AncestorsAndSelf().Reverse().Select(ancestor => ancestor.Name.LocalName))))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<EasStatus>();
            }
        }

        private static string? GetRootElementName(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return null;

            try
            {
                return XDocument.Parse(xml).Root?.Name.LocalName;
            }
            catch
            {
                return null;
            }
        }

        private static string? GetHeader(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
            => headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
