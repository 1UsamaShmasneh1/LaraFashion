using System.Text.RegularExpressions;

namespace LaraFashion.Services;

public static class OrderNumberPolicy
{
    public static string Normalize(string number)
    {
        var normalized = (number ?? string.Empty).Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalized, "^[A-Z0-9][A-Z0-9-]{0,63}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("رقم الطلب يجب أن يتكون من 1 إلى 64 حرفاً إنجليزياً أو رقماً أو شرطة، ويبدأ بحرف أو رقم.");
        return normalized;
    }
}
