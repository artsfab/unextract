using Unextract.Core.Display;

namespace Unextract.Core.Results;

// 原因となった ZIP エントリ。Index は ZIP 内の順序 (0 始まり)。
public sealed record ZipEntryRef(int Index, string Name)
{
    public int Number => Index + 1;

    public string DisplayName => SafeDisplay.Escape(Name);
}

// エントリの診断用段階。判定・削除順序は変更せず、判明した段階だけ保持する。
public enum EntryStep
{
    Resolve,
    Open,
    Verify,
    Inspect,
    Compare,
    FinalCheck,
    Dispose,
    Confirm,
}

// 全体 FATAL の原因。Entry は ZIP 全体の問題 (開けないなど) では null。
// Step と Win32Error は診断用。日本語の Detail を解析して復元しない。
public sealed record FatalError(FatalKind Kind, ZipEntryRef? Entry = null, string? Detail = null, EntryStep? Step = null, int? Win32Error = null)
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
