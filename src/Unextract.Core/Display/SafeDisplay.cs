using System.Globalization;
using System.Text;

namespace Unextract.Core.Display;

// 名前を端末に安全に表示するためのエスケープ (SPEC §10、テスト O04)。
// 制御文字、書式文字 (双方向制御など)、行・段落区切り、孤立サロゲート、U+FFFD を \u{XXXX} で表す。
// 表記を一意にするため、\ と " も \\ と \" にする。
public static class SafeDisplay
{
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string? replacement = null;

            if (c == '\\')
            {
                replacement = "\\\\";
            }
            else if (c == '"')
            {
                replacement = "\\\"";
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            else if (NeedsEscape(c))
            {
                replacement = $"\\u{{{(int)c:X4}}}";
            }

            if (replacement is null)
            {
                builder?.Append(c);
                continue;
            }

            builder ??= new StringBuilder(text, 0, i, text.Length + 16);
            builder.Append(replacement);
        }

        return builder?.ToString() ?? text;
    }

    private static bool NeedsEscape(char c)
    {
        if (char.IsSurrogate(c) || c == '\uFFFD')
        {
            return true;
        }

        return char.GetUnicodeCategory(c) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }
}
