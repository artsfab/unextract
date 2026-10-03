namespace Unextract.Core.Analysis;

// 実行モード (docs/spec/cli.md#arguments、docs/SPEC.md#modes)。1回の実行 (analyze または delete) の全体で固定する (docs/ARCHITECTURE.md#shared-path の「Fast モード」)。
// Strict と Fast の処理差は、内容を読むかどうか (docs/spec/filesystem.md#resolution の手順8、docs/spec/filesystem.md#delete-flow の手順6) の1点だけで、analyze と delete が共有する
// ContentComparer.Verify の1か所で分岐する (docs/RATIONALE.md#fast)。
public enum RunMode
{
    Strict,
    Fast,
}
