using System.Globalization;
using System.Text;

namespace TrainTracker.Api.Services;

public static class ArabicText
{
    // بادئات بتتشال من اسم المحطة قبل المقارنة (بعد التوحيد: ة→ه). الأطول الأول.
    private static readonly string[] Prefixes =
    {
        "محطه قطار ", "محطه سكه حديد ", "محطه السكه الحديد ", "محطه ", "قطار "
    };

    /// <summary>
    /// توحيد الهمزات والياء والتاء المربوطة وشيل التشكيل والتطويل والمسافات الزيادة،
    /// وشيل بادئة "محطة قطار" عشان أسماء المحطات تتطابق
    /// (الاسكندرية = الإسكندرية، ابو حمص = أبو حمص، "محطة قطار طنطا" = طنطا).
    /// </summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            switch (ch)
            {
                case 'أ': case 'إ': case 'آ': sb.Append('ا'); break;
                case 'ى': sb.Append('ي'); break;
                case 'ة': sb.Append('ه'); break;
                case 'ـ': break; // تطويل
                default:
                    if (char.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) break; // تشكيل
                    sb.Append(char.ToLowerInvariant(ch));
                    break;
            }
        }

        var result = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        foreach (var p in Prefixes)
        {
            if (result.StartsWith(p, StringComparison.Ordinal) && result.Length > p.Length)
            {
                result = result[p.Length..];
                break;
            }
        }
        return result;
    }
}