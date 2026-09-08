using System;
using System.Collections.Generic;
using System.Linq;

namespace HttpTraceAnalyser.Model
{
    /// <summary>A single loaded trace file, held in memory under a session id, with its own independent filter/highlight configuration.</summary>
    public sealed class TraceSession
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required HttpTraceFile Trace { get; init; }
        public required DateTimeOffset LoadedAt { get; init; }

        /// <summary>This session's own active filter rules, independent of every other loaded session.</summary>
        public FilterRuleCollection Filters { get; } = new();

        /// <summary>This session's own active highlight rules, independent of every other loaded session.</summary>
        public HighlightRuleCollection Highlights { get; } = new();
    }

    /// <summary>
    /// Process-wide registry of concurrently loaded trace files, keyed by a short session id.
    /// The UI (<see cref="MainWindow"/>) always displays exactly one session at a time (the
    /// "active" one); MCP tools can load additional sessions in the background, list them,
    /// switch which one is shown, close them, or query any of them directly without switching.
    /// </summary>
    public static class TraceSessionManager
    {
        private static readonly Dictionary<string, TraceSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
        private static int _nextId = 1;

        public static string? ActiveSessionId { get; private set; }

        /// <summary>Raised whenever the set of loaded sessions changes (added or removed).</summary>
        public static event EventHandler? SessionsChanged;

        /// <summary>Raised after <see cref="ActiveSessionId"/> changes, with the newly active session (or null if none).</summary>
        public static event EventHandler<TraceSession?>? ActiveSessionChanged;

        /// <summary>
        /// Registers a loaded trace under a new or caller-supplied session id. When
        /// <paramref name="requestedId"/> is null/empty, an id of the form "trace1", "trace2", ...
        /// is generated. Throws if <paramref name="requestedId"/> is already in use.
        /// </summary>
        public static TraceSession Add(HttpTraceFile trace, string? requestedId, string? label)
        {
            if (trace is null)
                throw new ArgumentNullException(nameof(trace));

            string id;
            if (!string.IsNullOrWhiteSpace(requestedId))
            {
                if (Sessions.ContainsKey(requestedId))
                    throw new InvalidOperationException($"Session id '{requestedId}' is already in use.");
                id = requestedId;
            }
            else
            {
                do
                {
                    id = $"trace{_nextId++}";
                }
                while (Sessions.ContainsKey(id));
            }

            var session = new TraceSession
            {
                Id = id,
                Label = string.IsNullOrWhiteSpace(label) ? System.IO.Path.GetFileName(trace.FilePath) : label,
                Trace = trace,
                LoadedAt = DateTimeOffset.Now,
            };
            Sessions[id] = session;
            SessionsChanged?.Invoke(null, EventArgs.Empty);
            return session;
        }

        /// <summary>Marks the given session id as the currently active/visible one and raises <see cref="ActiveSessionChanged"/>.</summary>
        public static void SetActive(string sessionId)
        {
            ActiveSessionId = sessionId;
            ActiveSessionChanged?.Invoke(null, Get(sessionId));
        }

        public static TraceSession? Get(string sessionId)
            => Sessions.TryGetValue(sessionId, out var session) ? session : null;

        public static TraceSession? GetActive()
            => ActiveSessionId is not null ? Get(ActiveSessionId) : null;

        public static IReadOnlyList<TraceSession> List()
            => Sessions.Values.OrderBy(s => s.LoadedAt).ToList();

        /// <summary>
        /// Removes a session from the registry. If it was active, clears <see cref="ActiveSessionId"/>
        /// (the caller is responsible for deciding what, if anything, to show instead, and should call
        /// <see cref="SetActive"/> afterward if a different session becomes active).
        /// </summary>
        public static bool Remove(string sessionId)
        {
            if (!Sessions.Remove(sessionId))
                return false;

            if (string.Equals(ActiveSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                ActiveSessionId = null;

            SessionsChanged?.Invoke(null, EventArgs.Empty);
            return true;
        }
    }
}
