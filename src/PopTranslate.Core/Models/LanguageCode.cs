using System.Text.Json;
using System.Text.Json.Serialization;

namespace PopTranslate.Core.Models;

/// <summary>
/// Normalized BCP-47 language code value object serialized as a JSON string.
/// </summary>
[JsonConverter(typeof(LanguageCodeJsonConverter))]
public readonly record struct LanguageCode
{
    public static readonly LanguageCode Auto = new("auto");
    public static readonly LanguageCode ChineseSimplified = new("zh-Hans");
    public static readonly LanguageCode ChineseTraditional = new("zh-Hant");
    public static readonly LanguageCode English = new("en");
    public static readonly LanguageCode Japanese = new("ja");
    public static readonly LanguageCode Korean = new("ko");
    public static readonly LanguageCode French = new("fr");
    public static readonly LanguageCode German = new("de");
    public static readonly LanguageCode Spanish = new("es");
    public static readonly LanguageCode Russian = new("ru");
    public static readonly LanguageCode Italian = new("it");
    public static readonly LanguageCode Portuguese = new("pt");
    public static readonly LanguageCode Arabic = new("ar");

    private static readonly Dictionary<string, (string DisplayName, string NativeName)> KnownMetadata =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = ("Auto Detect", "自动检测"),
            ["zh-Hans"] = ("Chinese (Simplified)", "简体中文"),
            ["zh-Hant"] = ("Chinese (Traditional)", "繁體中文"),
            ["en"] = ("English", "English"),
            ["ja"] = ("Japanese", "日本語"),
            ["ko"] = ("Korean", "한국어"),
            ["fr"] = ("French", "Français"),
            ["de"] = ("German", "Deutsch"),
            ["es"] = ("Spanish", "Español"),
            ["ru"] = ("Russian", "Русский"),
            ["it"] = ("Italian", "Italiano"),
            ["pt"] = ("Portuguese", "Português"),
            ["ar"] = ("Arabic", "العربية"),
        };

    public static readonly IReadOnlyList<LanguageCode> SelectableTargets =
    [
        ChineseSimplified,
        ChineseTraditional,
        English,
        Japanese,
        Korean,
        French,
        German,
        Spanish,
        Russian,
        Italian,
        Portuguese,
        Arabic,
    ];

    public static readonly IReadOnlyList<LanguageCode> SelectableSources =
    [
        Auto,
        ChineseSimplified,
        ChineseTraditional,
        English,
        Japanese,
        Korean,
        French,
        German,
        Spanish,
        Russian,
        Italian,
        Portuguese,
        Arabic,
    ];

    public LanguageCode(string value)
    {
        Value = Normalize(value);
    }

    public string Value { get; }

    public bool IsAuto => string.IsNullOrWhiteSpace(Value) || Value.Equals("auto", StringComparison.OrdinalIgnoreCase);

    public bool IsChinese =>
        Value.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
        Value.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
        Value.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public string DisplayName =>
        KnownMetadata.TryGetValue(Value, out var meta) ? meta.DisplayName : Value;

    public string NativeName =>
        KnownMetadata.TryGetValue(Value, out var meta) ? meta.NativeName : Value;

    public string Label =>
        KnownMetadata.TryGetValue(Value, out var meta)
            ? (meta.DisplayName == meta.NativeName ? meta.DisplayName : $"{meta.NativeName} ({meta.DisplayName})")
            : Value;

    public static LanguageCode Parse(string? code) => new(Normalize(code));

    public static LanguageCode FromBcp47(string? tag) => Parse(tag);

    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "auto";
        }

        var trimmed = code.Trim().Replace('_', '-');
        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        var lower = trimmed.ToLowerInvariant();
        return lower switch
        {
            "zh" or "zh-cn" or "zh-sg" or "zh-hans" or "zh-hans-cn" or "zh-hans-sg" or "zh-chs" => "zh-Hans",
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" or "zh-hant-tw" or "zh-hant-hk" or "zh-cht" => "zh-Hant",
            _ when lower.StartsWith("zh-hans", StringComparison.Ordinal) => "zh-Hans",
            _ when lower.StartsWith("zh-hant", StringComparison.Ordinal) => "zh-Hant",
            _ when lower.StartsWith("en-", StringComparison.Ordinal) || lower == "en" => "en",
            _ when lower.StartsWith("ja-", StringComparison.Ordinal) || lower == "ja" || lower == "jp" => "ja",
            _ when lower.StartsWith("ko-", StringComparison.Ordinal) || lower == "ko" || lower == "kr" => "ko",
            _ when lower.StartsWith("fr-", StringComparison.Ordinal) || lower == "fr" => "fr",
            _ when lower.StartsWith("de-", StringComparison.Ordinal) || lower == "de" => "de",
            _ when lower.StartsWith("es-", StringComparison.Ordinal) || lower == "es" => "es",
            _ when lower.StartsWith("ru-", StringComparison.Ordinal) || lower == "ru" => "ru",
            _ when lower.StartsWith("it-", StringComparison.Ordinal) || lower == "it" => "it",
            _ when lower.StartsWith("pt-", StringComparison.Ordinal) || lower == "pt" => "pt",
            _ when lower.StartsWith("ar-", StringComparison.Ordinal) || lower == "ar" => "ar",
            _ => trimmed,
        };
    }

    public bool Equals(LanguageCode other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? "auto");

    public override string ToString() => Value ?? "auto";
}

public sealed class LanguageCodeJsonConverter : JsonConverter<LanguageCode>
{
    public override LanguageCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return LanguageCode.Parse(reader.GetString());
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return LanguageCode.Auto;
        }

        throw new JsonException($"Expected string token for {nameof(LanguageCode)}, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, LanguageCode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value ?? "auto");
    }
}
