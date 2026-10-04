# 検証案内

役割: 検証原則、変更別のテストコード案内、重要回帰・再現上の知識と受入条件。テスト変更・実行時に該当節を読む。

[原則・安全](#principles) / [系列索引](#test-series) / [GUI](#gui) / [フック](#hooks) / [回帰](#regressions) / [再現](#reproduction) / [E2E](#e2e) / [受入](#acceptance)

<a id="principles"></a>
## 共通の原則・安全装置

**テストの系列**: `P` (Prepare と削除0件)、`C` (ZIP 内容の検証)、`Z` (ZIP 名・構造)、`R` (resource limits)、`T` (target 分類)、`A` (`analyze`)、`S` (逐次 `delete`)、`L` (`--entries`)、`K` (CLI の引数)、`O` (表示)、`J` (機械可読出力と実行ログ)、`X` (E2E)、`M` (手動)。各IDは現行のテスト名を維持する。

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
| 機械モード引数・事前検出 | J01〜J03 | [CommandLineParserTests](../tests/Unextract.Core.Tests/CommandLineParserTests.cs) のJ01/J02、[MachineModeDetectionTests](../tests/Unextract.Cli.Tests/MachineModeDetectionTests.cs) のJ03 ([起動](spec/machine-output.md#invocation)) |
| Prepare診断の保持 | J04 / P / L | [PrepareDiagnosticTests](../tests/Unextract.Core.Tests/PrepareDiagnosticTests.cs) のJ04、CommandTestsのP11、EntriesListTestsのL02〜L09/L17。入力エラー/FATAL、原因参照・行番号、件数未取得と空ZIP、表示不変を確認 ([result](spec/machine-output.md#result)、[コード](spec/machine-output.md#codes)) |
| 共通FS・内容検証の診断 | J05 / T / C / P / S | [HandleDiagnosticTests](../tests/Unextract.Core.Tests/HandleDiagnosticTests.cs) のJ05は最終確認の取得失敗と全M0不一致・番号省略・API順序。ClassificationTestsのT10/T13、ContentVerificationTestsのC01〜C08、TargetRootTestsとJ04で元のWin32値・段階を確認。S25/S27/S30とFast非読取は既存の回帰 ([コード](spec/machine-output.md#codes)) |
| delete固有の診断 | J06 / S / O15 | [DeleteDiagnosticTests](../tests/Unextract.Core.Tests/DeleteDiagnosticTests.cs) のJ06は識別確認自体の番号、resolve・hardlink検査の失敗、指示失敗後の状態と不確実性、正常結果の診断省略、通知とStopの原因参照を確認。SequentialDeleteTestsのS13/S15〜S18/S22/S23/S25/S27〜S33と例外テストは原因種別・段階・番号を確認し、S05/S06とCommandTestsのO15は同じハンドル・順序・表示を回帰 ([result](spec/machine-output.md#result)、[コード](spec/machine-output.md#codes)) |
| close後の同期通知・途中件数 | J07 / A / S / O | [CommandNotificationTests](../tests/Unextract.Core.Tests/CommandNotificationTests.cs) のJ07はPrepare通知→open→close→結果通知→次のopen、通知例外の非継続・資源終了、ZIP順とFATAL原因非通知、DIRECTORY累計・STOPPED・欠番選択・配送前の処理済み事実を両モードで確認。空ZIP/全DIRECTORY、64bit宣言Lengthと追加I/Oなし、Prepared終了処理例外、通知経路の表示/進捗/確認入力非使用も確認。ClassificationTests/SequentialDeleteTests/CommandTestsが既存のハンドル数・結果・人間向け表示を回帰 ([レコード](spec/machine-output.md#records)、[result](spec/machine-output.md#result)、[完了境界](spec/machine-output.md#boundary)) |
| 表示・警告 | O | [AnalyzeOutputTests](../tests/Unextract.Core.Tests/AnalyzeOutputTests.cs)、[CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[SafeDisplayTests](../tests/Unextract.Core.Tests/SafeDisplayTests.cs)、[CliApplicationTests](../tests/Unextract.Cli.Tests/CliApplicationTests.cs) |
| ASCII/LFのシリアライズ | J08 | [MachineOutputTests](../tests/Unextract.Cli.Tests/MachineOutputTests.cs) はrun/entry/resultの明示的フィールド、null省略、数値/bool、64bit Length、BOM/CRなし・LF終端、日本語/CP437相当の文字/補助平面/Cf/C1/区切り/引用符/バックスラッシュのraw name往復を確認。Core型からの対応は別に検証する ([出力先](spec/machine-output.md#destinations)、[レコード](spec/machine-output.md#records)) |
| 同期配送・出力失敗先の非再利用 | J09 | [MachineOutputWriterTests](../tests/Unextract.Cli.Tests/MachineOutputWriterTests.cs) は同一byte列のログwrite/flush→stdout write/flush、借用stdout非close、ログ/stdoutのwrite・部分write・flush失敗、健全な先だけへのOUTPUT_FAILED、報告失敗・全出力不能、終端失敗の追加result拒否、ログclose失敗の型と非retryを確認。各処理段階との結合はJ13が担う ([出力先](spec/machine-output.md#destinations)、[result](spec/machine-output.md#result)) |
| ログの新規作成・実共有 | J10 | [ExecutionLogTests](../tests/Unextract.Cli.Tests/ExecutionLogTests.cs) はCreateNewによる既存ログ/ZIP/entriesの不変、作成不能・競合作成の型付き失敗、デバイス (`NUL`・`CON`) とパイプを開けてもログとして受理しないこと (パイプには何も書かない)、各レコードの別読取ハンドルからの可視性、保持中の書込/削除open拒否とclose後の解放を確認。削除指示は行わず、自作GUID fixtureを保存する ([実行ログ](spec/machine-output.md#log)) |
| Core型からv1レコードへの対応 | J11 | [MachineRecordTests](../tests/Unextract.Cli.Tests/MachineRecordTests.cs) は全原因種別・状態・SkipReason・段階の明示的対応、Number/raw name/64bit Length、runのパス・件数、全outcome/終了コード、モード・段階によるcountsとerrorの省略、STOP診断・不確実性、配送失敗対象を含む報告専用集計をwriterなしで確認。analyzeの通常/空/FATAL件数は自作GUID fixtureで実commandの結果とも照合し、削除は行わない ([レコード](spec/machine-output.md#records)、[result](spec/machine-output.md#result)、[コード](spec/machine-output.md#codes)) |
| CLIの機械経路・ログ所有・終端 | J12 | [MachineCliTests](../tests/Unextract.Cli.Tests/MachineCliTests.cs) は両操作/両モードの通知接続、全outcomeと終了コード、ShowProgress有効時のstderr空・確認入力非参照、Prepare各段階・引数/ログ作成失敗の非接触、空/全DIRECTORY・entriesの欠番選択と未処理を確認。実ZIP/log/entriesと既存FakeFileSystemを使い、削除は模擬する。ログ作成後のPrepare失敗・存在しないZIP/entriesとログの同一パス、正常byte一致・終了後解放、Preparedのclose例外時のZIP解放と成功result抑制、OUTPUT_FAILEDと開始有無、終端log write/close失敗・stderr失敗でも実終了1を確認 ([起動](spec/machine-output.md#invocation)、[result](spec/machine-output.md#result)、[実行ログ](spec/machine-output.md#log)) |
| 配送失敗と完了境界の結合 | J13 | [MachineBoundaryTests](../tests/Unextract.Cli.Tests/MachineBoundaryTests.cs) は各I/O直前に既存fakeの呼び出し記録を取り込み、run配送→処理→当該ファイルのclose→log write/flush→stdout write/flush→次のopenと、Prepared終了後のresultを同じ時系列で確認。両操作/両モードのrun・先頭/途中/最終entry・resultにwrite/部分write/flush失敗を注入し、後続非接触・失敗先の非再利用・未終端行、配送失敗対象を含む件数とDIRECTORY/選択外/後続0件の境界を確認。STOPPED配送失敗のpossibly_deletedと開始有無、両出力先/報告の失敗、Prepared closeとの例外競合、終端resultと実終了コードの食い違い・ログclose失敗も確認。target削除は模擬で、実パイプ消失の観測はE2Eが担う ([出力先](spec/machine-output.md#destinations)、[result](spec/machine-output.md#result)、[完了境界](spec/machine-output.md#boundary)) |
| 機械出力の通常/publish exe | J14〜J16 | [MachineOutputE2ETests](../tests/Unextract.E2E.Tests/MachineOutputE2ETests.cs) は両操作/両モードの分類・件数・終了コード、ASCII/LF、生のnameとUTF-8/CP437/Cf/補助平面、空ZIP/全DIRECTORY、ログとのbyte一致を確認。JSON→UTF-8 entries→deleteで同サイズ変更を再検証し、選択外と未処理を区別する。Prepare失敗 (`--log NUL` を含む) ではrunなし・削除0件・作成後ログのresultを確認。CRC異常のFATAL/STOPとFast非読取、実共有拒否のDELETE_FAILEDと後続削除も確認 ([レコード](spec/machine-output.md#records)、[result](spec/machine-output.md#result)) |
| exe実行中のログ保持・共有 | J17〜J18 | 同じE2EのJ17はtarget内ログのDELETE_FAILED・非削除、後続削除と終了後の解放を確認。J18はstdoutのdrainを止め、別読取ハンドルからrunとentryのLFを観測して、ログ先行・実行中の可視性・書込/DELETE access拒否を確認し、drain再開後に全byteを照合する。削除指示は製品経路だけで、通常fixtureは保存 ([実行ログ](spec/machine-output.md#log)) |
| 実パイプ切断の観測 | J19 | 同じE2EのJ19はrunのLF受信と別ハンドルからのログentry観測を同期点にstdoutの読み手をcloseする。途中で検出した場合はOUTPUT_FAILED/終了1・後続未処理、非検出時は承認範囲の完走を検証し、ログの最後のentry/result・countsと実削除範囲を照合する。終端配送だけの失敗では記録済resultと実終了コードを区別する。検出自体を全環境のassertにせず、観測条件は[OPEN_ISSUES](OPEN_ISSUES.md#observations)へ記す ([出力先](spec/machine-output.md#destinations)、[result](spec/machine-output.md#result)) |
| exe・対話 | X | [E2ETests](../tests/Unextract.E2E.Tests/E2ETests.cs)、[PtyConfirmationTests](../tests/Unextract.E2E.Tests/PtyConfirmationTests.cs) |
| 実端末・実測 | M / L18 | [手動手順](MANUAL_TESTS.md)、[唯一の状態更新先](OPEN_ISSUES.md#manual-status) |

### 操作・モード適用の読み方

A05はanalyzeへの--yes/-y/--entries/--dry-run拒否をK系と同じ条件で確認し、targetに触れないことを確認する。C/Tは通常analyze、deleteの同じ状況はSで確認する。P/Z/R01〜R06は両操作のPrepareを確認する。上の原則に指定した共通安全性ケースを同じfixtureでFastでも実施する。H3はStrictだけなので、Fastへ単純に比較中の変更を適用しない。Fastの安全性変更は[FS](spec/filesystem.md#delete-flow)、分岐配置は[実装案内](ARCHITECTURE.md#shared-path)も読む。

<a id="gui"></a>
## GUIの配置・検索・Target管理・プロセス寿命の検証

契約は[GUI仕様](spec/gui.md)、配置は[ARCHITECTURE](ARCHITECTURE.md#gui)。画面を変えたときの確認は[下の節](#gui-review)、人間の受入は[M14](MANUAL_TESTS.md#m14)。

[Gui.Tests](../tests/Unextract.Gui.Tests/DeploymentTests.cs)は、通常buildの同梱CLIが入力エラーだけの呼び出しで起動できること、GUIのdepsにCore/Windows/Cliがないこと、固定配置以外のexeへフォールバックしないことを確認する。CLI欠落時の説明表示はSTA上でWPFの実ツリーを作って検証する。fixtureは既存方針どおり保存する。

[JsonlReceiverTests](../tests/Unextract.Gui.Tests/JsonlReceiverTests.cs)は、byte分割・複数行同時受信、生のUnicode名・64bit length、LF未終端断片、未知フィールド・診断コードの受理と、不正な版・型・順序・必要項目・completed件数の拒否を確認する。正常解析時に省略される未判定件数と、途中終了時の必要項目を区別する。deleteのindex欠番とSTOPPED、internal_errorの不確実性を保持する。

[CliProcessRunnerTests](../tests/Unextract.Gui.Tests/CliProcessRunnerTests.cs)は、実終了と両EOFの独立待機、実終了コード優先・result保持、起動の確実性、終了未確認後の再起動防止、解析だけのキャンセルを模擬adapterで検証する。自作のPowerShell子プロセスでも即時終了・両パイプの大量出力・出力異常後のdrain・stderrの保持制限・キャンセル・result欠落と終了コード不一致を確認する。delete役の子は実削除せず、所有するreleaseファイルで通常終了させる。同梱の実CLIでは、自作ZIPとtargetの空/非空解析をStrict/Fastで実行し、ファイルのパス・サイズ・SHA-256・更新日時の不変を確認する。これらはGUIの削除キュー・終了操作・性能の受入を代替しない。

[ArchiveSearchTests](../tests/Unextract.Gui.Tests/ArchiveSearchTests.cs)は、自作fixtureで非再帰/再帰、ZIP拡張子、メタデータ、hidden/systemのZIP・ディレクトリと実junctionの回避を確認する。ZIPの中身は検索で読まない。内部adapterでは列挙途中の例外、アクセス拒否、走査中の消失・メタデータ失敗、再帰進入前のreparse差替えを注入し、残りの場所が検索されることを確認する。Targetの存在確認は、実際の後からの作成と、確認不能の注入を分けて検証する。通常の属性確認による検索回避を、敵対的な同時変更下のハンドル安全性保証に読み替えない。

[TargetTemplateTests](../tests/Unextract.Gui.Tests/TargetTemplateTests.cs)は、既知変数・最後の拡張子・環境変数等の非展開、空名・不正構文・絶対パス形式・Windows名・dot成分、重複キーの区切りとドライブルートを確認する。[SearchSessionTests](../tests/Unextract.Gui.Tests/SearchSessionTests.cs)は、一括追加対象・個別登録エラー・重複スキップ、親三状態、フィルタ/閲覧と選択の独立、全件/表示中の選択、設定編集・モデル除去、再検索の破棄確認、操作ロック、存在の再評価・保存失敗通知を確認する。[SearchSettingsTests](../tests/Unextract.Gui.Tests/SearchSettingsTests.cs)は、自作設定ファイルと共有拒否を用い、最後の1件だけの保存、破損・読込失敗時の既定値、保存失敗のパス付き通知を確認する。利用者の設定ファイルやACLは変更しない。

[SearchViewTests](../tests/Unextract.Gui.Tests/SearchViewTests.cs)は、STA/Dispatcher上のWPFツリーで、作業一覧 (Archive行の後にそのTarget行が並ぶ1つの仮想化一覧で、行の中に一覧を入れ子にしない) のバインディング、親三状態、表示中の選択操作、行でのSpaceによる選択の切替と矢印による閲覧の分離、Targetエディター (各プリセットと形式エラー、一括追加で全Archiveを解決すること) を確認する。[ViewStateTests](../tests/Unextract.Gui.Tests/ViewStateTests.cs)は、閲覧対象 (詳細欄) が選択・絞り込み・除去・実行中のロックから独立していること (絞り込みで隠れても閲覧を保ちその旨を示す、除去した閲覧中のTargetはそのArchiveの表示へ戻る、選択は変えない)、次の操作の案内と選択の要約の遷移、状態バッジ・行の要約・操作できない理由の表示を確認する。これらは表示専用で、削除計画などの判定は従来どおりであることも確認する。[ScreenRenderTests](../tests/Unextract.Gui.Tests/ScreenRenderTests.cs)は、主な状態 (初期、検索後、Target追加、解析結果、解析失敗、絞り込みで隠れた閲覧、解析中、キャンセル、Fast、削除後の成功・結果が不完全・未実行、通知) を既定寸法・最小寸法・1920×1080の150%/200%相当の論理寸法で描画し、主要部品がウィンドウ内に面積を持つこととバインディングエラーが無いことを確認して、PNGを自作fixtureへ保存する (実装担当の画面確認用。画像差分の判定はしない)。ダイアログも同様に描画する。

[AnalysisQueueTests](../tests/Unextract.Gui.Tests/AnalysisQueueTests.cs)は、模擬runnerと受信器を通した実形式のJSONLで、逐次キュー (Archive順・Target登録順、同時実行1、失敗後の続行、表示フィルタ非依存、Archive/選択/個別の対象範囲)、正常完了だけの採用 (途中entry・終了コード不一致・非互換・未知コードは未採用でDisplayTextの表示変換を通す)、再解析開始時のスナップショット退役と失敗・キャンセルでの非復活、キャンセル後の後続未開始、実行中の設定ロックと閲覧・フィルタの継続、Target不存在の再観測と未実行、モード変更の確認と全結果破棄 (他モードの候補を再利用しない)、64bit lengthの合計、空ZIP・全DIRECTORY、run.selected/受信件数/Target位置の進捗、分類・パスの絞り込みと選択の独立、runnerの拒否によるキュー停止を確認する。同梱の実CLIでも、自作ZIP・targetをStrict/Fastで解析してスナップショットを採用し、ファイルのパス・サイズ・SHA-256・更新日時の不変を確認する。30万entryの一覧がフィルタ後のindexだけを保持し行を作らないこともここで確認する。[AnalysisViewTests](../tests/Unextract.Gui.Tests/AnalysisViewTests.cs)は、STA上のWPFツリーで、閲覧中のTargetの詳細欄の結果一覧 (ウィンドウ内で使える高さを持ち、仮想化され、別の一覧に入れ子にならない)、分類・パス絞り込みとTargetごとの絞り込み状態の保持、Fast警告の表示、解析中だけの進捗とキャンセルをバインディングエラーなしで確認する。大量entryの応答時間・メモリは性能受入で測る。

[DeletionPreparationTests](../tests/Unextract.Gui.Tests/DeletionPreparationTests.cs)は、削除計画の対象固定 (選択中・現モードの正常解析・候補のみ、表示フィルタ非依存、除外と理由、候補0件・不存在・削除実行済みの除外)、確認文の項目 (Target数・最大候補件数・論理サイズ・完全削除・取り消せないこと・Fastの両非保証、削除するTargetの全件一覧、表示フィルタで隠れた選択済みTargetの印と件数、除外の全件)、承認が無い場合にCLI・entries・ログへ触れないこと、承認後の計画の陳腐化 (再解析・モード変更) の拒否を確認する。起動では、渡すモード・絶対パス・entries・ログ、ZIP順の生のFullName、1回の一括内で共通の開始時刻と通し番号、プロセス終了後の所有entriesだけの後始末、確実な未起動・準備失敗 (ログ場所・entries作成失敗・UTF-8化不能) での解析保持と再実行、起動後のエラー終了でのdelete実行済み化と後続停止、後始末失敗が結果を変えないこと、終了未確認・起動成否不明でのentries残置とdelete実行済み化、runnerの同期的な拒否だけを未起動とし実行中の想定外の例外は起動成否不明 (結果不明・entries残置・後続停止) とすること、終了要求でKillせず後続を始めないことを模擬runnerで検証する。直前のdelete結果1件が再解析 (成功・失敗) とモード変更で残り、次に起動したdeleteでだけ置き換わること、後続の未実行・確実な未起動・準備失敗は直近の一括削除の状態として別に表示され直前のdelete結果とログパスを置き換えないこと (後始末の通知もその状態に付くこと)、消費済みスナップショットがセッションの削除候補合計から外れることもここで確認する。entriesのUTF-8実バイト量が上限ちょうどなら許可され、1バイト超過ならCLIを起動せず未開始として後続を止めることもここで確認する。実[EntriesStore](../src/Unextract.Gui/Services/EntriesStore.cs)は、BOMなしUTF-8・LF・生の大小文字/区切り/不可視文字、一意名、所有外ファイルの非削除、共有拒否時の削除失敗通知、部分書込後の後始末を自作fixtureで確認する。実[LogLocation](../src/Unextract.Gui/Services/LogLocation.cs)は、開始時刻+通し番号の名前、同名ログがあるときのGUID付き別名、ファイルを先に作らないこと、作成不能な保存場所を確認する。同梱の実CLIでは、自作ZIP・targetに対し、解析後に一致するようになった非候補を触れずに候補だけを削除し、一時entriesが残らず命名どおりのログが書かれることを確認する (自作fixtureの実削除)。

[DeleteReportTests](../tests/Unextract.Gui.Tests/DeleteReportTests.cs)は、受信できた範囲からの削除結果の表示を確認する。正常終了の削除成功件数と64bitを含むDELETED length合計、`DELETE_FAILED`のみの終了1、STOP (停止理由・possibly_deleted・表示変換)、`run`なしの削除0件、`run`のみ・先頭/中間/最後のentryでの途絶、index欠番を数値+1で扱わないこと、次の候補が無い場合に不明対象を作らないこと、`internal_error`の`deletion_started`の真偽による違い、解析後のZIPの並べ替え・エントリ数変化では1件を特定せず未受信の承認候補を結果不明とし、CLIがentry_nameで報告した対象だけを名指しすること、変化が見えない場合もCLIの報告を解析時順の推定より優先すること、承認していないentry_nameの結果不明、entry_nameの無い`result.error.possibly_deleted`の表示、非互換版・読めない行・重複index・stdout読取失敗・実終了未確認・承認範囲外の出力 (selected不一致・承認外の名前) の原因別の「結果不明」、runを受信していない場合のログ未確認、実終了コードとresultの食い違い、run.targetの差の表示のみを模擬出力で確認する。[DeletionPreparationTests](../tests/Unextract.Gui.Tests/DeletionPreparationTests.cs)は、これらのVM反映 (delete実行済み・スナップショット消費・後続の未実行・ログパス表示と作成未確認の明示・自動再試行や再解析をしないこと) を確認する。[AnalysisViewTests](../tests/Unextract.Gui.Tests/AnalysisViewTests.cs)は、実行中のdeleteへ閉じる要求を2回出してもKillも終了もせず、待機文言を出し、現在のTarget完了後に後続を始めず閉じることと、解析中の終了要求がキャンセルを経て閉じることを実ウィンドウで確認する。GUIクラッシュ・OS強制終了・電源断後の挙動は保証対象外で、テストしない。

[IntegrationTests](../tests/Unextract.Gui.Tests/IntegrationTests.cs)は、同梱の実CLIと自作GUID fixtureで、Fastの削除 (同サイズで内容が違うファイルは削除され、サイズが違うファイルは残る。内容の一致は保証しない)、解析後に変更されたファイルをStrictが残して残りだけ削除すること、同一Archiveの2 Targetが登録順で連番ログ (`-001`・`-002`、共通の開始時刻) を残すこと、親子Targetが重複して指す同じファイルの2回目をエラーにしないことを確認する。いずれも一時entriesは残らない。fixtureは保存し、テストからは削除しない。

[PerformanceTests](../tests/Unextract.Gui.Tests/PerformanceTests.cs)は、ZIPのエントリ数上限 (100,000件、[ZIP](spec/zip.md)) の模擬結果を使い、JSONL受信 (4 KiB単位)、10 Target分の採用・保持メモリ・絞り込み・削除計画の作成、STA上のWPFでの詳細の繰返し切替・スクロール・絞り込みの応答を測る。[LargeSessionPerformanceTests](../tests/Unextract.Gui.Tests/LargeSessionPerformanceTests.cs)は、10,000 Archive×3 Targetの作業一覧 (一括追加、初回表示、絞り込み、すべて選択/解除、個別とArchive単位の選択、詳細の切替、スクロール、モード変更)、解析の進捗を受信している間の詳細切替・絞り込み、自作fixtureの10,000 ZIPの再帰検索を測り、プロセスメモリを出力する。通常の選択・詳細切替・絞り込みは1秒以内を目標として判定し、検索・受信などは緩い上限とする。実測値はテスト出力へ書く (この環境の値であり、実データの性能保証や対応件数の上限ではない。メモリ値は同じテストホストで並行するテストを含む)。描画 (Opacity 0のウィンドウ) とレイアウトは測るが、実GPUでの描画・操作の体感は[手動確認](MANUAL_TESTS.md)の対象。削除は行わない。

[GuiDataRootTests](../tests/Unextract.Gui.Tests/GuiDataRootTests.cs)は、UI E2E専用の保存先切替 (環境変数 `UNEXTRACT_GUI_TEST_DATA_ROOT`、合成ルートだけが読む) の決定を確認する。未設定は従来の `%LOCALAPPDATA%\unextract` のまま、絶対パスの既存ディレクトリは `<root>\settings.json` と `<root>\logs`、空文字・相対パス・不存在・ファイルは通常の保存先へフォールバックせずエラー (起動中止) とする。これは利用者向け機能ではない。[SearchViewTests](../tests/Unextract.Gui.Tests/SearchViewTests.cs)は、Tab順が視覚順に沿うこと (入力欄 → 検索 → フォルダーを選択 → サブディレクトリ → モード (2つのラジオで1停止) → 絞り込み → 一括追加 → 選択の変更)、UI E2Eが操作する要素の自動化用IDと一覧の行・チェックボックスの名前、ダイアログの自動化用IDを確認する。

[UI E2E](../tests/Unextract.Gui.UiTests)は、FlaUI UIA3とxUnitで、配布形態のGUI (publishした `unextract-gui.exe`) を別プロセスとして起動し、UI Automation経由で操作・観測する。製品はFlaUIを参照しない。既存のSTA上のWPFテストを置き換えるものではない。範囲は次のとおり。

- Archive単位のまとめ選択 (作業一覧の行のチェック) と表示中の選択操作、閲覧 (詳細欄) と一括選択の独立 (絞り込みで隠れた閲覧対象、閲覧中のTargetの除去)、隠れた選択済みTargetの確認画面での印・件数と偽CLIの記録 (entriesに含まれる)、Target編集ロックと削除実行済みTargetの再解析まで続くロック、再検索の確認、Fast警告とモード変更の確認・Fastの両非保証の再表示、削除除外の理由と既定ボタン (Enter・Escで偽CLIが起動されない)、削除できるTargetが無いときの理由の一覧。
- 失敗・結果不明の画面 (偽CLIの異常出力で到達させる): 解析失敗の詳細、`DELETE_FAILED`での停止と後続の未実行、未知の版による結果不明とログの案内、出力の途絶による不完全な結果。
- キーボード操作 (Tab順が視覚順でウィンドウ自体に止まらないこと、モードは1停止位置で矢印でも同じ確認を経て変わること、[基本の流れ](spec/gui.md#quality)の全行程を明示承認までキーボードだけで行うこと、Target設定のEnter・Esc・不正テンプレート)。
- 主要部品がウィンドウ内にあること、状態別×寸法別の画面写真 (既定・最小・1920×1080の150%/200%相当の論理寸法。自作fixtureの `shots`、実装担当の画面確認用。差分比較はしない)、不可視文字・260文字を超えるパスの表示 (読取専用のテキスト欄の値も含む)、実行中終了 (解析のキャンセル、deleteは現在Targetだけ完了して後続を起動しない)、保存フォルダー操作 (保存先の案内、フォルダー選択ダイアログのEsc)。

- 実CLIの正常系1本 (配布物と自作GUID fixtureで、検索 → Strict解析 → 確認 → 削除。MATCHEDだけが消え、命名どおりのログができ、一時entriesが残らない)。

各テストのGUIは、一意なfixtureをデータルート・`TMP`/`TEMP`・偽CLIシナリオにして起動する。全体の前後で利用者の `%LOCALAPPDATA%\unextract` が変わらないことを、読み取りだけで確認する。待機は期限付きの条件確認で、固定sleepは否定の確認の短い待ちだけに使う。入力は原則UIAのパターンで、実マウス・実キーはキーボード操作とクリックの確認 (モードのラジオボタンなど) に限り、効果が見えるまで再試行する。後始末はGUIを強制終了せず、偽CLIの解放ファイルを作ってから通常終了を要求し、終了しなければプロセスIDを報告して失敗とする。fixtureはテストから削除しない。

[偽CLI](../tests/Unextract.Gui.FakeCli)は、シナリオファイル (環境変数 `UNEXTRACT_FAKE_CLI_SCENARIO`) の応答をoperation・archive・targetで選び (`max_uses` あり)、JSONL v1の行をそのまま書く。解放ファイルを待つことで「実行中」を決定的に作り、受け取った引数とentriesの内容を記録する。ZIP・targetの読み書きと削除・改名は一切しない。`--log` が渡されたときは本物と同じく新規作成して同じ行を書く。シナリオ未指定、または一致する応答がなければ何も書かず終了コード3。配布物には入れない。[FakeCliTests](../tests/Unextract.Gui.UiTests/FakeCliTests.cs)は非UIで、偽CLIの正常系の出力 (Strict/Fastの解析、delete) が、同じ自作fixtureを同梱の実CLIで実行したJSONLと同じレコード (種別・フィールド・型・値) であることと、偽CLIの記録・解放待ち・ログを確認する。

UI E2Eはロックされていない対話デスクトップを必要とし、実行中は人がマウスとキーボードに触れない。最小化したリモートデスクトップ、サービスセッション、ほかのUIテストとの同時実行は対象外。通常の `dotnet test unextract.sln` では実行されず (`dotnet build unextract.sln` ではビルドされる)、次のスクリプトだけが実行する。スクリプトは配布物と偽CLI入りの複製をOS tempへ作り、パスを環境変数 (`UNEXTRACT_UI_GUI_PACKAGE`・`UNEXTRACT_UI_FAKE_PACKAGE`) で渡し、0件の実行を失敗とし、環境変数を復元して自作tempだけを後始末する。環境変数が無ければテストは失敗する (前提不成立にしない)。配布スクリプトを変えたときはUI E2Eも再実行する。UI E2Eの合格は、色・フォーカス枠・省略表示・DPIの目視確認や、CLIの安全性・実データでの性能の代替ではない。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-gui-ui-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-gui-ui-tests.ps1 -Filter "FullyQualifiedName~LockUiTests" -Detailed
```

```text
dotnet test tests/Unextract.Gui.Tests
dotnet test tests/Unextract.Gui.Tests -c Release
dotnet test tests/Unextract.Gui.Tests -c Release -r win-x64
```

[GUI配布スモーク](../scripts/run-gui-smoke-tests.ps1)は、日本語と空白を含むOS tempの新規配置先にpublishし、自己完結型・GUIのDLL依存・同梱CLIのJSONL起動と、同梱CLIあり/なしのGUI起動・通常終了を確認する。CLIへの呼び出しは引数エラーだけで、ZIP/targetには触れない。GUIを強制終了せず、所有する一時publishだけを、絶対パスとreparse不在を確認して後始末する。画面の説明表示は上記WPFテストが担当し、このスモークはプロセス起動の確認である。ジョブ実行とUI操作・性能の受入を代替しない。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-gui-smoke-tests.ps1
```

STA上でWPFのウィンドウを作るGui.Testsのクラス (AnalysisViewTests・DeploymentTests・LargeSessionPerformanceTests・PerformanceTests・ScreenRenderTests・SearchViewTests) は同じ `[Collection("Wpf")]` に入れて直列に実行する。xUnitがこれらを並列に実行したとき、WPFのリソース読み込みの競合と見られる散発的な失敗 (例: DeploymentTestsのCLI欠落時の表示) が再現し、直列化で再現しなくなった。競合の内部機構は特定していない。ウィンドウを作るテストクラスを追加するときは同じコレクションに入れる。

<a id="gui-review"></a>
### GUIを変えたときの画面確認

画面・文言・寸法・状態の表示を変えたら、機能テストに加えて、実装者が実際の画面を見て評価し、明らかに使いづらい点を直してから先へ進む。見た目・操作性の問題を人間の確認待ちとして残さない。人間の確認はその後の受入 ([M14](MANUAL_TESTS.md#m14)) である。

- 写真: UI E2Eの `LayoutUiTests` が配布物を操作して状態別×寸法別の写真を各fixtureの `shots` (`tests/Unextract.Gui.UiTests/bin/<構成>/net10.0-windows/fixtures/` 配下) へ保存し、STA描画の `ScreenRenderTests` も同様にGui.Testsのfixtureへ保存する。リポジトリに入れず、掃除は `clean-test-fixtures.ps1` による。写真を開いて確認し、写真で判断できない操作感 (スクロール、フォーカスの移動、閲覧の切り替え) は配布物を自作fixtureで実際に操作する。画像差分の自動判定はしない。
- 状態 (画面構成を変えたらその構成で同じ状態を撮る): 初期 (未検索)、検索0件・検索の通知、Archive多数、Archive・Targetの閲覧、Target設定 (各プリセット・不正テンプレート・一括追加)、解析の準備中・進捗、解析結果 (Strict・Fast、分類・パスの絞り込み)、解析失敗・キャンセル、モード変更の確認、Fast警告、削除確認 (除外あり・全Target除外・隠れた選択・Fast)、削除中・終了待機、削除後 (成功・`DELETE_FAILED`・未実行・途絶・結果不明)、再検索の確認、通知の展開、長いパス・不可視文字。
- 寸法: 既定、最小、1920×1080の画面を150%・200%で使うときの作業領域に相当する論理寸法。OSの表示拡大率は実装者が変更しない (利用者環境の設定変更になる)。実際の拡大率とモニター間の移動はM14で扱う。
- 評価観点 ([利用品質](spec/gui.md#quality)の具体化): (1) 流れ — 基本の流れを説明なしで完走でき、次の操作と操作できない理由が分かる。(2) 情報 — 各Targetの状態 (未解析・解析中・解析済み・失敗・キャンセル・delete実行済み・結果不明)、候補件数・候補サイズが見分けられ、削除対象・除外・モードを取り違えず、Fastの非保証が埋もれない。(3) レイアウト — 主要な情報と操作が過度なスクロールなしに見え、入れ子のスクロールで結果が隠れず、文字欠け・重なり・ボタンの切れ・不要な横スクロールが無く、長いパスの全文を確認できる。(4) 操作 — 関連する操作がまとまり、ラベルが動作を表し、削除を他の操作と取り違えにくく、キーボードだけで完走でき、Tab順が視覚順でウィンドウ自体に止まらず、フォーカス枠が見え、既定の操作は安全側。(5) 文言 — 用語とCLIの分類名が画面間で揃い、エラー・通知が原因と次の行動を示す。
- 指摘の扱い: 重大 (削除対象・結果・モードを誤認し得る、操作を完了できない、必要な情報が見えない) と中 (明らかに使いづらい、上の観点を満たさない) は直して0件にし、影響する全状態を撮り直す。軽微 (好みの範囲) は直すか、残す理由を記録する (現行で意図して残した点は[画面構成の判断](RATIONALE.md#gui-layout))。契約を変えないと解決できない指摘は製品判断として報告する。
- 回帰: 構造を変えたらUI E2Eの観測点 (自動化ID・名前・操作手順) を新しい構成へ移してよいが、上のUI E2Eの範囲の各要件を観測し続け、件数ではなく要件の網羅で判定する。状態・安全性の期待値を緩めず、Skipで通さない。状態・受信・大量表示を変えたら性能試験も実行する。最終版ではsolution全体、性能試験、UI E2E全件の連続3回成功、publish版E2E、GUI配布スモークを確認する。

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

J系列 ([機械可読出力](spec/machine-output.md)) は引数・診断・close後通知をJ01〜J07、シリアライズ・配送・実共有・レコード対応をJ08〜J11、CLI接続・例外境界と確定的な失敗注入をJ12〜J13で確認する。通常/publish exeのraw byte・JSON→entries・実NTFSでの削除とログ保持はJ14〜J18、実パイプ消失はJ19が担う。`outcome`と`exit_code`の全組合せ、`--jsonl`なしの出力不変 (既存O/K/X)、`run`前の削除0件、検出した配送失敗後の非継続は、実パイプが失敗を報告するかという観測から独立して確認する。観測結果は[未確認一覧](OPEN_ISSUES.md#observations)へ記す。

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

E2E自身はpublishせず、UNEXTRACT_E2E_EXEがあればそれ、なければ通常build出力exeを使う。見つからなければ失敗で、前提不成立にしない。stdin/stdout/stderrをリダイレクトし、人間向け出力はUTF-8で読む。機械出力の[MachineProcess](../tests/Unextract.E2E.Tests/MachineProcess.cs)はStandardOutput.BaseStreamを読んでASCII/LFを検証し、stdinを開いたままにして入力待ちなしの終了も確認する。J18/J19の同期はレコードのLFを条件にし、sleepだけで再現を決めない。60秒timeoutでprocess treeを終了させ失敗とする。fixtureは出力先fixturesのテスト名+GUIDで、起動前のDeletionGuardも働かせる。

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
- 機械可読出力の完了境界 (`run`前は削除0件、書けなければ先へ進まない)、機械モードの`--yes`必須、`--jsonl`なしの不変性、実行ログとstdoutの一致を確認する。
- 未確認・前提不成立・手順未確立・対象外を区別し、ファイルsymlink/placeholderを成立とみなさない。READMEの要約を契約・限界と同期する。

文書・コメントだけの変更では破壊を伴う実測や全製品テストを新規実行する必要はない。

### 自動テストで代替しない現行要求

L18はWindows PowerShell 5.1の `>` と `Out-File -Encoding utf8` の保存形式を実測する。Get-Content/Set-Contentで形式を作り替えず先頭バイトを記録し、UTF-16ならL02の拒否・UTF-8案内、BOM付きUTF-8ならL01の受理を確認する。native出力の文字化けも記録する。[M13](MANUAL_TESTS.md#m13)は手順レビュー待ちで、L02/X21のBOM拒否をもって完了とはしない。

S26/S34/S37/S38は観測ケースであり、現在の限定付き結果は[OPEN_ISSUES](OPEN_ISSUES.md#observations)にある。S26は改名成功なら最終パス不一致STOP/非削除、失敗なら通常処理を確認する。S34はDELETED/STOP、DeletePending・終了状態・対象以外の非削除を記録する。S37は成功なら属性SKIPPED、open失敗なら識別確認のDELETE_FAILEDで、いずれも非削除を確認する。S38は属性/tagごとに一致/不一致を記録し、相違があれば事前判定の説明を検討する。未観測を推定でassertの期待値にしない。

M09/M10/M11/M12は必須の確認事項に残す。M09は同時open調査 (設計非依存)、M10は中断後の表示と実削除、M11はメモリマップ経由の書き込み、M12(a)/(b)は近似の祖先改名/read-only open。M10の許容とM11の再現方法は未確立で、製品保証を追加しない。M01〜M08の実端末手順もPTY/E2Eだけでは代替しない。
