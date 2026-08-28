using System;
using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Manages theme-aware syntax highlighting for AvalonEdit.
    /// Loads custom dark mode highlighting definitions when dark theme is active.
    /// </summary>
    public static class SyntaxHighlightingManager
    {
        private static IHighlightingDefinition? _darkJsonHighlighting;
        private static IHighlightingDefinition? _darkXmlHighlighting;
        private static IHighlightingDefinition? _darkHtmlHighlighting;
        private static IHighlightingDefinition? _darkJavaScriptHighlighting;
        private static bool _darkHighlightingsLoaded = false;

        /// <summary>
        /// Gets the appropriate syntax highlighting definition for the given format and current theme.
        /// </summary>
        public static IHighlightingDefinition? GetHighlighting(string format)
        {
            if (ThemeManager.Current == AppTheme.Dark)
            {
                // Ensure dark highlighting definitions are loaded
                if (!_darkHighlightingsLoaded)
                {
                    LoadDarkHighlightings();
                }

                return format.ToLowerInvariant() switch
                {
                    "json" => _darkJsonHighlighting,
                    "xml" => _darkXmlHighlighting,
                    "html" => _darkHtmlHighlighting,
                    "javascript" or "js" => _darkJavaScriptHighlighting,
                    _ => null
                };
            }
            else
            {
                // Use built-in light mode highlighting
                return format.ToLowerInvariant() switch
                {
                    "json" => HighlightingManager.Instance.GetDefinitionByExtension(".json")
                              ?? HighlightingManager.Instance.GetDefinition("Json"),
                    "xml" => HighlightingManager.Instance.GetDefinitionByExtension(".xml")
                             ?? HighlightingManager.Instance.GetDefinition("XML"),
                    "html" => HighlightingManager.Instance.GetDefinitionByExtension(".html")
                              ?? HighlightingManager.Instance.GetDefinitionByExtension(".htm")
                              ?? HighlightingManager.Instance.GetDefinition("HTML"),
                    "javascript" or "js" => HighlightingManager.Instance.GetDefinitionByExtension(".js")
                                            ?? HighlightingManager.Instance.GetDefinition("JavaScript"),
                    _ => null
                };
            }
        }

        /// <summary>
        /// Loads custom dark theme highlighting definitions from embedded resources.
        /// </summary>
        private static void LoadDarkHighlightings()
        {
            try
            {
                _darkJsonHighlighting = LoadHighlightingFromFile("Highlighting/Dark-JSON.xshd");
                _darkXmlHighlighting = LoadHighlightingFromFile("Highlighting/Dark-XML.xshd");
                _darkHtmlHighlighting = LoadHighlightingFromFile("Highlighting/Dark-HTML.xshd");
                _darkJavaScriptHighlighting = LoadHighlightingFromFile("Highlighting/Dark-JavaScript.xshd");
                _darkHighlightingsLoaded = true;
            }
            catch (Exception ex)
            {
                // Log error but don't crash - fall back to built-in highlighting
                System.Diagnostics.Debug.WriteLine($"Failed to load dark theme highlighting: {ex.Message}");
                _darkHighlightingsLoaded = true; // Prevent retry
            }
        }

        /// <summary>
        /// Loads a highlighting definition from a file.
        /// </summary>
        private static IHighlightingDefinition? LoadHighlightingFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    System.Diagnostics.Debug.WriteLine($"Highlighting file not found: {filePath}");
                    return null;
                }

                using var reader = new XmlTextReader(filePath);
                var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                return definition;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading highlighting from {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resets the loaded highlighting definitions. Call this when the theme changes.
        /// </summary>
        public static void ResetHighlightings()
        {
            _darkHighlightingsLoaded = false;
            _darkJsonHighlighting = null;
            _darkXmlHighlighting = null;
            _darkHtmlHighlighting = null;
            _darkJavaScriptHighlighting = null;
        }
    }
}
