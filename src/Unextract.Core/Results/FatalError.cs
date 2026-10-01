using Unextract.Core.Display;

namespace Unextract.Core.Results;

// 原因となった ZIP エントリ。Index は ZIP 内の順序 (0 始まり)。
public sealed record ZipEntryRef(int Index, string Name)
{
    public int Number => Index + 1;

    public string DisplayName => SafeDisplay.Escape(Name);
}

// 全体 FATAL の原因。Entry は ZIP 全体の問題 (開けないなど) では null。
public sealed record FatalError(FatalKind Kind, ZipEntryRef? Entry = null, string? Detail = null)
{
    public string Describe()
    {
        var reason = FatalKindText.Describe(Kind);
        var text = Entry is null
            ? reason
            : $"エントリ #{Entry.Number} \"{Entry.DisplayName}\": {reason}";
        return Detail is null ? text : $"{text} ({SafeDisplay.Escape(Detail)})";
    }
}
