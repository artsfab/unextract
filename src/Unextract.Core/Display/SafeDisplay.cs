using System.Globalization;
using System.Text;

namespace Unextract.Core.Display;

// 名前を端末に安全に表示するためのエスケープ (docs/spec/cli.md#output、テスト O04)。
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
                var scalar = new Rune(c, text[i + 1]);
                if (Rune.GetUnicodeCategory(scalar) == UnicodeCategory.Format)
                {
                    builder ??= new StringBuilder(text, 0, i, text.Length + 16);
                    builder.Append($"\\u{{{scalar.Value:X4}}}");
                }
                else
                {
                    builder?.Append(c).Append(text[i + 1]);
                }
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

    // 結果行の Entry と Target の表示用エスケープ (docs/spec/cli.md#result-lines、docs/spec/cli.md#result-lines)。端末上で危険・誤認しやすい文字 (制御文字 (C0、DEL、
    // C1)、書式文字 (双方向制御などの Cf)、行・段落区切り、孤立サロゲート) だけを \u{XXXX} で表す。\ と " は変換しない
    // (表示された Entry をそのまま --entries に書けるようにするため。docs/RATIONALE.md#entries-display)。escaped はエスケープした文字があったか。
    public static string EscapeForList(string text, out bool escaped)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var scalar = new Rune(c, text[i + 1]);
                if (Rune.GetUnicodeCategory(scalar) == UnicodeCategory.Format)
                {
                    builder ??= new StringBuilder(text, 0, i, text.Length + 16);
                    builder.Append($"\\u{{{scalar.Value:X4}}}");
                }
                else
                {
                    builder?.Append(c).Append(text[i + 1]);
                }
                i++;
                continue;
            }

            if (!NeedsListEscape(c))
            {
                builder?.Append(c);
                continue;
            }

            builder ??= new StringBuilder(text, 0, i, text.Length + 16);
            builder.Append($"\\u{{{(int)c:X4}}}");
        }

        escaped = builder is not null;
        return builder?.ToString() ?? text;
    }

    private static bool NeedsListEscape(char c) =>
        char.IsSurrogate(c)
        || char.GetUnicodeCategory(c) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;

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
