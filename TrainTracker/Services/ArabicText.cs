using System.Globalization;
using System.Text;

namespace TrainTracker.Api.Services;

public static class ArabicText
{
    /// <summary>
    /// توحيد الهمزات والياء والتاء المربوطة وشيل التشكيل والتطويل والمسافات الزيادة
    /// عشان أسماء المحطات تتطابق (الاسكندرية = الإسكندرية، ابو حمص = أبو حمص).
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
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
