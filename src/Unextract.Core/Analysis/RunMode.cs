namespace Unextract.Core.Analysis;

// 実行モード (SPEC §2、§15)。1回の実行全体で固定し、初回分類と削除フェーズに同じ値を渡す (PLAN.md §4 の「Fast モード」)。
// Strict と Fast の処理差は SPEC §6.1 の手順7 と §8.3 の手順3 の2点だけ (DEC-19、DEC-22)。
public enum RunMode
{
    Strict,
    Fast,
}
