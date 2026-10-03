namespace Unextract.Core.Analysis;

// 実行モード (SPEC §2、§15)。1回の実行 (analyze または delete) の全体で固定する (PLAN.md §4 の「Fast モード」)。
// Strict と Fast の処理差は、内容を読むかどうか (SPEC §6.1 の手順8、§8.3 の手順6) の1点だけで、analyze と delete が共有する
// ContentComparer.Verify の1か所で分岐する (DEC-34)。
public enum RunMode
{
    Strict,
    Fast,
}
