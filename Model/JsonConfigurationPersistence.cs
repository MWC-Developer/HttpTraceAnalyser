using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HttpTraceAnalyser.Model
{
    internal static class JsonConfigurationPersistence
    {
        internal const int CurrentVersion = 1;

        internal static JsonSerializerOptions Options { get; } = new()
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter() },
        };

        internal static void Save<T>(string path, T document)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, document, Options);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        internal static T Load<T>(string path, string description)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<T>(stream, Options)
                    ?? throw new InvalidDataException($"The {description} is empty or invalid.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The {description} contains invalid JSON or an unsupported schema.", ex);
            }
        }

        internal static void ValidateDocument(
            string? actualType,
            string expectedType,
            int version,
            string description)
        {
            if (!string.Equals(actualType, expectedType, StringComparison.Ordinal))
                throw new InvalidDataException($"Expected a {description}, but found '{actualType ?? "an unspecified type"}'.");
            if (version != CurrentVersion)
                throw new InvalidDataException($"{description} version {version} is not supported.");
        }

        internal static T ValidateEnum<T>(T value, string propertyName) where T : struct, Enum
        {
            if (!Enum.IsDefined(value))
                throw new InvalidDataException($"Invalid {propertyName} value '{value}'.");
            return value;
        }
    }
}