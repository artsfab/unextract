using System.Security;
using System.Text;
using Unextract.Core.Display;
using Unextract.Core.Zip;

namespace Unextract.Core.Entries;

// --entries の1行。LineNumber は 1 始まり。Name は ZIP の FullName として照合する文字列 (trim しない)。
public sealed record EntriesLine(int LineNumber, string Name);

// --entries の入力エラー。LineNumber はファイル全体の問題 (読めない、空、大きすぎる) では null。
public sealed record EntriesError(int? LineNumber, string Reason)
{
    public string Describe() => LineNumber is { } line ? $"--entries の {line} 行目: {Reason}" : $"--entries: {Reason}";
}

public sealed record EntriesParseResult(IReadOnlyList<EntriesLine>? Lines, EntriesError? Error);

// 照合の結果。Selected は選ばれたエントリ (ZIP の順)。
public sealed record EntriesMatchResult(IReadOnlyList<ValidatedZipEntry>? Selected, EntriesError? Error);

// --entries の読み込み・形式の検査 (SPEC §3.1 の手順3、§3.3) と、事前検証を通過したエントリとの照合 (手順8)。
// --entries は delete の処理対象を狭めるフィルタで、安全性の判断材料にしない (DEC-30)。文字列からパスを組み立てない。
// 最初のエラーで中止し、行番号と安全に表示できる形の行内容と理由を返す。
public static class EntriesList
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ファイルを開いて読み、形式を検査する。読み終えたら閉じ、以後は参照しない。上限を超えるファイルは全体を読まない。
    public static EntriesParseResult Read(string path, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(limits);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(stream, limits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException)
        {
            return Fail(null, $"ファイルを読めません ({ex.Message})");
        }
    }

    // stream から最大で上限 + 1 バイトだけ読む。長さが分かるストリームで上限を超えていれば読まない。
    public static EntriesParseResult Read(Stream stream, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);

        var tooLarge = Fail(null, $"ファイルが大きさの上限 ({limits.MaxEntriesFileBytes:N0} バイト) を超えています");
        if (stream.CanSeek && stream.Length > limits.MaxEntriesFileBytes)
        {
            return tooLarge;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var remaining = limits.MaxEntriesFileBytes + 1 - buffer.Length;
            var n = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
            if (n == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, n);
            if (buffer.Length > limits.MaxEntriesFileBytes)
            {
                return tooLarge;
            }
        }

        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), limits);
    }

    // バイト列から行を取り出す (純粋関数)。
    // - UTF-8。不正なバイト列は入力エラー。先頭の UTF-8 BOM は1個だけ除く。UTF-16・UTF-32 の BOM は入力エラー。
    // - LF と CRLF (混在可)。各行末の CR を1個だけ除く。それ以外の CR は入力エラー。最後の改行の後の空文字列は行ではない。
    // - 空行、行が1つも無いファイル、重複行、1行と行数の上限超過は入力エラー。trim・コメント・glob は無い。
    public static EntriesParseResult Parse(ReadOnlySpan<byte> data, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (data.Length > limits.MaxEntriesFileBytes)
        {
            return Fail(null, $"ファイルが大きさの上限 ({limits.MaxEntriesFileBytes:N0} バイト) を超えています");
        }

        // UTF-32 LE (FF FE 00 00) は UTF-16 LE (FF FE) より先に判定する。
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]) || data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return Fail(null, "UTF-32 で保存されています。UTF-8 で保存してください");
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Fail(null, "UTF-16 で保存されています。UTF-8 で保存してください");
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            data = data[3..];
        }

        var lines = new List<EntriesLine>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var lineNumber = 0;
        while (!data.IsEmpty)
        {
            lineNumber++;
            var end = data.IndexOf((byte)'\n');
            var line = end < 0 ? data : data[..end];
            data = end < 0 ? [] : data[(end + 1)..];

            if (lineNumber > limits.MaxEntriesLines)
            {
                return Fail(lineNumber, $"行数が上限 ({limits.MaxEntriesLines:N0} 行) を超えています");
            }

            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.Contains((byte)'\r'))
            {
                return Fail(lineNumber, "行末以外に CR があります");
            }

            if (line.IsEmpty)
            {
                return Fail(lineNumber, "空行です");
            }

            if (line.Length > limits.MaxEntriesLineBytes)
            {
                return Fail(lineNumber, $"1行の長さが上限 ({limits.MaxEntriesLineBytes:N0} バイト) を超えています");
            }

            string name;
            try
            {
                name = StrictUtf8.GetString(line);
            }
            catch (DecoderFallbackException)
            {
                return Fail(lineNumber, "UTF-8 として読めません");
            }

            if (seen.TryGetValue(name, out var first))
            {
                return Fail(lineNumber, $"{first} 行目と同じです ({Quote(name)})");
            }

            seen.Add(name, lineNumber);
            lines.Add(new EntriesLine(lineNumber, name));
        }

        if (lines.Count == 0)
        {
            return Fail(null, "行がありません (空のファイルです)");
        }

        return new EntriesParseResult(lines, null);
    }

    // 各行を事前検証を通過したエントリの FullName と序数比較で照合する (SPEC §3.3)。大小文字と区切りを補正しない。
    // 一致するエントリが無い行、ディレクトリエントリを指定した行は入力エラー。大小文字・区切りだけが違うエントリがあればヒントにする。
    public static EntriesMatchResult Match(IReadOnlyList<EntriesLine> lines, IReadOnlyList<ValidatedZipEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(entries);

        var byName = new Dictionary<string, ValidatedZipEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byName.TryAdd(entry.Entry.FullName, entry);
        }

        var selected = new HashSet<int>();
        foreach (var line in lines)
        {
            if (!byName.TryGetValue(line.Name, out var entry))
            {
                return new EntriesMatchResult(null, new EntriesError(line.LineNumber, Unknown(line.Name, entries)));
            }

            if (entry.IsDirectory)
            {
                return new EntriesMatchResult(null, new EntriesError(line.LineNumber, $"ディレクトリエントリは指定できません ({Quote(line.Name)})"));
            }

            selected.Add(entry.Entry.Index);
        }

        return new EntriesMatchResult(entries.Where(e => selected.Contains(e.Entry.Index)).ToList(), null);
    }

    private static string Unknown(string name, IReadOnlyList<ValidatedZipEntry> entries)
    {
        var text = $"ZIP に一致するエントリがありません ({Quote(name)})";
        var normalized = Normalize(name);

        // §4.3 により、大小文字・区切りだけが違うエントリは ZIP の中に高々1つ。
        var near = entries.FirstOrDefault(e => string.Equals(Normalize(e.Entry.FullName), normalized, StringComparison.OrdinalIgnoreCase));
        if (near is null)
        {
            return text;
        }

        var fullName = near.Entry.FullName;
        var differs = string.Equals(fullName, name, StringComparison.OrdinalIgnoreCase)
            ? "大小文字だけが違う"
            : string.Equals(Normalize(fullName), normalized, StringComparison.Ordinal) ? "区切りだけが違う" : "大小文字と区切りだけが違う";
        return $"{text}。{differs}エントリ {Quote(fullName)} があります";
    }

    private static string Normalize(string name) => name.Replace('\\', '/');

    // 行内容とヒントの FullName は、そのまま --entries に書ける形で示すため \ を変換しない表示用エスケープを使う。
    private static string Quote(string name) => $"\"{SafeDisplay.EscapeForList(name, out _)}\"";

    private static EntriesParseResult Fail(int? line, string reason) => new(null, new EntriesError(line, reason));
}
