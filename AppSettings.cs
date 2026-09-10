using System;
using System.IO;
using HttpTraceAnalyser.Model;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Persisted application-level preferences (appearance, layout, MCP bridge pipe name). Whether
    /// the MCP bridge is currently enabled is intentionally excluded: it always starts stopped and
    /// must be explicitly re-enabled each session.
    /// </summary>
    public static class AppSettings
    {
        private const string DocumentType = "app-settings";

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HttpTraceAnalyser",
            "app-settings.json");

        public static ThemePreference ThemePreference { get; set; } = ThemePreference.Auto;

        /// <summary>The theme to actually apply: the system theme when <see cref="ThemePreference"/> is Auto.</summary>
        public static AppTheme EffectiveTheme => ThemePreference switch
        {
            ThemePreference.Light => AppTheme.Light,
            ThemePreference.Dark => AppTheme.Dark,
            _ => ThemeManager.GetSystemTheme(),
        };

        public static bool UseSplitView { get; set; }

        /// <summary>
        /// Optional suffix distinguishing this instance's MCP named-pipe bridge from other running
        /// copies of the app (e.g. "1", "2"). Null/empty selects the default, well-known pipe
        /// name. Persisted under the legacy "McpPort" field name for settings-file compatibility.
        /// </summary>
        public static string? McpPipeNameSuffix { get; set; }

        static AppSettings() => Load();

        private static void Load()
        {
            if (!File.Exists(SettingsPath))
                return;

            try
            {
                var document = JsonConfigurationPersistence.Load<AppSettingsDocument>(SettingsPath, "app settings file");
                JsonConfigurationPersistence.ValidateDocument(
                    document.DocumentType, DocumentType, document.Version, "app settings file");

                ThemePreference = JsonConfigurationPersistence.ValidateEnum(
                    document.ThemePreference, nameof(document.ThemePreference));
                UseSplitView = document.UseSplitView;
                McpPipeNameSuffix = string.IsNullOrWhiteSpace(document.McpPipeNameSuffix) ? null : document.McpPipeNameSuffix;
            }
            catch (InvalidDataException)
            {
                // A corrupt settings file must not prevent application startup.
            }
        }

        public static void Save()
        {
            var document = new AppSettingsDocument
            {
                DocumentType = DocumentType,
                Version = JsonConfigurationPersistence.CurrentVersion,
                ThemePreference = ThemePreference,
                UseSplitView = UseSplitView,
                McpPipeNameSuffix = McpPipeNameSuffix,
            };

            try
            {
                JsonConfigurationPersistence.Save(SettingsPath, document);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private sealed class AppSettingsDocument
        {
            public AppSettingsDocument() { }

            public required string DocumentType { get; set; }
            public required int Version { get; set; }
            public ThemePreference ThemePreference { get; set; }
            public bool UseSplitView { get; set; }
            public string? McpPipeNameSuffix { get; set; }
        }
    }
}
