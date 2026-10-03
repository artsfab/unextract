# 検証案内

役割: 検証原則、変更別のテストコード案内、重要回帰・再現上の知識と受入条件。テスト変更・実行時に該当節を読む。

[原則・安全](#principles) / [系列索引](#test-series) / [フック](#hooks) / [回帰](#regressions) / [再現](#reproduction) / [E2E](#e2e) / [受入](#acceptance)

<a id="principles"></a>
## 共通の原則・安全装置

**テストの系列**: `P` (Prepare と削除0件)、`C` (ZIP 内容の検証)、`Z` (ZIP 名・構造)、`R` (resource limits)、`T` (target 分類)、`A` (`analyze`)、`S` (逐次 `delete`)、`L` (`--entries`)、`K` (CLI の引数)、`O` (表示)、`X` (E2E)、`M` (手動)。各IDは現行のテスト名を維持する。

**操作の割り当て**: 特に断らない限り、C、T の各テストは `analyze` で実施する (分類と FATAL の判定)。`delete` での同じ状況の扱いは S 系で確かめる。P、Z、R01〜R06 は Prepare の検査なので、`analyze` と `delete` (`--yes` 付き) の両方で実施し、どちらも同じ FATAL・入力エラー・削除0件になることを確認する。

**analyze の非破壊**: `analyze` を実行する全てのテストで、target 全体 (パス・サイズ・SHA-256・更新日時) と ZIP が実行前後で不変であることを確認する。Core では `OpenForDeletion`、`CheckIdentity`、`SetDispositionEx` が一度も呼ばれないことを呼び出し記録で確認する。

**削除0件**: Prepare ([Prepare](SPEC.md#prepare)) の失敗と確認での中止を扱う全てのテストで、削除0件に加えて、target ルート以外の列挙・比較用オープン・削除用オープンが一度も呼ばれないこと (Core の呼び出し記録) を確認する。

**同じハンドル**: `delete` を扱う Core テストでは、各エントリでパスを使う呼び出しが `OpenForDeletion` 1回 (と、その失敗時の `CheckIdentity` 1回) だけで、`OpenForComparison` が一度も呼ばれないことを確認する。

**モード違いの再利用の原則** ([受入条件](#acceptance)、[モード契約](SPEC.md#modes)): 特に断らない限り、各テストの期待結果は Strict (既定) のものである。Strict と共通の安全性を確かめる次のテストは、同じ fixture・同じ種別で `--fast` を付けても実行する。

- 削除対象になる正常系の期待結果は、`MATCHED` を `SAME_SIZE` に読み替える (`delete` の `DELETED` はそのまま)。
- それ以外の、両モードに共通して適用される安全性の期待結果 (入力エラー、内容検証に依存しない FATAL・STOP、`MISSING`、`SKIPPED_SPECIAL_FILE`、削除0件、`DELETE_FAILED`、識別確認の判定、削除される対象と残る対象) は、Strict と同じとする。
- [内容検証基準](spec/zip.md#verification) の内容検証に依存する FATAL・STOP (内容比較候補の [内容検証基準](spec/zip.md#verification) の 1〜5 の違反、比較中の target の読み取り失敗、実測展開量) は、この原則に含めない。Fast では発生しない ([モード契約](SPEC.md#modes))。Fast での扱いは C15、R09、T17、S06、S07 で確かめる。

対象: P02〜P08、Z01〜Z09、R01〜R06、T02〜T16 (T10 の「比較中の読取失敗」と T12 の「比較中 target-read 例外」の経路を除く)、A03 (FATAL の原因を内容に依存しないものにする)、A04〜A06、S01〜S04、S08〜S12、S16〜S24、S27〜S38 (S13〜S15、S25、S26 の比較中の経路を除く)、L 系、O08〜O10、O12〜O16。

**期待値の扱い**: Skip・期待値緩和は禁止。実測の結果は推測で確定せず、[未確認一覧](OPEN_ISSUES.md#observations)に観測条件と範囲を記す。仕様と違えば明示された規定・決定を調べ、新しい製品判断は保留して報告する。


実削除を伴う Win・E2E テストは、テストが自分で作った一意な fixture ディレクトリの中のファイルだけを削除する。削除指示の直前のフック (H5) で、テスト側のガード (`DeletionGuard`) が削除用ハンドルの最終パスが fixture の内側であること (`\` 境界付きの比較) と、fixture から対象の親までの各ディレクトリが reparse point でないことを確かめ、違反なら例外で中止する。このガードはテストの安全装置であり、製品の安全装置の代わりにしない。ACL を変えるテストは `finally` で元に戻し、終了後に `icacls` で DENY が残っていないことを確認する。fixture のディレクトリ自体はテストから削除しない (PTY の例外は E2E節)。

<a id="test-series"></a>
## 変更別の系列とコード

Coreは副作用・API失敗を注入した判定、Winは実NTFS/Win32、CLIは同一プロセス、E2Eはexeの別プロセス、PTYは対話機能、Mは実端末を担う。単純ケースの入力・期待値は現行メソッドを読む。テスト名/ID/assertは文書再編で変更しない。CoreだけではWindowsの同一個体保証は証明できない。

| 変更領域 | 系列 | 担当コードと追加先 |
|---|---|---|
| Prepare・削除0件 | P | [CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[InputIntegrationTests](../tests/Unextract.Windows.Tests/Integration/InputIntegrationTests.cs) |
| ZIP内容・CRC | C | [ContentVerificationTests](../tests/Unextract.Core.Tests/ContentVerificationTests.cs)、[Win分類](../tests/Unextract.Windows.Tests/Integration/ClassificationIntegrationTests.cs) |
| 名前・種別・構造・runtime形式 | Z | [EntryPathTests](../tests/Unextract.Core.Tests/EntryPathTests.cs)、[EntryTypeTests](../tests/Unextract.Core.Tests/EntryTypeTests.cs)、[StructureTests](../tests/Unextract.Core.Tests/StructureTests.cs)、[ZipArchiveSourceTests](../tests/Unextract.Core.Tests/ZipArchiveSourceTests.cs) |
| 上限 | R | [LimitsTests](../tests/Unextract.Core.Tests/LimitsTests.cs)、[Win入力](../tests/Unextract.Windows.Tests/Integration/InputIntegrationTests.cs) |
| target・属性・実名 | T | [ClassificationTests](../tests/Unextract.Core.Tests/ClassificationTests.cs)、[FileAttributeRulesTests](../tests/Unextract.Core.Tests/FileAttributeRulesTests.cs)、[Win分類](../tests/Unextract.Windows.Tests/Integration/ClassificationIntegrationTests.cs)、[TargetRootTests](../tests/Unextract.Core.Tests/TargetRootTests.cs) |
| analyze | A | [CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[ClassificationTests](../tests/Unextract.Core.Tests/ClassificationTests.cs)、[Win逐次](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs) |
| 削除・競合・エラー | S | [SequentialDeleteTests](../tests/Unextract.Core.Tests/SequentialDeleteTests.cs)、[Win逐次](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)、[DeletionHandleTests](../tests/Unextract.Windows.Tests/DeletionHandleTests.cs) |
| entries | L | [EntriesListTests](../tests/Unextract.Core.Tests/EntriesListTests.cs)、[CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[Win逐次](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs) |
| 引数 | K | [CommandLineParserTests](../tests/Unextract.Core.Tests/CommandLineParserTests.cs)、[CliApplicationTests](../tests/Unextract.Cli.Tests/CliApplicationTests.cs) |
| 表示・警告 | O | [AnalyzeOutputTests](../tests/Unextract.Core.Tests/AnalyzeOutputTests.cs)、[CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[SafeDisplayTests](../tests/Unextract.Core.Tests/SafeDisplayTests.cs)、[CliApplicationTests](../tests/Unextract.Cli.Tests/CliApplicationTests.cs) |
| exe・対話 | X | [E2ETests](../tests/Unextract.E2E.Tests/E2ETests.cs)、[PtyConfirmationTests](../tests/Unextract.E2E.Tests/PtyConfirmationTests.cs) |
| 実端末・実測 | M / L18 | [手動手順](MANUAL_TESTS.md)、[唯一の状態更新先](OPEN_ISSUES.md#manual-status) |

### 操作・モード適用の読み方

A05はanalyzeへの--yes/-y/--entries/--dry-run拒否をK系と同じ条件で確認し、targetに触れないことを確認する。C/Tは通常analyze、deleteの同じ状況はSで確認する。P/Z/R01〜R06は両操作のPrepareを確認する。上の原則に指定した共通安全性ケースを同じfixtureでFastでも実施する。H3はStrictだけなので、Fastへ単純に比較中の変更を適用しない。Fastの安全性変更は[FS](spec/filesystem.md#delete-flow)、分岐配置は[実装案内](ARCHITECTURE.md#shared-path)も読む。

<a id="hooks"></a>
## フックと失敗注入

フックの正本は本節。製品CLIから設定しない内部差し込み口で、テストは受け取ったハンドルを閉じない。

| フック | 位置 | 適用 |
|---|---|---|
| H1 / BeforeOpen | 解決・事前判定後、削除用open直前 | Strict/Fast |
| H2 / AfterOpen | 削除用open直後、照合前 | Strict/Fast |
| H3 / DuringCompare | 全バイト比較中、target最初のread直前 | Strictのみ |
| H4 / BeforeFinalCheck | 最終確認直前 | Strict/Fast |
| H5 / BeforeDisposition | 最終確認後、削除指示直前 | Strict/Fast。fixtureガードにも使う |

analyzeのAfterResultsは結果表示直後、deleteのAwaitingConfirmationは確認直前。偽FSでエラーコード・情報取得・削除API失敗を注入する。コード位置は[DeleteHooks](../src/Unextract.Core/Deletion/SequentialDeleter.cs)、[DeleteCommand](../src/Unextract.Core/Commands/DeleteCommand.cs)、[AnalyzeCommand](../src/Unextract.Core/Commands/AnalyzeCommand.cs)、[FakeFileSystem](../tests/Unextract.Core.Tests/Fakes/FakeFileSystem.cs)。

<a id="regressions"></a>
## 誤解しやすい回帰の意図

| 回帰 | 確認する条件・落とし穴 | 担当コード |
|---|---|---|
| S09 | ZIP自身の共有違反→識別確認一致→DELETE_FAILED・終了1、ZIP不変、後続を実削除、未処理0。専用SKIPPEDへ変えない | Core/Win逐次の `S09_ArchiveItselfIsNotDeleted` |
| S20 | hardlinkは開いた名の親IDと不一致でも、確認済み非dir・非削除保留・リンク数2以上の非削除例外。通常候補の親照合と取得失敗は残す | Core/Win逐次のS20 |
| S22/S23 | 同じ個体を同名の別親へ移すと親IDだけ、junction/大小文字改名は最終パスだけが変わり得る | Win逐次 |
| S25/S27 | 共有モードで防げないADS/hardlink/属性変更。Strict H3と共通H4でM0全項目を検査する | Win逐次、Core失敗注入 |
| S28/S31〜S33 | 指示失敗、取得失敗、API成功だがDeletePending=false、指示後例外。非削除と「可能性あり」を区別し、後続を止める | Core/Win逐次、CLI出力 |
| S34 | 親DELETE_CHILDが許可する実行前拒否と、Strict比較中ChangeTime変更を分ける。全モード/時点へ外挿しない | Win逐次、OPEN_ISSUES |
| S35/S36 | 自分の削除で再列挙しない。列挙後の新名はMISSING、同名重複は最初のIDを採用しopenで照合 | Core/Win逐次 |
| A07/A08 | 競合・権限差なしの一致は保証ではない。メタデータを書き戻した内容変更、同名の新個体、ADS/read-onlyを現在状態で判定 | Win逐次 |
| C09/C13 | 内容不一致でも終端を読み、後方破損を見逃さない。runtime単体の観測変化と製品FATAL維持を分ける | Core内容検証、Win分類 |
| C15/R09/T17/S06/S07 | Fastのbody非読取・実測量非計上・同サイズ異内容の削除 | Core内容/上限/分類/逐次、X26 |
| T16/S19 | dirにFileStreamInfoを問い合わせない。事前判定通過後もハンドル検査を省かない | Core/Win分類・逐次 |
| L10/L11/L13/L16 | 最後の入力誤りでも削除0件、指定外は解決/列挙しない、ZIP全体検査は維持。entries自身は読み終え閉じているので選択されれば削除できる | CommandTests、Win逐次 |
| O13/O15 | 出力失敗後に削除0件と断定しない。指示後の可能性ありとSTOP前の実削除を要約する | CommandTests、CliApplicationTests |

### ケース横断の検証要件

CRC/CDだけ変更、Deflate破損・途中切れ・Length過少/過大、Storedの過少/過大、暗号化フラグ、未対応方式を、target不在・サイズ不一致・サイズ一致の3状態で組み合わせる (C01〜C08)。前2状態ではZIP Open/CRC計算なし、Strictサイズ一致ではFATAL。Fastは同じ全状態でbody非読取、サイズ一致はSAME_SIZE。先頭不一致+後方破損、0byte CRC非ゼロ、DDのCD由来CRCも確認する。

R系列は既定上限ちょうど/+1と小さな注入値の双方を扱う。メタデータ総量はUTF-16名バイト数+固定件数分で、通常の+2だけでなく奇数注入で+1を確認。全MISSING/選択外でも宣言合計を検査し、実測超過でそれ以上読まず、Fastは実測を計上しない。

Z系列は予約名の成分・拡張子/空白/大小文字、ZIP内のcase/file-dir/親file衝突、種別と区切りの矛盾、ZIP64/DD/調整済SFX/末尾ごみと未調整SFXを区別する。T系列は8.3生成とcase-sensitive設定の前提不成立を成功扱いしない。属性の未知ビットも既知許可ビットとの組合せで確認する。

L系列はUTF-8/BOM、UTF-16/32 BOM、不正UTF-8、空/BOMのみ、LF/CRLF混在と末尾改行、行途中CR、trimなし、重複の両行番号、未知/大小文字/区切りヒント、dir/暗黙dir、コメント/glob無し、境界、最後の行の誤り、転記不可文字を確認する。入力のUTF-16拒否とPowerShellの実保存形式L18は別である。

O/X系列はカテゴリー内ZIP順、0件カテゴリーの行省略と合計への掲載、モード別カテゴリー、FATALの判定済み/原因/未判定、stdout/stderr所属、非対話stdinにyを書いても中止、STOP前の部分削除、正常exe/publish exeの転記・日本語・CP437を確認する。生の危険文字をソースへ入れずescape fixtureで作る。

<a id="reproduction"></a>
## 再現するときの知識

以下のE-2・PoC 6の原観測は `606d89c:docs/PLAN_VALIDATION.md` の同名見出しで参照できる。

- ACLはfinallyで復元し、icaclsでDENY残存を確認する。親DACLを途中で変えると継承再適用で子・兄弟のChangeTimeが変わり、狙った対象より先にSTOPし得る。親DELETE_CHILD拒否は操作開始前に設定し、競合で変える対象を限定する。
- ADS追加側はDELETEを共有しなければ、製品の削除用ハンドルと共有違反になる。共有R/W/Dを使って、ADSによる最終確認の検出とopen拒否を混同しない。これらは2026-10-02 E-2 (Windows 11 10.0.26300、NTFS、.NET 10.0.12、同一プロセス別ハンドル) のfixture上の観測で、PoC 6には別プロセスの観測もある。
- メタデータ書き戻しは内容変更を隠せる。A08の現在内容検証でStrict MODIFIEDとFastの契約を確認し、ID/日時だけを一致の証拠にしない。
- Fast body非読取はCoreのRecordingContentProvider、ThrowOnRead、呼び出し記録でGetContent/Open/target readを直接確認する。CRC計算専用フックはないので、body非読取・CRC期待値非参照からの間接確認と区別する。Winの結果だけをbody/CRCの直接観測と呼ばない。
- 実測ではOS、SDK/runtime、NTFS、同一/別プロセス、フック位置、モード、実施できなかった前提を短く記録する。Coreで模擬できてもWinで再現不能なら未確認に残す。

<a id="e2e"></a>
## E2E・PTY・実行案内

通常のbuild/testコマンドは[ルートREADME](../README.md#ビルドとテスト)、CIの正本は[workflow](../.github/workflows/ci.yml)。dotnet runを使わずビルド済みexeを使う。publishはリポジトリ外へ出し、publish版E2E/PTYの標準は[run-e2e-tests.ps1](../scripts/run-e2e-tests.ps1)。wrapperは自作tempへRelease/win-x64 publish、UNEXTRACT_E2E_EXE設定、E2E全体実行、環境変数復元・cleanupを行う。通常buildの全体検証と配布exe検証を区別する。

E2E自身はpublishせず、UNEXTRACT_E2E_EXEがあればそれ、なければ通常build出力exeを使う。見つからなければ失敗で、前提不成立にしない。stdin/stdout/stderrをリダイレクトし出力をUTF-8で読む。60秒timeoutでprocess treeを終了させ失敗とする。fixtureは出力先fixturesのテスト名+GUIDで、起動前のDeletionGuardも働かせる。

PTYは同一端末にstdout/stderrが流れるため、所属はCore/CLIで別に確認する。X28は実際の警告順序とn中止を担い、y・実端末の字形/折り返し/視認性はMに残す。Porta.PtyはE2E専用依存で製品に追加しない。通常fixtureは保存し[cleanup script](../scripts/clean-test-fixtures.ps1)を使う。PTYだけはprocess tree終了・Dispose後に自作GUID fixtureをcleanupし、失敗を黙殺せず元の例外・terminal outputを保持する。wrapperも自作tempだけを片付け、外部exeや既存bin/objを削除しない。

実端末は[MANUAL_TESTS](MANUAL_TESTS.md)、実施状態と手順保留は[OPEN_ISSUES](OPEN_ISSUES.md)。M10/M13は実行前にレビューする。全製品テスト合格はM系や前提不成立を埋めない。

<a id="acceptance"></a>
## 受入条件

必須系列の全項目が通り、実測の条件付き結果と未確認の扱いが決まり、以下を確認した時点を製品のテスト完了とする。合格件数だけで完了を判断しない。

- analyzeは削除能力を持たず全件の分類・非破壊、FATALの判定済み/未判定を確認する。
- Prepare・entries入力エラー・確認中止で削除0件。旧CLIとdry-runは開始前に拒否する。
- deleteは1件ずつ1回open、同じハンドルで1回比較 (Strict)・M0全項目・指示・成立確認。選択外、特殊対象、ZIP自身、無関係ファイル、dirを削除しない。
- DELETE_FAILEDで非削除続行、未知エラーSTOP、親ID/最終パス/事前判定が働く。STOP前の削除は戻らず後続は未処理。指示後STOPは削除された可能性を保持する。
- 共通安全性をFastでも確認し、同サイズ異内容の削除を仕様どおりと判定する。非読取と実測量非計上も確認する。
- 未確認・前提不成立・手順未確立・対象外を区別し、ファイルsymlink/placeholderを成立とみなさない。READMEの要約を契約・限界と同期する。

文書・コメントだけの変更では破壊を伴う実測や全製品テストを新規実行する必要はない。

### 自動テストで代替しない現行要求

L18はWindows PowerShell 5.1の `>` と `Out-File -Encoding utf8` の保存形式を実測する。Get-Content/Set-Contentで形式を作り替えず先頭バイトを記録し、UTF-16ならL02の拒否・UTF-8案内、BOM付きUTF-8ならL01の受理を確認する。native出力の文字化けも記録する。[M13](MANUAL_TESTS.md#m13)は手順レビュー待ちで、L02/X21のBOM拒否をもって完了とはしない。

S26/S34/S37/S38は観測ケースであり、現在の限定付き結果は[OPEN_ISSUES](OPEN_ISSUES.md#observations)にある。S26は改名成功なら最終パス不一致STOP/非削除、失敗なら通常処理を確認する。S34はDELETED/STOP、DeletePending・終了状態・対象以外の非削除を記録する。S37は成功なら属性SKIPPED、open失敗なら識別確認のDELETE_FAILEDで、いずれも非削除を確認する。S38は属性/tagごとに一致/不一致を記録し、相違があれば事前判定の説明を検討する。未観測を推定でassertの期待値にしない。

M09/M10/M11/M12は必須の確認事項に残す。M09は同時open調査 (設計非依存)、M10は中断後の表示と実削除、M11はメモリマップ経由の書き込み、M12(a)/(b)は近似の祖先改名/read-only open。M10の許容とM11の再現方法は未確立で、製品保証を追加しない。M01〜M08の実端末手順もPTY/E2Eだけでは代替しない。
