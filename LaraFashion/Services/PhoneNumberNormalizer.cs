using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LaraFashion.Services;

public static partial class PhoneNumberNormalizer
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}])\+?[\d٠-٩۰-۹][\d٠-٩۰-۹\s\-()]{6,}[\d٠-٩۰-۹](?![\p{L}\p{N}])")]
    private static partial Regex PhoneLikeRegex();

    [GeneratedRegex(@"(?i)(?:اسمي|الاسم)\s*[:\-]?\s*[\p{L} ]{2,40}")]
    private static partial Regex LabelledNameRegex();

    [GeneratedRegex(@"(?i)(?:عنواني|العنوان)\s*[:\-]?\s*[^،,.\r\n]{2,100}")]
    private static partial Regex LabelledAddressRegex();

    [GeneratedRegex(@"(?i)\bORD[-\s]?\d{4,}\b")]
    private static partial Regex OrderNumberRegex();

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var digits = new StringBuilder();
        foreach (var character in value)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.DecimalDigitNumber)
                digits.Append(char.GetNumericValue(character).ToString(CultureInfo.InvariantCulture));
        }

        var normalized = digits.ToString();
        if (normalized.StartsWith("00", StringComparison.Ordinal))
            normalized = normalized[2..];

        if (normalized.Length is < 7 or > 15)
            return null;

        // Treat the common Palestinian/Israeli local 05... form as +9725...
        // without applying broad suffix matching that could merge distinct numbers.
        if (normalized.Length == 10 && normalized.StartsWith("05", StringComparison.Ordinal))
            normalized = "972" + normalized[1..];

        return normalized;
    }

    public static (string SanitizedText, string? LocalPhone) RedactSensitiveData(string text)
    {
        string? phone = null;
        var sanitized = PhoneLikeRegex().Replace(text, match =>
        {
            phone ??= Normalize(match.Value);
            return "[رقم هاتف]";
        });
        sanitized = LabelledNameRegex().Replace(sanitized, "[اسم]");
        sanitized = LabelledAddressRegex().Replace(sanitized, "[عنوان]");
        sanitized = OrderNumberRegex().Replace(sanitized, "[رقم طلب]");
        return (sanitized.Trim(), phone);
    }
}
