using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LaraFashion.Services;

public static partial class SizeLabelParser
{
    private static readonly IReadOnlyDictionary<string, int> ArabicAgeWords =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["واحد"] = 1, ["واحدة"] = 1,
            ["اثنان"] = 2, ["اثنين"] = 2, ["سنتان"] = 2, ["سنتين"] = 2,
            ["ثلاث"] = 3, ["ثلاثة"] = 3,
            ["أربع"] = 4, ["اربعة"] = 4, ["أربعة"] = 4,
            ["خمس"] = 5, ["خمسة"] = 5,
            ["ست"] = 6, ["ستة"] = 6,
            ["سبع"] = 7, ["سبعة"] = 7,
            ["ثمان"] = 8, ["ثماني"] = 8, ["ثمانية"] = 8,
            ["تسع"] = 9, ["تسعة"] = 9,
            ["عشر"] = 10, ["عشرة"] = 10,
            ["إحدى عشر"] = 11, ["احدى عشر"] = 11, ["أحد عشر"] = 11, ["احد عشر"] = 11,
            ["اثنتا عشر"] = 12, ["اثنا عشر"] = 12, ["اثني عشر"] = 12
        };

    private static readonly IReadOnlyDictionary<string, int> ClothingOrder =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["XXS"] = 0, ["XS"] = 1, ["S"] = 2, ["M"] = 3,
            ["L"] = 4, ["XL"] = 5, ["XXL"] = 6, ["2XL"] = 6,
            ["XXXL"] = 7, ["3XL"] = 7
        };

    public static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                >= '\u0660' and <= '\u0669' => (char)('0' + character - '\u0660'),
                >= '\u06F0' and <= '\u06F9' => (char)('0' + character - '\u06F0'),
                _ => character
            });
        }

        return builder.ToString();
    }

    public static int? ExtractAgeYears(string? value)
    {
        var text = NormalizeDigits(value).Trim();
        if (text.Length == 0)
            return null;

        var match = AgeContextNumberRegex().Match(text);
        if (!match.Success)
            match = NumberWithYearSuffixRegex().Match(text);

        if (match.Success && int.TryParse(match.Groups["age"].Value, out var numericAge))
            return IsReasonableAge(numericAge) ? numericAge : null;

        foreach (var pair in ArabicAgeWords.OrderByDescending(x => x.Key.Length))
        {
            var escapedWord = Regex.Escape(pair.Key).Replace("\\ ", "\\s+");
            if (Regex.IsMatch(text,
                    $@"(?:(?:جيل|ل?عمر(?:ه|ها)?|بعمر)\s+{escapedWord}|{escapedWord}\s+(?:سنة|سنوات|سنين|عام|أعوام))",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return pair.Value;
        }

        return null;
    }

    public static string? ExtractExplicitSize(string? value)
    {
        var text = NormalizeDigits(value).Trim();
        if (text.Length == 0 || ExtractAgeYears(text) is not null)
            return null;

        var standalone = NormalizeSize(text);
        if (standalone is not null && StandaloneSizeRegex().IsMatch(standalone))
            return standalone;

        var match = ExplicitSizeRegex().Match(text);
        if (!match.Success)
            return null;

        return NormalizeSize(match.Groups["size"].Value);
    }

    public static string? NormalizeSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var size = Regex.Replace(NormalizeDigits(value).Trim(), @"\s+", " ").ToUpperInvariant();
        return size.Length <= 30 && Regex.IsMatch(size, @"^[\p{L}\p{N}][\p{L}\p{N}\s./-]*$", RegexOptions.CultureInvariant)
            ? size
            : null;
    }

    public static bool IsAvailableSizesQuestion(string? value)
    {
        var text = NormalizeDigits(value);
        return AvailableSizesRegex().IsMatch(text);
    }

    public static IReadOnlyList<string> FindAgeMatches(int ageYears, IEnumerable<string> availableLabels)
    {
        if (!IsReasonableAge(ageYears))
            return [];

        return availableLabels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Where(label => TryGetAgeBounds(label, out var minimum, out var maximum) &&
                            ageYears >= minimum && ageYears <= maximum)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, Comparer<string>.Create(CompareAvailableLabels))
            .ToList();
    }

    public static bool TryGetAgeBounds(string? label, out int minimum, out int maximum)
    {
        minimum = 0;
        maximum = 0;
        var text = NormalizeDigits(label).Trim();
        var range = AgeRangeLabelRegex().Match(text);
        if (range.Success &&
            int.TryParse(range.Groups["minimum"].Value, out minimum) &&
            int.TryParse(range.Groups["maximum"].Value, out maximum))
        {
            if (minimum > maximum)
                (minimum, maximum) = (maximum, minimum);
            return IsReasonableAge(minimum) && IsReasonableAge(maximum);
        }

        var exact = ExactAgeLabelRegex().Match(text);
        if (!exact.Success || !int.TryParse(exact.Groups["age"].Value, out minimum) || !IsReasonableAge(minimum))
            return false;

        maximum = minimum;
        return true;
    }

    public static string NormalizeLabelKey(string? value) =>
        Regex.Replace(NormalizeDigits(value).Trim().ToUpperInvariant(), @"\s+", string.Empty);

    public static string NormalizeAvailableLabelForDisplay(string value)
    {
        var trimmed = value.Trim();
        var key = NormalizeLabelKey(trimmed);
        return ClothingOrder.ContainsKey(key) || Regex.IsMatch(key, @"^\d{1,3}Y$", RegexOptions.CultureInvariant)
            ? key
            : trimmed;
    }

    public static int CompareAvailableLabels(string? left, string? right)
    {
        left ??= string.Empty;
        right ??= string.Empty;
        var leftHasAge = TryGetAgeBounds(left, out var leftMin, out var leftMax);
        var rightHasAge = TryGetAgeBounds(right, out var rightMin, out var rightMax);
        if (leftHasAge != rightHasAge)
            return leftHasAge ? -1 : 1;
        if (leftHasAge)
        {
            var result = leftMin.CompareTo(rightMin);
            return result != 0 ? result : leftMax.CompareTo(rightMax);
        }

        var leftKey = NormalizeLabelKey(left);
        var rightKey = NormalizeLabelKey(right);
        var leftKnown = ClothingOrder.TryGetValue(leftKey, out var leftOrder);
        var rightKnown = ClothingOrder.TryGetValue(rightKey, out var rightOrder);
        if (leftKnown != rightKnown)
            return leftKnown ? -1 : 1;
        if (leftKnown)
            return leftOrder.CompareTo(rightOrder);

        if (decimal.TryParse(leftKey, NumberStyles.Number, CultureInfo.InvariantCulture, out var leftNumber) &&
            decimal.TryParse(rightKey, NumberStyles.Number, CultureInfo.InvariantCulture, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);

        return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
    }

    private static bool IsReasonableAge(int age) => age is >= 0 and <= 18;

    [GeneratedRegex(@"(?:جيل|ل?عمر(?:ه|ها)?|بعمر)\s*(?<age>\d{1,2})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgeContextNumberRegex();

    [GeneratedRegex(@"(?<!\d)(?<age>\d{1,2})\s*(?:سنة|سنوات|سنين|عام|أعوام|years?|yrs?)(?![\p{L}\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberWithYearSuffixRegex();

    [GeneratedRegex(@"(?:مقاس|قياس|عندكم|بدي|أريد|اريد|اعرض(?:لي)?|أبحث|ابحث)\s*(?:مقاس|قياس)?\s*(?<size>(?:XXXS|XXS|XS|S|M|L|XL|XXL|XXXL|[23]XL|\d{1,3}(?:\s*[Yy])?))(?![\p{L}\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitSizeRegex();

    [GeneratedRegex(@"^(?:XXXS|XXS|XS|S|M|L|XL|XXL|XXXL|[23]XL|\d{1,3}(?:\s*Y)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneSizeRegex();

    [GeneratedRegex(@"(?=.*(?:مقاسات|قياسات))(?=.*(?:متوفر(?:ة|ه)?|موجود(?:ة|ه)?|عندكم|شو|ما\s+هي|ايش|إيش|ما\s+عندكم)).*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AvailableSizesRegex();

    [GeneratedRegex(@"^\s*(?<minimum>\d{1,2})\s*(?:-|–|—|/|إلى|الى|TO)\s*(?<maximum>\d{1,2})\s*(?:(?:Y|YR|YRS|YEAR|YEARS)|(?:سنة|سنوات|سنين|عام|أعوام))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgeRangeLabelRegex();

    [GeneratedRegex(@"^\s*(?<age>\d{1,2})\s*(?:(?:Y|YR|YRS|YEAR|YEARS)|(?:سنة|سنوات|سنين|عام|أعوام))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExactAgeLabelRegex();
}
