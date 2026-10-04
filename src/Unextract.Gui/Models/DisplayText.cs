using System.Globalization;
using System.Text;

namespace Unextract.Gui.Models;

// Display only. Input paths and, later, CLI entry names are kept separately.
internal static class DisplayText
{
    public static string Escape(string value)
    {
        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var rune = new Rune(c, value[i + 1]);
                if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
                    text.Append($"\\u{{{rune.Value:X4}}}");
                else text.Append(c).Append(value[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Control
                or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append($"\\u{{{(int)c:X4}}}");
            else text.Append(c);
        }
        return text.ToString();
    }
}
