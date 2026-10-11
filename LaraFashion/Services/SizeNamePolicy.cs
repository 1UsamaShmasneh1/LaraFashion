using LaraFashion.Models;
using System.Text;

namespace LaraFashion.Services;

// Version 1 is also the database contract. Do not change it without a migration.
public static class SizeNamePolicy
{
    public const int MaximumLength = 128;
    public const string Spaces = "\t\n\v\f\r \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";
    public const string Invisible = "\u00ad\u061c\u200b\u200c\u200d\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2060\u2066\u2067\u2068\u2069\ufeff";
    public static string Key(string? label)
    {
        var key = new StringBuilder(); var space = false;
        foreach (var c in label ?? "")
        {
            if (Invisible.Contains(c)) continue;
            if (Spaces.Contains(c)) { space = key.Length > 0; continue; }
            if (space) key.Append(' ');
            key.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
            space = false;
        }
        return key.ToString();
    }
    public static bool IsValid(string? label) => label is { Length: > 0 and <= MaximumLength }
        && Key(label).Length > 0 && !label.Any(c => char.IsSurrogate(c) || (char.IsControl(c) && !Spaces.Contains(c)));

    public static void Validate(IReadOnlyList<ProductSize> sizes, Guid productId)
    {
        for (var i = 0; i < sizes.Count; i++)
        {
            if (!IsValid(sizes[i].SizeName) || sizes[i].Quantity < 0)
                throw new InvalidOperationException($"صف المقاس {i + 1}: أدخل اسماً صالحاً (حتى 128 محرفاً) وكمية صحيحة غير سالبة.");
            if (sizes[i].ProductId != Guid.Empty && sizes[i].ProductId != productId)
                throw new InvalidOperationException($"صف المقاس {i + 1} مرتبط بمنتج آخر. أعد فتح التعديل.");
        }
        var duplicate = sizes.Select((s, i) => new { Size = s, Row = i + 1 })
            .GroupBy(x => Key(x.Size.SizeName), StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"المقاس مكرر في الصفوف {string.Join("، ", duplicate.Select(x => x.Row))}: {string.Join(" / ", duplicate.Select(x => x.Size.SizeName))}. صحح الصفوف ثم احفظ.");
        if (sizes.Where(s => s.Id != Guid.Empty).GroupBy(s => s.Id).Any(g => g.Count() > 1))
            throw new InvalidOperationException("هوية صف المقاس مكررة. أعد فتح التعديل.");
    }
    public static bool IsSelectable(Product product, ProductSize size) => IsValid(size.SizeName) && size.Quantity > 0
        && product.Sizes.Count(s => Key(s.SizeName) == Key(size.SizeName)) == 1;
    public static string? Warning(Product product)
    {
        try { Validate(product.Sizes, product.Id); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    // Small CTE stages avoid deeply nested SQLite expressions. ASCII upper only matches Key.
    public static string SqlKey(string source)
    {
        var stages = new List<string> { $"s0(v) AS (SELECT {source})" }; var index = 0;
        var replacements = Spaces.Where(c => c != ' ').Select(c => (c, "' '"))
            .Concat(Invisible.Select(c => (c, "''"))).ToList();
        foreach (var chunk in replacements.Chunk(4))
        {
            var expression = "v";
            foreach (var (c, replacement) in chunk) expression = $"replace({expression},char({(int)c}),{replacement})";
            stages.Add($"s{index + 1}(v) AS (SELECT {expression} FROM s{index})"); index++;
        }
        for (var i = 0; i < 7; i++)
        { stages.Add($"s{index + 1}(v) AS (SELECT replace(v,'  ',' ') FROM s{index})"); index++; }
        return $"(WITH {string.Join(",", stages)} SELECT upper(trim(v)) FROM s{index})";
    }
    public static string SqlValid(string source) => $"length({source}) BETWEEN 1 AND 128 AND length({SqlKey(source)}) > 0 AND {source} NOT GLOB ('*[' || char(65536) || '-' || char(1114111) || ']*') AND "
        + string.Join(" AND ", Enumerable.Range(0, 160).Where(i => char.IsControl((char)i) && !Spaces.Contains((char)i))
            .Select(i => $"instr({source},char({i})) = 0"));
    public static string SqlGuidValid(string source) => $"length({source}) = 36 AND upper({source}) = {source} AND "
        + $"substr({source},9,1) = '-' AND substr({source},14,1) = '-' AND substr({source},19,1) = '-' AND substr({source},24,1) = '-' AND "
        + $"length(replace({source},'-','')) = 32 AND replace({source},'-','') NOT GLOB '*[^0-9A-F]*' AND {source} <> '00000000-0000-0000-0000-000000000000'";
}
