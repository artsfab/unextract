namespace Unextract.Windows.Tests.Rar;

// 採用版の UnRAR.dll を同じテストプロセスでロードして使うテストクラスは、すべてこのコレクションに入れて直列に実行する。
// UnRAR.dll のエラーの状態はプロセス全体で1つ (unrarsrc 7.2.3 の global.hpp の ErrHandler) で、RAROpenArchiveEx でだけ消去され、
// RARReadHeaderEx・RARProcessFileW は成功時もその状態を返す。別スレッドの別ハンドルでの中止・CRC 不一致などが、並行する
// ハンドルの RAR_SKIP・RAR_TEST の結果 (ERAR_UNKNOWN など) に漏れる (2026-10-09 に並列実行で観測。docs/RATIONALE.md#rar-dll-usage)。
// 製品は1プロセスで同時に1つのハンドルだけを単一スレッドで使うので、この直列化は製品の使い方をテストで再現するもので、判定を緩めない。
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UnrarDllCollection
{
    public const string Name = "UnRAR.dll";
}
