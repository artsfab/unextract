# 現在の実装構造

役割: コード配置・依存方向・資源寿命の地図。実装を配置/分岐/抽象化するときに読む。契約・検査順序は仕様が定める。

[依存](#dependencies) / [共有経路](#shared-path) / [寿命](#lifetimes)

<a id="dependencies"></a>
## 配置と依存方向

Cli → Core、Windows → Core。CoreはWin32にもWindows projectにも依存しない。CliがWindowsFileSystemProbeとCoreのcommandを組み立てる。Win32は文書化kernel32/FSCTLだけをLibraryImportとSafeFileHandleで呼び、ntdll未文書API/reflectionを使わない。

| 場所 | 現在の担当 |
|---|---|
| [CliApplication](../src/Unextract.Cli/CliApplication.cs)、[Program](../src/Unextract.Cli/Program.cs) | 引数、ConsolePrompt/ProgressLine、stdout/stderr、Windowsとの組立てと例外境界 |
| [Preparation](../src/Unextract.Core/Commands/Preparation.cs)、[AnalyzeCommand](../src/Unextract.Core/Commands/AnalyzeCommand.cs)、[DeleteCommand](../src/Unextract.Core/Commands/DeleteCommand.cs) | 共通Prepare・寿命と2入口。AnalyzerにIDeletionProbeを渡さず削除能力を型で持たせない |
| [ZipArchiveSource](../src/Unextract.Core/Zip/ZipArchiveSource.cs)、[ZipPrevalidator](../src/Unextract.Core/Zip/ZipPrevalidator.cs)、[EntriesList](../src/Unextract.Core/Entries/EntriesList.cs) | ZIP公開APIのアダプター、名前/種別/構造/宣言量、exact選択入力 |
| [TargetRoot](../src/Unextract.Core/Target/TargetRoot.cs)、[TargetResolver](../src/Unextract.Core/Analysis/TargetResolver.cs)、[RealNameResolver](../src/Unextract.Core/Analysis/RealNameResolver.cs) | root検証、検証済み列挙と実名対応・結果再利用 |
| [HandleInspection](../src/Unextract.Core/Analysis/HandleInspection.cs)、[ContentComparer](../src/Unextract.Core/Analysis/ContentComparer.cs) | 共通の個体/特殊性/サイズ検査と内容検証・比較 |
| [Analyzer](../src/Unextract.Core/Analysis/Analyzer.cs)、[SequentialDeleter](../src/Unextract.Core/Deletion/SequentialDeleter.cs) | 操作別のハンドル種別、FATAL/STOP、逐次結果・指示/成立確認 |
| [Display](../src/Unextract.Core/Display/ReportText.cs) | ReportText/AnalyzeOutput/DeleteOutput/SafeDisplayの純粋な表示生成 |
| [WindowsFileSystemProbe](../src/Unextract.Windows/WindowsFileSystemProbe.cs)、[WindowsHandles](../src/Unextract.Windows/WindowsHandles.cs) | Core抽象のWin32実装・SafeFileHandle所有 |
| [HandleOpener](../src/Unextract.Windows/HandleOpener.cs)、[FileInformation](../src/Unextract.Windows/FileInformation.cs)、[DirectoryEnumerator](../src/Unextract.Windows/DirectoryEnumerator.cs)、[ProtectedLocations](../src/Unextract.Windows/ProtectedLocations.cs) | 用途別open、同一ハンドル情報、列挙、拒否位置の実体解決 |

<a id="shared-path"></a>
## 共通解決・検査・比較とモード分岐

Prepare、TargetResolver/RealNameResolver、HandleInspector、ContentComparer.Verifyを両操作で共有する。操作差は呼び出し側に明示する。analyzeは比較用、deleteは削除用だけを使う。モード分岐はContentComparer.Verifyの非読取経路であり、Fast専用の層・実装クラス・抽象化・追加状態・API・ハンドル構成を作らない。[採用理由](RATIONALE.md#structure)と[モード契約](SPEC.md#modes)による。

ZIP構造の独自パーサを製品へ入れない。テストのZipFixture/ZipPatcherは異常を作る専用生成器で、製品パーサを兼ねない。フックは内部差し込み口で[TESTING](TESTING.md#hooks)が位置と適用条件を定める。

<a id="lifetimes"></a>
## 資源の寿命

PreparationのPreparedがZIPとtargetルートを実行終了まで所有し、Disposeで閉じる。entriesはPrepare中に読み終え閉じ、選択情報だけを使う。確認待ちに個々のtargetハンドルはない。列挙用は検証/列挙の間だけ開き、照合結果だけをRealNameResolverが再利用する。

各エントリの比較用/削除用はその判定/処理中だけ所有し、usingで通常結果・STOP・例外の各経路で閉じる。M0と列挙由来の削除基準はエントリ中だけで、全件の候補/状態を保持しない。内容比較器の実測累計は実行内だけ。具体的なアクセス・共有・フラグ、情報項目と処理順は[FS仕様](spec/filesystem.md#handles)で定める。
