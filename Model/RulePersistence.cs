using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace HttpTraceAnalyser.Model
{
    internal static class RulePersistence
    {
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
                Version = JsonConfigurationPersistence.CurrentVersion,
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
            JsonConfigurationPersistence.Save(path, document);
        }

        internal static List<HighlightRule> LoadHighlights(string path)
        {
            var document = JsonConfigurationPersistence.Load<HighlightRuleDocument>(path, "rule file");
            JsonConfigurationPersistence.ValidateDocument(
                document.DocumentType, HighlightDocumentType, document.Version, "highlights rule file");
            if (document.Rules is null)
                throw new InvalidDataException("The highlight rule file does not contain a rules collection.");

            return document.Rules.Select(rule => new HighlightRule
            {
                IsEnabled = rule.IsEnabled,
                Column = JsonConfigurationPersistence.ValidateEnum(rule.Column, nameof(rule.Column)),
                Operator = JsonConfigurationPersistence.ValidateEnum(rule.Operator, nameof(rule.Operator)),
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
                Version = JsonConfigurationPersistence.CurrentVersion,
                Rules = rules.Select(rule => new FilterRuleData
                {
                    Combinator = rule.Combinator,
                    Field = rule.Field,
                    Comparator = rule.Comparator,
                    Value = rule.Value,
                    CustomFieldName = rule.CustomFieldName,
                }).ToList(),
            };
            JsonConfigurationPersistence.Save(path, document);
        }

        internal static List<FilterRule> LoadFilters(string path)
        {
            var document = JsonConfigurationPersistence.Load<FilterRuleDocument>(path, "rule file");
            JsonConfigurationPersistence.ValidateDocument(
                document.DocumentType, FilterDocumentType, document.Version, "filters rule file");
            if (document.Rules is null)
                throw new InvalidDataException("The filter rule file does not contain a rules collection.");

            return document.Rules.Select(rule => new FilterRule
            {
                Combinator = JsonConfigurationPersistence.ValidateEnum(rule.Combinator, nameof(rule.Combinator)),
                Field = JsonConfigurationPersistence.ValidateEnum(rule.Field, nameof(rule.Field)),
                Comparator = JsonConfigurationPersistence.ValidateEnum(rule.Comparator, nameof(rule.Comparator)),
                Value = rule.Value ?? string.Empty,
                CustomFieldName = rule.CustomFieldName ?? string.Empty,
            }).ToList();
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