using System;
using System.Collections.Generic;

namespace HttpTraceAnalyser.Model.Extensibility
{
    /// <summary>
    /// Describes an additional ("extended") field that a plugin contributes to the trace
    /// grid. Extended fields behave like the built-in ContentType/ClientRequestId/SoapMethod
    /// fields: they add a column to the underlying <see cref="System.Data.DataTable"/> and are
    /// automatically available for filtering (<see cref="FilterRule"/>) and highlighting
    /// (<see cref="HighlightRule"/>) via their <see cref="Name"/>.
    /// </summary>
    public sealed class ExtendedFieldDefinition
    {
        /// <summary>
        /// Column/field name. Must be unique across all built-in and plugin-contributed
        /// fields and must be a valid identifier (used as a DataTable column name and
        /// referenced in DataView.RowFilter expressions).
        /// </summary>
        public string Name { get; }

        /// <summary>Header text shown in the grid column chooser and column header.</summary>
        public string DisplayName { get; }

        /// <summary>CLR type backing the DataTable column. Typically <see cref="string"/>.</summary>
        public Type FieldType { get; }

        /// <summary>
        /// Extracts the value for this field from a request/response pair. Invoked once per
        /// row as it is added to the trace. Return null to leave the cell as DBNull.
        /// </summary>
        public Func<HttpRequest, HttpResponse?, object?> Extractor { get; }

        public ExtendedFieldDefinition(
            string name,
            string displayName,
            Type fieldType,
            Func<HttpRequest, HttpResponse?, object?> extractor)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name is required.", nameof(name));
            Name = name;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
            FieldType = fieldType ?? throw new ArgumentNullException(nameof(fieldType));
            Extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        }
    }

    /// <summary>
    /// Contract implemented by an assembly that wants to add support for loading an
    /// additional trace file format (e.g. an internal ETL variant with custom ETW
    /// providers) without modifying HttpTraceAnalyser itself.
    ///
    /// Plugin assemblies are discovered at startup from the "Plugins" folder next to the
    /// application executable (see <see cref="PluginManager"/>). A plugin DLL must reference
    /// HttpTraceAnalyser.exe and implement this interface on at least one public,
    /// parameterless-constructible type.
    /// </summary>
    public interface ITraceParserPlugin
    {
        /// <summary>Human-readable plugin name, used for diagnostics only.</summary>
        string Name { get; }

        /// <summary>
        /// File extensions this plugin can load, including the leading dot
        /// (e.g. ".etl"). Case-insensitive.
        /// </summary>
        IReadOnlyList<string> SupportedExtensions { get; }

        /// <summary>
        /// Returns true if this plugin should handle the given file. Called only for files
        /// whose extension matches <see cref="SupportedExtensions"/>, or for ambiguous
        /// extensions (e.g. ".log") when no dedicated loader claimed the extension - use this
        /// to sniff file contents in that case.
        /// </summary>
        bool CanLoad(string filePath);

        /// <summary>Loads the trace file and returns the populated <see cref="HttpTraceFile"/>.</summary>
        HttpTraceFile Load(string filePath);

        /// <summary>
        /// Additional fields this plugin contributes to the trace grid (similar to the
        /// built-in MAPI-derived fields). Return an empty list if none.
        /// </summary>
        IReadOnlyList<ExtendedFieldDefinition> ExtendedFields { get; }
    }
}
