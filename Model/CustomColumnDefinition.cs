using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HttpTraceAnalyser.Model
{
    public enum HeaderSource
    {
        Request,
        Response,
        RequestThenResponse,
    }

    public sealed class CustomColumnDefinition
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        public string Name { get; set; } = string.Empty;

        public HeaderSource Source { get; set; } = HeaderSource.Request;

        public string HeaderNamePattern { get; set; } = string.Empty;

        public string ValuePattern { get; set; } = string.Empty;

        internal string ExtractValue(
            IReadOnlyList<KeyValuePair<string, string>>? requestHeaders,
            IReadOnlyList<KeyValuePair<string, string>>? responseHeaders)
        {
            try
            {
                var headerPattern = new Regex(
                    HeaderNamePattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexTimeout);
                var headers = Source switch
                {
                    HeaderSource.Request => FindMatches(requestHeaders, headerPattern),
                    HeaderSource.Response => FindMatches(responseHeaders, headerPattern),
                    _ => FindMatches(requestHeaders, headerPattern)
                        .Concat(FindMatches(responseHeaders, headerPattern)),
                };

                var value = headers.FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(ValuePattern) || string.IsNullOrEmpty(value))
                    return value;

                var match = Regex.Match(
                    value,
                    ValuePattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexTimeout);
                if (!match.Success)
                    return string.Empty;
                return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                return string.Empty;
            }
        }

        private static IEnumerable<string> FindMatches(
            IReadOnlyList<KeyValuePair<string, string>>? headers,
            Regex headerPattern)
        {
            if (headers is null)
                yield break;

            foreach (var header in headers)
            {
                if (headerPattern.IsMatch(header.Key))
                    yield return header.Value;
            }
        }

        internal CustomColumnDefinition Clone() => new()
        {
            Name = Name,
            Source = Source,
            HeaderNamePattern = HeaderNamePattern,
            ValuePattern = ValuePattern,
        };
    }

    public static class CustomColumnSet
    {
        private const string DocumentType = "custom-columns";

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HttpTraceAnalyser",
            "custom-columns.json");

        internal static string CustomColumnDirectory => Path.Combine(
            Path.GetDirectoryName(SettingsPath)!, "CustomColumns");

        public static ObservableCollection<CustomColumnDefinition> Columns { get; } = LoadSettings();

        public static event EventHandler? ColumnsChanged;

        public static void Replace(IEnumerable<CustomColumnDefinition> columns)
        {
            Columns.Clear();
            foreach (var column in columns)
                Columns.Add(column.Clone());

            TraceColumnCatalog.Refresh();
            SaveSettings();
            ColumnsChanged?.Invoke(null, EventArgs.Empty);
        }

        internal static void Save(string path, IEnumerable<CustomColumnDefinition> columns)
        {
            var document = new CustomColumnDocument
            {
                DocumentType = DocumentType,
                Version = JsonConfigurationPersistence.CurrentVersion,
                Columns = columns.Select(column => column.Clone()).ToList(),
            };
            JsonConfigurationPersistence.Save(path, document);
        }

        internal static List<CustomColumnDefinition> Load(string path)
        {
            var document = JsonConfigurationPersistence.Load<CustomColumnDocument>(
                path, "custom column file");
            JsonConfigurationPersistence.ValidateDocument(
                document.DocumentType, DocumentType, document.Version, "custom column file");
            var columns = document.Columns
                ?? throw new InvalidDataException("The custom column file does not contain a columns collection.");
            foreach (var column in columns)
            {
                if (column is null)
                    throw new InvalidDataException("The custom column file contains an empty column definition.");
                JsonConfigurationPersistence.ValidateEnum(column.Source, nameof(column.Source));
            }
            return columns;
        }

        private static ObservableCollection<CustomColumnDefinition> LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var columns = Load(SettingsPath);
                    return new ObservableCollection<CustomColumnDefinition>(columns);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }

            return new ObservableCollection<CustomColumnDefinition>();
        }

        private static void SaveSettings()
        {
            try
            {
                Save(SettingsPath, Columns);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private sealed class CustomColumnDocument
        {
            public CustomColumnDocument() { }

            public required string DocumentType { get; set; }
            public required int Version { get; set; }
            public required List<CustomColumnDefinition>? Columns { get; set; }
        }
    }

    public static class TraceColumnCatalog
    {
        public static ObservableCollection<string> Names { get; } = new();

        static TraceColumnCatalog()
        {
            Refresh();
        }

        internal static bool IsReservedName(string name)
            => Enum.GetNames<FilterField>()
                    .Contains(name, StringComparer.OrdinalIgnoreCase)
                || HttpTraceFile.ExtendedFieldNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        internal static void Refresh()
        {
            Names.Clear();
            var names = Enum.GetNames<FilterField>()
                .Where(name => name != nameof(FilterField.Custom))
                .Concat(HttpTraceFile.ExtendedFieldNames)
                .Concat(CustomColumnSet.Columns.Select(column => column.Name));

            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    !Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    Names.Add(name);
                }
            }
        }
    }
}