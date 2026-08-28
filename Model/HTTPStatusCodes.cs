using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Provides information about HTTP status codes including error and throttling indicators.
    /// </summary>
    public class HTTPStatusCodes
    {
        private static readonly Lazy<HTTPStatusCodes> _instance = new Lazy<HTTPStatusCodes>(() => new HTTPStatusCodes());
        private readonly Dictionary<int, StatusCodeInfo> _statusCodes;

        private HTTPStatusCodes()
        {
            _statusCodes = new Dictionary<int, StatusCodeInfo>();
            LoadStatusCodes();
        }

        /// <summary>
        /// Gets the singleton instance of HTTPStatusCodes.
        /// </summary>
        public static HTTPStatusCodes Instance => _instance.Value;

        /// <summary>
        /// Gets information for a specific HTTP status code.
        /// </summary>
        /// <param name="responseCode">The HTTP status code to look up.</param>
        /// <returns>Status code information if found, or a default unknown status if not found.</returns>
        public StatusCodeInfo GetStatusInfo(int responseCode)
        {
            if (_statusCodes.TryGetValue(responseCode, out var info))
            {
                return info;
            }

            // Return a default for unknown status codes
            return new StatusCodeInfo(
                responseCode,
                responseCode >= 400,
                false,
                $"Unknown HTTP status code {responseCode}");
        }

        private void LoadStatusCodes()
        {
            try
            {
                string csvPath = GetCsvFilePath();

                if (!File.Exists(csvPath))
                {
                    LoadDefaultStatusCodes();
                    return;
                }

                var lines = File.ReadAllLines(csvPath);

                // Skip header line
                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split(',');
                    if (parts.Length >= 4)
                    {
                        if (int.TryParse(parts[0], out int code) &&
                            bool.TryParse(parts[1], out bool isError) &&
                            bool.TryParse(parts[2], out bool isThrottling))
                        {
                            var description = parts[3];
                            // Handle descriptions that might contain commas
                            if (parts.Length > 4)
                            {
                                description = string.Join(",", parts.Skip(3));
                            }

                            _statusCodes[code] = new StatusCodeInfo(code, isError, isThrottling, description);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // If loading fails, use defaults
                LoadDefaultStatusCodes();
            }
        }

        private string GetCsvFilePath()
        {
            // Try to find the CSV file in the application directory
            string exeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string csvPath = Path.Combine(exeDirectory, "HttpStatusCodes.csv");

            // If not found there, try the current directory (useful during development)
            if (!File.Exists(csvPath))
            {
                csvPath = "HttpStatusCodes.csv";
            }

            return csvPath;
        }

        private void LoadDefaultStatusCodes()
        {
            // Fallback to a minimal set of common codes if CSV loading fails
            _statusCodes[200] = new StatusCodeInfo(200, false, false, "OK - The request succeeded.");
            _statusCodes[201] = new StatusCodeInfo(201, false, false, "Created - The request succeeded and a new resource was created.");
            _statusCodes[204] = new StatusCodeInfo(204, false, false, "No Content - There is no content to send for this request.");
            _statusCodes[301] = new StatusCodeInfo(301, false, false, "Moved Permanently - The URL has changed permanently. Check the Location header.");
            _statusCodes[302] = new StatusCodeInfo(302, false, false, "Found - The URI has changed temporarily. Check the Location header.");
            _statusCodes[304] = new StatusCodeInfo(304, false, false, "Not Modified - The resource has not been modified.");
            _statusCodes[400] = new StatusCodeInfo(400, true, false, "Bad Request - The server cannot process the request. Verify request format and parameters.");
            _statusCodes[401] = new StatusCodeInfo(401, true, false, "Unauthorized - Authentication failed or not provided. Check credentials and tokens.");
            _statusCodes[403] = new StatusCodeInfo(403, true, false, "Forbidden - Access denied. Verify permissions and authorization.");
            _statusCodes[404] = new StatusCodeInfo(404, true, false, "Not Found - The resource cannot be found. Verify the URL.");
            _statusCodes[429] = new StatusCodeInfo(429, true, true, "Too Many Requests - Rate limit exceeded. Implement exponential backoff and retry.");
            _statusCodes[500] = new StatusCodeInfo(500, true, false, "Internal Server Error - The server encountered an error. Contact support.");
            _statusCodes[502] = new StatusCodeInfo(502, true, false, "Bad Gateway - Invalid response from upstream. Check upstream server status.");
            _statusCodes[503] = new StatusCodeInfo(503, true, false, "Service Unavailable - Server not ready. Retry after some time.");
            _statusCodes[504] = new StatusCodeInfo(504, true, false, "Gateway Timeout - No response in time. Retry or increase timeout.");
        }
    }

    /// <summary>
    /// Information about a specific HTTP status code.
    /// </summary>
    public class StatusCodeInfo
    {
        public StatusCodeInfo(int responseCode, bool isError, bool isThrottling, string description)
        {
            ResponseCode = responseCode;
            IsError = isError;
            IsThrottling = isThrottling;
            Description = description ?? string.Empty;
        }

        /// <summary>
        /// The HTTP status code.
        /// </summary>
        public int ResponseCode { get; }

        /// <summary>
        /// Indicates whether this status code represents an error condition.
        /// </summary>
        public bool IsError { get; }

        /// <summary>
        /// Indicates whether this status code represents a throttling condition.
        /// </summary>
        public bool IsThrottling { get; }

        /// <summary>
        /// Description of the status code, including resolution suggestions for errors and throttling.
        /// </summary>
        public string Description { get; }
    }
}
