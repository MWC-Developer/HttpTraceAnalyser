using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace HttpTraceAnalyser.Model
{
    internal static class RulePersistence
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter() },
        };

        private const int CurrentVersion = 1;
        private const string HighlightDocumentType = "highlights";
        private const string FilterDocumentType = "filters";

        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HttpTraceAnalyser");

        internal static string HighlightDirectory => Path.Combine(SettingsDirectory, "HighlightRules");
        internal static string FilterDirectory => Path.Combine(SettingsDirectory, "FilterRules");

        internal static string HighlightDefaultPath => Path.Combine(
            HighlightDirectory, "default-highlights.json");

        internal static string FilterDefaultPath => Path.Combine(
            FilterDirectory, "default-filters.json");

        internal static void SaveHighlights(string path, IEnumerable<HighlightRule> rules)
        {
            var document = new HighlightRuleDocument
            {
                DocumentType = HighlightDocumentType,
                Version = CurrentVersion,
                Rules = rules.Select(rule => new HighlightRuleData
                {
                    IsEnabled = rule.IsEnabled,
                    Column = rule.Column,
                    Operator = rule.Operator,
                    Value = rule.Value,
                    CustomFieldName = rule.CustomFieldName,
                    BackgroundColor = FormatColor(rule.BackgroundColor),
                    ForegroundColor = rule.ForegroundColor is Color color ? FormatColor(color) : null,
                }).ToList(),
            };
            Save(path, document);
        }

        internal static List<HighlightRule> LoadHighlights(string path)
        {
            var document = Load<HighlightRuleDocument>(path);
            ValidateDocument(document.DocumentType, HighlightDocumentType, document.Version);
            if (document.Rules is null)
                throw new InvalidDataException("The highlight rule file does not contain a rules collection.");

            return document.Rules.Select(rule => new HighlightRule
            {
                IsEnabled = rule.IsEnabled,
                Column = ValidateEnum(rule.Column, nameof(rule.Column)),
                Operator = ValidateEnum(rule.Operator, nameof(rule.Operator)),
                Value = rule.Value ?? string.Empty,
                CustomFieldName = rule.CustomFieldName ?? string.Empty,
                BackgroundColor = ParseColor(rule.BackgroundColor),
                ForegroundColor = string.IsNullOrWhiteSpace(rule.ForegroundColor)
                    ? null
                    : ParseColor(rule.ForegroundColor),
            }).ToList();
        }

        internal static void SaveFilters(string path, IEnumerable<FilterRule> rules)
        {
            var document = new FilterRuleDocument
            {
                DocumentType = FilterDocumentType,
                Version = CurrentVersion,
                Rules = rules.Select(rule => new FilterRuleData
                {
                    Combinator = rule.Combinator,
                    Field = rule.Field,
                    Comparator = rule.Comparator,
                    Value = rule.Value,
                    CustomFieldName = rule.CustomFieldName,
                }).ToList(),
            };
            Save(path, document);
        }

        internal static List<FilterRule> LoadFilters(string path)
        {
            var document = Load<FilterRuleDocument>(path);
            ValidateDocument(document.DocumentType, FilterDocumentType, document.Version);
            if (document.Rules is null)
                throw new InvalidDataException("The filter rule file does not contain a rules collection.");

            return document.Rules.Select(rule => new FilterRule
            {
                Combinator = ValidateEnum(rule.Combinator, nameof(rule.Combinator)),
                Field = ValidateEnum(rule.Field, nameof(rule.Field)),
                Comparator = ValidateEnum(rule.Comparator, nameof(rule.Comparator)),
                Value = rule.Value ?? string.Empty,
                CustomFieldName = rule.CustomFieldName ?? string.Empty,
            }).ToList();
        }

        private static void Save<T>(string path, T document)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, document, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        private static T Load<T>(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<T>(stream, JsonOptions)
                    ?? throw new InvalidDataException("The rule file is empty or invalid.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The rule file contains invalid JSON or an unsupported schema.", ex);
            }
        }

        private static string FormatColor(Color color)
            => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        private static Color ParseColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                ColorConverter.ConvertFromString(value) is not Color color)
            {
                throw new InvalidDataException($"Invalid colour value '{value}'.");
            }
            return color;
        }

        private static void ValidateDocument(string? actualType, string expectedType, int version)
        {
            if (!string.Equals(actualType, expectedType, StringComparison.Ordinal))
                throw new InvalidDataException($"Expected a {expectedType} rule file, but found '{actualType ?? "an unspecified type"}'.");
            if (version != CurrentVersion)
                throw new InvalidDataException($"Rule file version {version} is not supported.");
        }

        private static T ValidateEnum<T>(T value, string propertyName) where T : struct, Enum
        {
            if (!Enum.IsDefined(value))
                throw new InvalidDataException($"Invalid {propertyName} value '{value}'.");
            return value;
        }

        private sealed class HighlightRuleDocument
        {
            public HighlightRuleDocument() { }

            public required string DocumentType { get; set; }
            public required int Version { get; set; }
            public required List<HighlightRuleData>? Rules { get; set; }
        }

        private sealed class HighlightRuleData
        {
            public HighlightRuleData() { }

            public bool IsEnabled { get; set; } = true;
            public HighlightColumn Column { get; set; }
            public HighlightOperator Operator { get; set; }
            public string? Value { get; set; }
            public string? CustomFieldName { get; set; }
            public string? BackgroundColor { get; set; }
            public string? ForegroundColor { get; set; }
        }

        private sealed class FilterRuleDocument
        {
            public FilterRuleDocument() { }

            public required string DocumentType { get; set; }
            public required int Version { get; set; }
            public required List<FilterRuleData>? Rules { get; set; }
        }

        private sealed class FilterRuleData
        {
            public FilterRuleData() { }

            public FilterCombinator Combinator { get; set; }
            public FilterField Field { get; set; }
            public FilterComparator Comparator { get; set; }
            public string? Value { get; set; }
            public string? CustomFieldName { get; set; }
        }
    }
}