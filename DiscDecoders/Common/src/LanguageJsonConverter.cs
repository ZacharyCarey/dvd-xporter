using System.Text.Json;
using System.Text.Json.Serialization;
using Iso639;

namespace DiscDecoders.Common;

public class LanguageJsonConverter : JsonConverter<Language> {
    public static Language? TryParseLanguage(string? input) {
        if (string.IsNullOrWhiteSpace(input)) return null;
        Language? result = Language.Database
            .Where(lang =>
            {
                if (lang.Part1 != null && input.Equals(lang.Part1, StringComparison.OrdinalIgnoreCase)) return true;
                if (lang.Part2 != null && input.Equals(lang.Part2, StringComparison.OrdinalIgnoreCase)) return true;
                // Iso639 calls B codes obsolete, but MKV and older tools still write them ("fre", "ger")
#pragma warning disable CS0612
                if (lang.Part2B != null && input.Equals(lang.Part2B, StringComparison.OrdinalIgnoreCase)) return true;
#pragma warning restore CS0612
                if (lang.Part3 != null && input.Equals(lang.Part3, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            })
            .FirstOrDefault();

        if (result == null)
        {
            throw new JsonException($"Failed to parse language code: '{input}'");
        }

        if (result != null && result.Part3 == null)
        {
            throw new JsonException($"Found language '{result.Name}' but an ISO 639-3 code could not be found.");
        }

        return result;
    }

    public override Language? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        string? input = reader.GetString();
        return TryParseLanguage(input);
    }

    public override void Write(Utf8JsonWriter writer, Language value, JsonSerializerOptions options) {
        string? lang = value.Part3;
        if (lang == null) {
            throw new JsonException($"Language does not have a valid ISO 639-3 code. (The ISO 639-3 code={value.Part3})");
        } else
        {
            writer.WriteStringValue(lang);
        }
    }
}
