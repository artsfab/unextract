using Unextract.Core.Results;

namespace Unextract.Core.Zip;

internal readonly record struct ParsedEntryPath(bool IsDirectory, string[] Components, FatalKind? Error)
{
    public static ParsedEntryPath Fail(FatalKind kind) => new(false, [], kind);
}

// エントリ名のパス検査 (docs/spec/zip.md#paths)。/ と \ を区切りとし、正規化前の名前で判定する。
internal static class EntryPath
{
    private static readonly char[] Separators = ['/', '\\'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",

        // 上付き数字 (U+00B9、U+00B2、U+00B3)。
        "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
        "CONIN$", "CONOUT$",
    };

    public static ParsedEntryPath Parse(string name)
    {
        if (name.Length == 0)
        {
            return ParsedEntryPath.Fail(FatalKind.EmptyComponent);
        }

        // 先頭の区切りは絶対パス、UNC (\\server)、デバイスパス (\\?\、\\.\) を含む。
        if (IsSeparator(name[0]))
        {
            return ParsedEntryPath.Fail(FatalKind.RootedPath);
        }

        // ディレクトリエントリの末尾区切りは1個だけ許す。2個目以降は空成分になる。
        var isDirectory = IsSeparator(name[^1]);
        var body = isDirectory ? name[..^1] : name;

        if (body.Length >= 2 && body[1] == ':' && char.IsAsciiLetter(body[0]))
        {
            return ParsedEntryPath.Fail(FatalKind.DriveSpecifier);
        }

        foreach (var c in body)
        {
            if (c <= '\u001F' || c == '\u007F')
            {
                return ParsedEntryPath.Fail(FatalKind.ControlCharacter);
            }

            if (c is '<' or '>' or '"' or '|' or '?' or '*')
            {
                return ParsedEntryPath.Fail(FatalKind.InvalidCharacter);
            }

            if (c == ':')
            {
                return ParsedEntryPath.Fail(FatalKind.Colon);
            }
        }

        // 区切りを含まない名前は文字列を複製しない。
        var components = body.IndexOfAny(Separators) < 0 ? [body] : body.Split(Separators);
        foreach (var component in components)
        {
            if (CheckComponent(component) is { } error)
            {
                return ParsedEntryPath.Fail(error);
            }
        }

        return new ParsedEntryPath(isDirectory, components, null);
    }

    private static FatalKind? CheckComponent(string component)
    {
        if (component.Length == 0)
        {
            return FatalKind.EmptyComponent;
        }

        if (component == ".")
        {
            return FatalKind.DotComponent;
        }

        if (component == "..")
        {
            return FatalKind.DotDotComponent;
        }

        if (component[^1] is '.' or ' ')
        {
            return FatalKind.TrailingDotOrSpace;
        }

        // 拡張子付き (CON.txt) と、拡張子の前の空白 (CON .txt) も予約名として扱う。
        var dot = component.IndexOf('.');
        var stem = (dot < 0 ? component : component[..dot]).TrimEnd(' ');
        if (ReservedNames.Contains(stem))
        {
            return FatalKind.ReservedName;
        }

        return null;
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';
}
