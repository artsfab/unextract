# 検証案内

役割: 検証原則、変更別のテストコード案内、重要回帰・再現上の知識と受入条件。テスト変更・実行時に該当節を読む。

[原則・安全](#principles) / [標準の検証](#verify) / [fixture](#fixtures) / [系列索引](#test-series) / [GUI](#gui) / [フック](#hooks) / [回帰](#regressions) / [再現](#reproduction) / [RAR](#rar) / [E2E](#e2e) / [受入](#acceptance)

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


実削除を伴う Win・E2E テストは、テストが自分で作った一意な fixture ディレクトリの中のファイルだけを削除する。削除指示の直前のフック (H5) で、テスト側のガード (`DeletionGuard`) が削除用ハンドルの最終パスが fixture の内側であること (`\` 境界付きの比較) と、fixture から対象の親までの各ディレクトリが reparse point でないことを確かめ、違反なら例外で中止する。このガードはテストの安全装置であり、製品の安全装置の代わりにしない。ACL を変えるテストは [AclChanges](../tests/Unextract.Windows.Tests/AclChanges.cs) の `Run` で元に戻し、自分の SID の DENY が残っていないことをテストの中で確かめる (戻せない・残っていればテストの失敗。本体の失敗も保持する)。fixture はテストの終了後に共通の削除処理が削除する ([fixture](#fixtures))。

<a id="verify"></a>
## 標準の検証

引き渡しの合否は [verify.ps1](../scripts/verify.ps1) の1回の実行で判定する。実行者がテストを選んだり省略したりする運用は使わず、毎回次の段をこの順ですべて実行する (段を選ぶ引数は無い)。

1. Release ビルド (警告ゼロ)
2. solution のテスト (`dotnet test unextract.sln -c Release`。この間は `UNEXTRACT_E2E_EXE` を解除し、現在の Release build の exe を使う)
3. 一時 publish と publish 版 E2E ([run-e2e-tests.ps1](../scripts/run-e2e-tests.ps1))
4. GUI の publish とスモーク ([run-gui-smoke-tests.ps1](../scripts/run-gui-smoke-tests.ps1))
5. UI E2E の全件 ([run-gui-ui-tests.ps1](../scripts/run-gui-ui-tests.ps1))。開始前に、セッションが対話的で入力デスクトップ (`Default`) を開けることを確かめ、満たさなければ不合格にする。ロックしない、実行中はマウスとキーボードに触れない、リモートデスクトップを最小化しない、サービスセッションで実行しない、ほかの UI テストと同時に実行しない、が前提である。

- 合否: 全段と結果の集約が成功したときだけ `PASSED`。テスト段は、期待するアセンブリごとの TRX があり、実行件数が0でなく、全件が成功し、テストホストが中断していないことを確かめる。ビルドとスモークは終了コードで判定する。ある段が失敗したら後続の段は実行せず (UI E2E は最後なので、先の段の失敗ではデスクトップを占有しない)、実行済みの段の結果・止まった段・未実行の段を `summary.txt` に残す。対話デスクトップ・UnRAR.dll の不足、テストホストの中断、TRX の欠落は不合格である。
- 未観測: 環境に依存する次の観測だけは、前提が成り立たないときに「前提不成立: ...」を出力して確認を行わずに終わる。`verify.ps1` はこれをテスト名・モード・理由つきで「未観測」として別に数え、その性質の成功には数えない。ほかが成功なら判定は `PASSED (not observed: N)` である。対象は `DirectoryEnumeratorTests` の8.3名、T06の8.3名、T08の圧縮・sparse属性、T15のcase-sensitive設定に固定し ([test-results.ps1](../scripts/test-results.ps1))、それ以外の前提不成立は不合格にする。OS・ボリューム全体の設定変更、常時の昇格、専用ボリュームは要求しない。
- 結果: リポジトリ外の `%TEMP%\unextract-verify\<checkout識別子>\latest` (識別子は正規化した checkout のパスから決める) に、`summary.txt` (段ごとの判定・所要時間・アセンブリごとの件数・使った exe の絶対パス・未観測・問題) と、段・アセンブリ別の TRX と診断を置く。開始時に、この保存先に対応する名前付き Mutex を取得してから保存先だけを空にし、集約まで保持する。使用中なら既存の結果を変えずに非0で終える。保存先とその経路の reparse point は拒否する。`run-gui-ui-tests.ps1` を単独で実行したときは同じ親の `ui-debug-latest` を使い、標準検証の結果を上書きしない。
- 構成は Release に統一する。開発中の `dotnet test` (Debug) と、失敗したテストだけの再実行 (`dotnet test --filter`、`run-gui-ui-tests.ps1 -Filter`) はデバッグの手段として使ってよいが、合否は最後に `verify.ps1` を全段で実行して判定する。
- CI ([workflow](../.github/workflows/ci.yml)) は `verify.ps1 -Ci` を実行する。`-Ci` は UI E2E だけを除き (結果に「excluded in CI」と出す)、`GITHUB_ACTIONS=true` のときしか受け付けない。結果は成功・失敗を問わず成果物として保存する。
- 文書・コメントだけの変更では、リンク先のアンカーと不可視文字だけを確かめればよい ([受入条件](#acceptance))。

<a id="fixtures"></a>
## fixture の作成と削除

- 作成: テストが使うディスク上の作業ディレクトリは、共通の補助 [TestFixtures](../tests/Unextract.Core.Tests/Fixtures/TestFixtures.cs) (各テストプロジェクトへ `Compile Include ... Link` で取り込む) だけで作る。場所は `<テストの出力先>\fixtures\<テスト名>-<32桁の16進>` で、実行中のテストの所有一覧に登録する。作成前に、ドライブのルートから fixtures ルートまでの経路が通常のディレクトリ (reparse point でない) であることを確かめる。所有者のいない呼び出し (テストメソッドの外) は拒否する。`fixtures` 直下に直接書く補助や、登録を経由しない作成を新設しない。
- 削除の契機は2つだけである。(a) 既定は、アセンブリ単位の `BeforeAfterTestAttribute` (`FixtureCleanup`) が、テストの終了後に成功・失敗を問わず削除する (xUnit の順序は Before → テスト → After → テストクラスの Dispose)。(b) テストメソッドより寿命の長い資源は、明示的な所有者 (`FixtureOwner`) を持つ側が削除する。UI E2E の `UiTestBase` は GUI の終了 (必要なら強制終了) の後に、`RarE2ETests` の DLL を置いた配置はクラスの全テストの後 (class fixture の Dispose) に削除する。終了を確認できない GUI の fixture は削除せず、パスを報告する。
- 削除処理 (1か所): 登録した絶対パスだけを、fixtures ルートの直下・`<接頭辞>-<32桁の16進>` の名前・それ自体と上位の経路に reparse point が無いことを削除の直前に確かめてから消す。fixtures ルート自体は消さない。reparse point の中へ入らずに ReadOnly 属性を外し (リンクはリンクだけを外す)、`Directory.Delete(recursive)` で削除する。ACL は変えない。一時的な共有違反は合計2秒まで再試行する。削除後に存在しないことを確かめ (アクセス拒否を不存在と扱わない)、残ればパスと原因を示してテストを失敗にする。テスト本体の失敗も保持し、残りの所有パスの削除も試みる。安全性は [TestFixturesTests](../tests/Unextract.Windows.Tests/TestFixturesTests.cs) (junction の先・ルートや上位の junction・未登録のパス・ルート自体・DENY・別の所有者・属性/長いパス/不可視文字) が常に確かめる。
- 残存プロセス・ACL: 起動した exe・PTY・ヘルパー・GUI はテストの中で終了させ、ACL は `AclChanges.Run` で戻してから削除に進む。STA のテスト ([SearchViewTests](../tests/Unextract.Gui.Tests/SearchViewTests.cs) の `OnSta`) は、処理・Dispatcher・STA スレッドの終了を待ってから完了する。ハングは Gui.Tests の既定の runsettings ([gui.runsettings](../tests/Unextract.Gui.Tests/gui.runsettings)) の Blame (2分・mini dump) が testhost ごと止め、実行は中断として不合格になる。
- 失敗時の診断は fixture に頼らない。判定に使った状態は assert のメッセージとテスト出力に含め、fixture の中にしか無い情報 (偽 CLI の記録・GUI のログ) は削除の前に結果ディレクトリへ書く (UI E2E)。画面写真は fixture の外に保存する。
- 回収: テストホストの異常終了 (ハング・クラッシュ) では削除が行われないことがある。残ったものは [clean-test-fixtures.ps1](../scripts/clean-test-fixtures.ps1) で回収する (既定は一覧のみ、`-Execute` で実行。稼働中のテストと同時に実行しない)。次の実行の開始時の一括削除、失敗時の保持、再利用・キャッシュは行わない。並行する別の実行の fixture には触れない (自分の登録パスだけを扱う)。

<a id="test-series"></a>
## 変更別の系列とコード

Coreは副作用・API失敗を注入した判定、Winは実NTFS/Win32、CLIは同一プロセス、E2Eはexeの別プロセス、PTYは対話機能、Mは実端末を担う。各テストクラスが確かめる内容はクラス冒頭のコメントに書き、本書には複製しない。単純ケースの入力・期待値は現行メソッドを読む。テスト名/ID/assertは文書再編で変更しない。CoreだけではWindowsの同一個体保証は証明できない。

| 変更領域 | 系列 | 担当コードと追加先 |
|---|---|---|
| Prepare・削除0件 | P | [CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[InputIntegrationTests](../tests/Unextract.Windows.Tests/Integration/InputIntegrationTests.cs) |
| ZIP内容・CRC | C | [ContentVerificationTests](../tests/Unextract.Core.Tests/ContentVerificationTests.cs)、[Win分類](../tests/Unextract.Windows.Tests/Integration/ClassificationIntegrationTests.cs) |
| 内容検証器のチャンクの契約 | C16 | [ContentVerifierTests](../tests/Unextract.Core.Tests/ContentVerifierTests.cs) は読み方 (ZIPのpull・RARの押し込み) と独立に、64 KiB超・4 MiB・1バイトずつ・宣言サイズをまたぐチャンク、targetの短いread、空、中止後の呼び出し、実測累計を同じ判定にする |
| RAR (Core) | U01〜U03、U10〜U22 | [ArchiveFormatTests](../tests/Unextract.Core.Tests/ArchiveFormatTests.cs)、[EntryBudgetTests](../tests/Unextract.Core.Tests/EntryBudgetTests.cs)、[RarLibraryTextTests](../tests/Unextract.Core.Tests/RarLibraryTextTests.cs)、[RarCommandTests](../tests/Unextract.Core.Tests/RarCommandTests.cs)。DLLの代わりに台本で動く偽のソース・セッション ([FakeRar](../tests/Unextract.Core.Tests/Fakes/FakeRar.cs)) で、Prepareの手順2/6/10、受理規則の全コード、前進とdeleteの保留ディレクトリ、押し込み型の内容検証、形式名、閉じる順と誤配線を確かめる |
| RAR (Windows・CLI) | U30〜U50、J11 | [RarSourceTests](../tests/Unextract.Windows.Tests/Rar/RarSourceTests.cs) は製品のソース・セッション・コールバックを台本で動く偽のDLL ([FakeUnrarApi](../tests/Unextract.Windows.Tests/Rar/FakeUnrarApi.cs)) で動かす (署名、`RAR_OM_LIST_INCSPLIT`と最終パス、ボリューム、列挙中の上限、ヘッダーの各項目の照合 (種類がCRC-32以外の `FileCRC` は照合しない。BLAKE2は `Hash` を照合し、CRC-32の期待値を内容検証に渡さない)、SKIPの回数、チャンク・中止・例外の再送出、closeの失敗、`RAR_SKIP`/`RAR_TEST`以外を使わないことの静的検査)。[UnrarLibraryTests](../tests/Unextract.Windows.Tests/Rar/UnrarLibraryTests.cs) はDLLを使わずに作れる原因 (不在・SHA-256不一致・非PEのロード失敗)。[RarCliTests](../tests/Unextract.Cli.Tests/RarCliTests.cs)・[RarMachineRecordTests](../tests/Unextract.Cli.Tests/RarMachineRecordTests.cs) はCLIの組立て、DLLのFATALの人間向け・機械出力、ZIPの実行がRARを開く関数を呼ばないこと |
| RAR (採用版のDLL) | U51〜U54、U60〜U68、U70〜U73 | 採用版のDLL ([DLLの用意](#rar)) と生成器のRARで製品の経路を通す。[RealUnrarTests](../tests/Unextract.Windows.Tests/Rar/RealUnrarTests.cs) は成功時の版・版とSHA-256の不一致、RAR5/RAR4の一覧 (Unix名の `\`・`:` の変換を含む)、セッションの往復 (5 MiB超・空のエントリ)、中止後のclose、コールバックの例外の再送出、DLLのCRC不一致。[RarIntegrationTests](../tests/Unextract.Windows.Tests/Integration/RarIntegrationTests.cs) は実NTFSで、生成器で作れる受理・拒否を両操作・両モードで、analyzeの非破壊とdelete (MODIFIED・MISSING・ADS・ディレクトリの通過)、target内のRAR自身、`--entries` の選択外の通過、Strictの内容検証のSTOPとFastの削除、非候補の破損を読まないこと、切り詰めと1ビット破壊の全位置、DLLが何も書かないこと (先頭がハッシュの無いディレクトリのRARで、両モードのanalyzeとdeleteの成功と分類も確かめる)。[RarE2ETests](../tests/Unextract.E2E.Tests/RarE2ETests.cs) はexe (publish版を含む) で、DLLを置かない配置でのRARのFATAL (削除0件、target・entries不変) と同じexeのZIPの成功、build・publish出力にDLLが無いこと、DLLを置いた配置のanalyze、JSONLの `name` → entries → deleteとログのバイト一致、作業ディレクトリへの無書き込み。GUIの経路は[GUI](#gui)のIntegrationTests |
| 名前・種別・構造・runtime形式 | Z | [EntryPathTests](../tests/Unextract.Core.Tests/EntryPathTests.cs)、[EntryTypeTests](../tests/Unextract.Core.Tests/EntryTypeTests.cs)、[StructureTests](../tests/Unextract.Core.Tests/StructureTests.cs)、[ZipArchiveSourceTests](../tests/Unextract.Core.Tests/ZipArchiveSourceTests.cs) |
| 上限 | R | [LimitsTests](../tests/Unextract.Core.Tests/LimitsTests.cs)、[Win入力](../tests/Unextract.Windows.Tests/Integration/InputIntegrationTests.cs) |
| target・属性・実名 | T | [ClassificationTests](../tests/Unextract.Core.Tests/ClassificationTests.cs)、[FileAttributeRulesTests](../tests/Unextract.Core.Tests/FileAttributeRulesTests.cs)、[Win分類](../tests/Unextract.Windows.Tests/Integration/ClassificationIntegrationTests.cs)、[TargetRootTests](../tests/Unextract.Core.Tests/TargetRootTests.cs) |
| analyze | A | [CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[ClassificationTests](../tests/Unextract.Core.Tests/ClassificationTests.cs)、[Win逐次](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs) |
| 削除・競合・エラー | S | [SequentialDeleteTests](../tests/Unextract.Core.Tests/SequentialDeleteTests.cs)、[Win逐次](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)、[DeletionHandleTests](../tests/Unextract.Windows.Tests/DeletionHandleTests.cs) |
| entries | L | [EntriesListTests](../tests/Unextract.Core.Tests/EntriesListTests.cs)、[CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[Win逐次](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs) |
| 引数 | K | [CommandLineParserTests](../tests/Unextract.Core.Tests/CommandLineParserTests.cs)、[CliApplicationTests](../tests/Unextract.Cli.Tests/CliApplicationTests.cs) |
| 機械モード引数・事前検出 | J01〜J03 | [CommandLineParserTests](../tests/Unextract.Core.Tests/CommandLineParserTests.cs) のJ01/J02、[MachineModeDetectionTests](../tests/Unextract.Cli.Tests/MachineModeDetectionTests.cs) のJ03 ([起動](spec/machine-output.md#invocation)) |
| Prepare診断の保持 | J04 / P / L | [PrepareDiagnosticTests](../tests/Unextract.Core.Tests/PrepareDiagnosticTests.cs)、CommandTestsのP11、EntriesListTestsのL02〜L09/L17 |
| 共通FS・内容検証の診断 | J05 / T / C / P / S | [HandleDiagnosticTests](../tests/Unextract.Core.Tests/HandleDiagnosticTests.cs)、ClassificationTests、ContentVerificationTests、TargetRootTests |
| delete固有の診断 | J06 / S / O15 | [DeleteDiagnosticTests](../tests/Unextract.Core.Tests/DeleteDiagnosticTests.cs)、SequentialDeleteTests、CommandTests |
| close後の同期通知・途中件数 | J07 / A / S / O | [CommandNotificationTests](../tests/Unextract.Core.Tests/CommandNotificationTests.cs)。人間向け出力の同じ順序はCommandTests |
| 表示・警告 | O | [AnalyzeOutputTests](../tests/Unextract.Core.Tests/AnalyzeOutputTests.cs)、[CommandTests](../tests/Unextract.Core.Tests/CommandTests.cs)、[SafeDisplayTests](../tests/Unextract.Core.Tests/SafeDisplayTests.cs)、[CliApplicationTests](../tests/Unextract.Cli.Tests/CliApplicationTests.cs) |
| ASCII/LFのシリアライズ | J08 | [MachineOutputTests](../tests/Unextract.Cli.Tests/MachineOutputTests.cs) |
| 同期配送・出力失敗先の非再利用 | J09 | [MachineOutputWriterTests](../tests/Unextract.Cli.Tests/MachineOutputWriterTests.cs) |
| ログの新規作成・実共有 | J10 | [ExecutionLogTests](../tests/Unextract.Cli.Tests/ExecutionLogTests.cs) |
| Core型からv1レコードへの対応 | J11 | [MachineRecordTests](../tests/Unextract.Cli.Tests/MachineRecordTests.cs) |
| CLIの機械経路・ログ所有・終端 | J12 | [MachineCliTests](../tests/Unextract.Cli.Tests/MachineCliTests.cs) |
| 配送失敗と完了境界の結合 | J13 | [MachineBoundaryTests](../tests/Unextract.Cli.Tests/MachineBoundaryTests.cs) |
| 機械出力のexe (通常build・publish版)、実行中のログ保持、実パイプ切断の観測 | J14〜J19 | [MachineOutputE2ETests](../tests/Unextract.E2E.Tests/MachineOutputE2ETests.cs)。J19の観測条件は[OPEN_ISSUES](OPEN_ISSUES.md#observations) |
| exe・対話 | X | [E2ETests](../tests/Unextract.E2E.Tests/E2ETests.cs)、[PtyConfirmationTests](../tests/Unextract.E2E.Tests/PtyConfirmationTests.cs) (X28・X29)、[PtyConsoleTests](../tests/Unextract.E2E.Tests/PtyConsoleTests.cs) (X30・X31)、[InterruptE2ETests](../tests/Unextract.E2E.Tests/InterruptE2ETests.cs) (X32) |
| 実端末の見え方・GUI受入・DLL導入 | M05・M08・M14・M15 | [手動手順](MANUAL_TESTS.md)、[唯一の状態更新先](OPEN_ISSUES.md#manual-status) |

### 操作・モード適用の読み方

A05はanalyzeへの--yes/-y/--entries/--dry-run拒否をK系と同じ条件で確認し、targetに触れないことを確認する。C/Tは通常analyze、deleteの同じ状況はSで確認する。P/Z/R01〜R06は両操作のPrepareを確認する。上の原則に指定した共通安全性ケースを同じfixtureでFastでも実施する。H3はStrictだけなので、Fastへ単純に比較中の変更を適用しない。Fastの安全性変更は[FS](spec/filesystem.md#delete-flow)、分岐配置は[実装案内](ARCHITECTURE.md#shared-path)も読む。

<a id="gui"></a>
## GUIの配置・検索・Target管理・プロセス寿命の検証

契約は[GUI仕様](spec/gui.md)、配置は[ARCHITECTURE](ARCHITECTURE.md#gui)。画面を変えたときの確認は[下の節](#gui-review)、人間の受入は[M14](MANUAL_TESTS.md#m14)。

[Gui.Tests](../tests/Unextract.Gui.Tests) は、VM・サービスを模擬のrunner・偽CLI・実形式のJSONLで、WPFの表示をSTA上の実ツリーで、CLIとの結合を同梱の実CLIと自作fixtureで確かめる。各クラスの確認内容はクラス冒頭のコメントにある。

| 領域 | クラス |
|---|---|
| 配布の構成 (同梱CLI・依存・UnRAR.dllを同梱しないこと) | [DeploymentTests](../tests/Unextract.Gui.Tests/DeploymentTests.cs) |
| JSONLの受信・CLIプロセスの寿命 | [JsonlReceiverTests](../tests/Unextract.Gui.Tests/JsonlReceiverTests.cs)、[CliProcessRunnerTests](../tests/Unextract.Gui.Tests/CliProcessRunnerTests.cs) |
| 検索・Target・設定 | [ArchiveSearchTests](../tests/Unextract.Gui.Tests/ArchiveSearchTests.cs)、[TargetTemplateTests](../tests/Unextract.Gui.Tests/TargetTemplateTests.cs)、[SearchSessionTests](../tests/Unextract.Gui.Tests/SearchSessionTests.cs)、[SearchSettingsTests](../tests/Unextract.Gui.Tests/SearchSettingsTests.cs) |
| 作業一覧・閲覧・Tab順・自動化ID (STA) | [SearchViewTests](../tests/Unextract.Gui.Tests/SearchViewTests.cs)、[ViewStateTests](../tests/Unextract.Gui.Tests/ViewStateTests.cs) |
| 画面の配置制約の正本 (全状態×寸法とダイアログ、STA) | [ScreenRenderTests](../tests/Unextract.Gui.Tests/ScreenRenderTests.cs)。描画は毎回、PNGの保存は `UNEXTRACT_GUI_SHOTS` 指定時だけ ([画面確認](#gui-review)) |
| 解析キュー・結果表示・終了操作 | [AnalysisQueueTests](../tests/Unextract.Gui.Tests/AnalysisQueueTests.cs)、[AnalysisViewTests](../tests/Unextract.Gui.Tests/AnalysisViewTests.cs) |
| 削除計画・起動・結果表示 | [DeletionPreparationTests](../tests/Unextract.Gui.Tests/DeletionPreparationTests.cs)、[DeleteReportTests](../tests/Unextract.Gui.Tests/DeleteReportTests.cs) |
| 同梱の実CLIとの結合 (Fast・Strict・連番ログ・RAR) | [IntegrationTests](../tests/Unextract.Gui.Tests/IntegrationTests.cs) |
| 性能 (模擬データ。実測値はテスト出力) | [PerformanceTests](../tests/Unextract.Gui.Tests/PerformanceTests.cs)、[LargeSessionPerformanceTests](../tests/Unextract.Gui.Tests/LargeSessionPerformanceTests.cs) |
| UI E2E専用の保存先の切替 | [GuiDataRootTests](../tests/Unextract.Gui.Tests/GuiDataRootTests.cs) |

[UI E2E](../tests/Unextract.Gui.UiTests)は、FlaUI UIA3とxUnitで、配布形態のGUI (publishした `unextract-gui.exe`) を別プロセスとして起動し、UI Automation経由で操作・観測する。製品はFlaUIを参照しない。既存のSTA上のWPFテストを置き換えるものではない。範囲は次のとおり。

- Archive単位のまとめ選択 (作業一覧の行のチェック) と表示中の選択操作、閲覧 (詳細欄) と一括選択の独立 (絞り込みで隠れた閲覧対象、閲覧中のTargetの除去)、隠れた選択済みTargetの確認画面での印・件数と偽CLIの記録 (entriesに含まれる)、Target編集ロックと削除実行済みTargetの再解析まで続くロック、再検索の確認、Fast警告とモード変更の確認・Fastの両非保証の再表示、削除除外の理由と既定ボタン (Enter・Escで偽CLIが起動されない)、削除できるTargetが無いときの理由の一覧。
- 失敗・結果不明の画面 (偽CLIの異常出力で到達させる): 解析失敗の詳細、`DELETE_FAILED`での停止と後続の未実行、未知の版による結果不明とログの案内、出力の途絶による不完全な結果。
- キーボード操作 (Tab順が視覚順でウィンドウ自体に止まらないこと、モードは1停止位置で矢印でも同じ確認を経て変わること、[基本の流れ](spec/gui.md#quality)の全行程を明示承認までキーボードだけで行うこと、Target設定のEnter・Esc・不正テンプレート)。
- 起動時の寸法で主要部品がウィンドウ内にあること (寸法は変えない。ウィンドウのDPIを出力する。状態×寸法の配置は上のScreenRenderTests)、不可視文字・260文字を超えるパスの表示 (読取専用のテキスト欄の値も含む)、実行中終了 (解析のキャンセル、deleteは現在Targetだけ完了して後続を起動しない)、保存フォルダー操作 (保存先の案内、フォルダー選択ダイアログのEsc)。

- 実CLIの正常系1本 (配布物と自作GUID fixtureで、検索 → Strict解析 → 確認 → 削除。MATCHEDだけが消え、命名どおりのログができ、一時entriesが残らない)。

各テストのGUIは、一意なfixtureをデータルート・`TMP`/`TEMP`・偽CLIシナリオにして起動する。全体の前後で利用者の `%LOCALAPPDATA%\unextract` が変わらないことを、読み取りだけで確認する。待機は期限付きの条件確認で、固定sleepは否定の確認の短い待ちだけに使う。入力は原則UIAのパターンで、実マウス・実キーはキーボード操作とクリックの確認 (モードのラジオボタンなど) に限り、効果が見えるまで再試行する。1秒を超えた待機は待った対象とともにテスト出力に書き、タイムアウトには待機中に起きた例外の種類と回数を含める。各テストは起動したGUIのfixtureと結果ディレクトリをテスト出力に書く。

合否と失敗時の診断: UI E2Eは全件1回の成功で合格とし、再試行で合格にせず、1回の失敗も不具合として扱う。テスト本体が失敗すると ([UiFact/UiTheory](../tests/Unextract.Gui.UiTests/UiFact.cs) が本体の直後に呼ぶ)、GUIを閉じる前に、結果ディレクトリ (`run-gui-ui-tests.ps1` が渡す `UNEXTRACT_UI_RESULTS`。[標準の検証](#verify)) の `diagnostics\<テスト名>-<id>\` へ、デスクトップ全体の画面、GUIプロセスの全トップレベルウィンドウのUIAの木、偽CLIのシナリオと呼び出し記録、GUIのデータルート (設定・ログ) を保存する。後始末は、偽CLIの解放ファイルを作ってから通常終了を要求する。終了しなければテストを失敗にしてプロセスIDを報告し、診断を保存してから、そのテストが起動したプロセスツリーだけを強制終了して後続のテストへの影響を断つ。診断には期限があり、取得に失敗しても強制終了は省かない。終了を確認できなければ、残ったfixtureのパスも報告する。

[偽CLI](../tests/Unextract.Gui.FakeCli)は、シナリオファイル (環境変数 `UNEXTRACT_FAKE_CLI_SCENARIO`) の応答をoperation・archive・targetで選び (`max_uses` あり)、JSONL v1の行をそのまま書く。解放ファイルを待つことで「実行中」を決定的に作り、受け取った引数とentriesの内容を記録する。ZIP・targetの読み書きと削除・改名は一切しない。`--log` が渡されたときは本物と同じく新規作成して同じ行を書く。シナリオ未指定、または一致する応答がなければ何も書かず終了コード3。配布物には入れない。[FakeCliTests](../tests/Unextract.Gui.UiTests/FakeCliTests.cs)は非UIで、偽CLIの正常系の出力 (Strict/Fastの解析、delete) が、同じ自作fixtureを同梱の実CLIで実行したJSONLと同じレコード (種別・フィールド・型・値) であることと、偽CLIの記録・解放待ち・ログを確認する。

UI E2Eは[標準の検証](#verify)の最後の段で、その前提 (ロックされていない対話デスクトップなど) に従う。通常の `dotnet test unextract.sln` では実行されず (`dotnet build unextract.sln` ではビルドされる)、次のスクリプトだけが実行する。スクリプトは配布物と偽CLI入りの複製をOS tempへ作り、パスを環境変数 (`UNEXTRACT_UI_GUI_PACKAGE`・`UNEXTRACT_UI_FAKE_PACKAGE`) で渡し、0件の実行を失敗とし、環境変数を復元して自作tempだけを後始末する。環境変数が無ければテストは失敗する (前提不成立にしない)。UI E2Eの合格は、色・フォーカス枠・省略表示・DPIの目視確認や、CLIの安全性・実データでの性能の代替ではない。

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

- 写真: STA描画の `ScreenRenderTests` (と `SearchViewTests` の作業一覧) が全状態×寸法とダイアログを撮る。環境変数 `UNEXTRACT_GUI_SHOTS` に fixture の外の絶対パスを指定して `dotnet test tests/Unextract.Gui.Tests --filter "FullyQualifiedName~ScreenRenderTests"` を実行すると、そこへPNGを保存する (通常の実行では描画だけを行い保存しない)。STAで描画できないOSのメッセージボックス (モード変更・再検索の確認) は `run-gui-ui-tests.ps1 -Shots` が既存のUI E2Eの中で結果ディレクトリの `shots` へ撮る。リポジトリに入れない。写真を開いて確認し、写真で判断できない操作感 (スクロール、フォーカスの移動、閲覧の切り替え) は配布物を自作fixtureで実際に操作する。画像差分の自動判定はしない。
- 状態 (画面構成を変えたらその構成で同じ状態を撮る): 初期 (未検索)、検索0件・検索の通知、Archive多数、Archive・Targetの閲覧、Target設定 (各プリセット・不正テンプレート・一括追加)、解析の準備中・進捗、解析結果 (Strict・Fast、分類・パスの絞り込み)、解析失敗・キャンセル、モード変更の確認、Fast警告、削除確認 (除外あり・全Target除外・隠れた選択・Fast)、削除中・終了待機、削除後 (成功・`DELETE_FAILED`・未実行・途絶・結果不明)、再検索の確認、通知の展開、長いパス・不可視文字。
- 寸法: 既定、最小、1920×1080の画面を150%・200%で使うときの作業領域に相当する論理寸法。OSの表示拡大率は実装者が変更しない (利用者環境の設定変更になる)。実際の拡大率とモニター間の移動はM14で扱う。
- 評価観点 ([利用品質](spec/gui.md#quality)の具体化): (1) 流れ — 基本の流れを説明なしで完走でき、次の操作と操作できない理由が分かる。(2) 情報 — 各Targetの状態 (未解析・解析中・解析済み・失敗・キャンセル・delete実行済み・結果不明)、候補件数・候補サイズが見分けられ、削除対象・除外・モードを取り違えず、Fastの非保証が埋もれない。(3) レイアウト — 主要な情報と操作が過度なスクロールなしに見え、入れ子のスクロールで結果が隠れず、文字欠け・重なり・ボタンの切れ・不要な横スクロールが無く、長いパスの全文を確認できる。(4) 操作 — 関連する操作がまとまり、ラベルが動作を表し、削除を他の操作と取り違えにくく、キーボードだけで完走でき、Tab順が視覚順でウィンドウ自体に止まらず、フォーカス枠が見え、既定の操作は安全側。(5) 文言 — 用語とCLIの分類名が画面間で揃い、エラー・通知が原因と次の行動を示す。
- 指摘の扱い: 重大 (削除対象・結果・モードを誤認し得る、操作を完了できない、必要な情報が見えない) と中 (明らかに使いづらい、上の観点を満たさない) は直して0件にし、影響する全状態を撮り直す。軽微 (好みの範囲) は直すか、残す理由を記録する (現行で意図して残した点は[画面構成の判断](RATIONALE.md#gui-layout))。契約を変えないと解決できない指摘は製品判断として報告する。
- 回帰: 構造を変えたらUI E2Eの観測点 (自動化ID・名前・操作手順) を新しい構成へ移してよいが、上のUI E2Eの範囲の各要件を観測し続け、件数ではなく要件の網羅で判定する。状態・安全性の期待値を緩めず、Skipで通さない。引き渡しは[標準の検証](#verify)の合格で判定する (性能試験・UI E2E全件・publish版E2E・GUI配布スモークを毎回含む。UI E2Eは全件1回の成功で合格とし、再試行で合格にしない)。

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

L系列はUTF-8/BOM、UTF-16/32 BOM、不正UTF-8、空/BOMのみ、LF/CRLF混在と末尾改行、行途中CR、trimなし、重複の両行番号、未知/大小文字/区切りヒント、dir/暗黙dir、コメント/glob無し、境界、最後の行の誤り、転記不可文字を確認する。入力のUTF-16拒否と、PowerShellが実際に保存する形式 (X31) は別である。

O/X系列はカテゴリー内ZIP順、0件カテゴリーの行省略と合計への掲載、モード別カテゴリー、FATALの判定済み/原因/未判定、stdout/stderr所属、非対話stdinにyを書いても中止、STOP前の部分削除、正常exe/publish exeの転記・日本語・CP437を確認する。生の危険文字をソースへ入れずescape fixtureで作る。

J系列 ([機械可読出力](spec/machine-output.md)) は引数・診断・close後通知をJ01〜J07、シリアライズ・配送・実共有・レコード対応をJ08〜J11、CLI接続・例外境界と確定的な失敗注入をJ12〜J13で確認する。通常/publish exeのraw byte・JSON→entries・実NTFSでの削除とログ保持はJ14〜J18、実パイプ消失はJ19が担う。`outcome`と`exit_code`の全組合せ、`--jsonl`なしの出力不変 (既存O/K/X)、`run`前の削除0件、検出した配送失敗後の非継続は、実パイプが失敗を報告するかという観測から独立して確認する。観測結果は[未確認一覧](OPEN_ISSUES.md#observations)へ記す。

<a id="reproduction"></a>
## 再現するときの知識

以下のE-2・PoC 6の原観測は `606d89c:docs/PLAN_VALIDATION.md` の同名見出しで参照できる。

- ACLは`AclChanges.Run`で復元し、DENYの残存はテストの失敗として検出する。親DACLを途中で変えると継承再適用で子・兄弟のChangeTimeが変わり、狙った対象より先にSTOPし得る。親DELETE_CHILD拒否は操作開始前に設定し、競合で変える対象を限定する。
- ADS追加側はDELETEを共有しなければ、製品の削除用ハンドルと共有違反になる。共有R/W/Dを使って、ADSによる最終確認の検出とopen拒否を混同しない。これらは2026-10-02 E-2 (Windows 11 10.0.26300、NTFS、.NET 10.0.12、同一プロセス別ハンドル) のfixture上の観測で、PoC 6には別プロセスの観測もある。
- メタデータ書き戻しは内容変更を隠せる。A08の現在内容検証でStrict MODIFIEDとFastの契約を確認し、ID/日時だけを一致の証拠にしない。
- Fast body非読取はCoreのRecordingContentProvider、ThrowOnRead、呼び出し記録でGetContent/Open/target readを直接確認する。CRC計算専用フックはないので、body非読取・CRC期待値非参照からの間接確認と区別する。Winの結果だけをbody/CRCの直接観測と呼ばない。
- 実測ではOS、SDK/runtime、NTFS、同一/別プロセス、フック位置、モード、実施できなかった前提を短く記録する。Coreで模擬できてもWinで再現不能なら未確認に残す。

<a id="rar"></a>
## RARの検証で引き継ぐ知識

RARの系列は[上表](#test-series)のC16とU。偽のソース・セッション・DLLによる試験は、実物のDLLによる試験を置き換えない。採用版のDLLを使う自動試験 (CIを含む) は生成器のRARで行う。WinRARで作った実物のRAR (圧縮・BLAKE2の実値・NTFSストリーム・実物のSolid・分割・SFX・暗号化・リンク) はローカル検証 ([下記](#rar-real)) で確かめ、自動試験には含めない。RAR4の実物は前提不成立 (RAR4を作れる旧版のWinRARが無い) で未確認 ([OPEN_ISSUES](OPEN_ISSUES.md#rar))。契約は[RAR仕様](spec/rar.md)、観測と判断は[理由](RATIONALE.md#rar)にある。

- 既存の原則 (analyzeの非破壊、Prepare失敗の削除0件、同じハンドル、モード違いの再利用、Skip・期待値緩和の禁止) をRARにもそのまま適用する。RARのPrepareの拒否 ([列挙](spec/rar.md#listing)) は両操作・両モードで同じFATAL・削除0件になることを確認する。ZIPの既存の系列はRAR対応の後も期待値を変えずに通す。
- UnRAR.dllが無い・照合できない環境でRARのテストを前提不成立や成功にしない (E2Eのexeと同じく失敗とする)。DLLが無くてもZIPの実行が影響を受けないこと (DLLを探さない) は別に確認する。
- DLLの用意: UnRAR.dllはリポジトリに置かない ([版の固定](spec/rar.md#pinning))。開発機・CIでは [get-unrar-dll.ps1](../scripts/get-unrar-dll.ps1) が rarlab の `unrardll-723.exe` を取得し (取得済みなら `-InstallerPath`)、外側と `x64\UnRAR64.dll` のSHA-256を照合して、リポジトリ外 (既定 `%LOCALAPPDATA%\unextract-dev\unrar-7.23\`) に置く。展開は7-ZipかWinRARの `UnRAR.exe` で行い、自己展開exeは実行しない。テストは環境変数 `UNEXTRACT_TEST_UNRAR_DLL` (DLLの絶対パス)、未設定なら既定の場所から読み ([UnrarTestDll](../tests/Unextract.Core.Tests/Fixtures/UnrarTestDll.cs))、版の照合は製品のローダーが行う。製品はこの環境変数を読まない。in-processの試験はテスト用のローダーにそのパスを渡す。exeの試験 (E2E・GUI) は、テスト対象のexeのフォルダーの直下のファイルをfixtureへ複製して隣にDLLを置く (利用者が置いた状態の再現)。元のbuild・publish出力にはDLLを置かず、DLLの無い配置として使う。
- 採用版のDLLを同じテストプロセスでロードするテストクラスは、xUnitのコレクション [UnrarDllCollection](../tests/Unextract.Windows.Tests/Rar/UnrarDllCollection.cs) に入れて直列に実行する。DLLのエラーの状態はプロセスで1つで、並列のハンドルの間で結果が漏れる ([理由](RATIONALE.md#rar-dll-usage))。製品の使い方 (1プロセスで同時に1つのハンドル) を再現するもので、判定を緩めるものではない。exeを子プロセスで起動する試験 (E2E・GUI) は対象外。
- 製品がDLLに `RAR_EXTRACT` と展開先を渡さないこと、作業ディレクトリとtargetにDLLが書き込まないことを試験で固定する。
- fixture: 異常系 (password checkの無い暗号化、作成元OS・属性、Unix名の `\`・`:`、redir、ハッシュ不一致、サイズ不明、辞書サイズの宣言、Solid・分割のフラグ、RAR4のUnicode名) とStoredの正常系は、テスト専用生成器 ([RarWriter](../tests/Unextract.Core.Tests/Fixtures/RarWriter.cs)。`ZipPatcher` と同じ位置づけで製品パーサを兼ねない) でテスト中に作る。生成器は圧縮・BLAKE2の実値・NTFSストリームを作れず、実物の試験を置き換えない。
- <a id="rar-real"></a>WinRARで作った実物のRAR (2026-10-09の方針): **ローカル検証専用**とし、リポジトリに収録せず、CIでも実行しない。再配布条件の確認は検証の前提にしない。WinRAR (試用期間内を含め、そのライセンス条件の範囲内) の `Rar.exe` で、自作データから検証のたびに作る。第三者のRARや実在のデータは使わない。
  - 手順: [verify-real-rar.ps1](../scripts/verify-real-rar.ps1) に、検証するexe (隣に採用版の `UnRAR64.dll` を置いたもの。publish版ならリポジトリ外の出力) を渡す。リポジトリ外の新しい作業フォルダー (既定は `%TEMP%` の下) に元データ・RAR・targetを作り、`--jsonl` で確かめる。作業フォルダーは掃除しない (削除はunextractが自作のtargetに対して行うものだけ)。終了コード0で全項目合格。
  - 範囲: RAR5の圧縮 (-m3/-m5)・Stored・BLAKE2 (-htb)・`-rr`/`-qo`/`-ts`・`-md1g` の受理 (Strictの全件MATCHED、FastのSAME_SIZE、1ビットの変更がMODIFIED)、Strictのdeleteが一致したファイルだけを消すこと (同サイズの変更・余分なファイル・フォルダーは残る)、Fastが同サイズの別内容を消すこと、日本語名の `--entries`、圧縮データの破損がMATCHEDにならず (CRC-32・BLAKE2) Strictで FATAL/STOP になること、`-os` (targetがADSを持てば `SKIPPED_SPECIAL_FILE`、持たなければStrictの `CONTENT_TOO_LONG`)、Solid・ヘッダー暗号化・ファイル暗号化・分割の全巻・SFX (`.rar` に改名)・`-oi`・`-oh` が両操作・両モードで同じFATALになりtargetに触れないこと。
  - 範囲外: RAR4の実物 (WinRAR 7.xは作れない。旧版が要る)、Unix作成のRAR、1 GiB超の辞書の実物 (WinRARはデータより大きい辞書を縮めて記録する)、`-ol` のsymlink (作成権限が要る)。
  - 実施する時: DLLの版、RARの読み取り・内容検証・受理規則、`verify-real-rar.ps1` を変えたとき。結果 (日付・WinRARの版・exe) は[OPEN_ISSUES](OPEN_ISSUES.md#rar-observations)に記録する。
- 誤解しやすい点: NTFSストリーム付き (`-os`) は、targetがADSを持てば `SKIPPED_SPECIAL_FILE`、持たなければStrictでFATAL/STOPになる (両方を作る)。分割は第1巻・途中の巻・最終巻のすべてでopen直後の `ARCHIVE_MULTI_VOLUME` (手順2) になり、`ENTRY_SPLIT` (手順6) は非分割アーカイブ内の分割フラグ (前・後の両方。生成器で作る) で確かめる。切り詰めは位置によって拒否・エントリの減少・全件のままに分かれるので、全位置を試す。非候補の破損 (1件目) を読まずに後続がMATCHEDになることを確かめる。判定が `RAR_TEST` の成功後に確定すること (データ一致でもDLLのハッシュ不一致ならFATAL/STOP) を確かめる。
- DLLの版を変えるとき (採用版・SHA-256の変更は[版の固定](spec/rar.md#pinning)の変更): 新しい版で、採用版のDLLを使う自動試験 (U51〜U54・U60〜U68・U70〜U73。生成器のRARでの受理・拒否、Unix名の変換、切り詰めと1ビット破壊の全位置を含む) と[実物のRAR](#rar-real)のローカル検証を実行する。加えて、それらが扱わない観測 ([DLLの使い方](RATIONALE.md#rar-dll-usage)の各項目 (最終パスでのopen、照合中のロード、中止後の挙動、チャンクの長さ、`UCM_LARGEDICT`、構造体の配置、単一ファイルpublishの配置)、WinRAR製の実物の全位置の切り詰め、名前の変換とWinRARの展開名の一致) を確かめ直す。当時の観測用PoCは残していないので、確かめ直すときは観測の内容と条件 ([理由](RATIONALE.md#rar)) をもとに、リポジトリ外・solution外の使い捨てのコードで行う (DLLは絶対パスでロードし、`RAR_TEST`・`RAR_SKIP` だけを使う)。切り詰めは、WinRARの `-m0` で自作データから作ったRAR5を全位置で切り詰め、拒否・エントリの減少・全件のままの内訳と例外が無いことを見る。名前の変換は、生成器でUnix名の `\`・`:` を持つRARを作り、DLLの列挙名とWinRARの `UnRAR.exe x` の展開名を比べる。fixtureは自作データだけで作り、第三者のRAR (SharpCompressのテスト用アーカイブなど) は使わない (RAR4は生成器か、旧版のWinRARで作る)。

<a id="e2e"></a>
## E2E・PTY・実行案内

通常のbuild/testコマンドは[ルートREADME](../README.md#ビルドとテスト)、CIの正本は[workflow](../.github/workflows/ci.yml)。dotnet runを使わずビルド済みexeを使う。publishはリポジトリ外へ出し、publish版E2E/PTYは[標準の検証](#verify)の段 ([run-e2e-tests.ps1](../scripts/run-e2e-tests.ps1)) が実行する。wrapperは自作tempへRelease/win-x64 publish、UNEXTRACT_E2E_EXE設定、E2E全体実行、環境変数復元・cleanupを行う。通常buildの全体検証と配布exe検証を区別する。

E2E自身はpublishせず、UNEXTRACT_E2E_EXEがあればそれ、なければ通常build出力exeを使う。見つからなければ失敗で、前提不成立にしない。stdin/stdout/stderrをリダイレクトし、人間向け出力はUTF-8で読む。機械出力の[MachineProcess](../tests/Unextract.E2E.Tests/MachineProcess.cs)はStandardOutput.BaseStreamを読んでASCII/LFを検証し、stdinを開いたままにして入力待ちなしの終了も確認する。J18/J19の同期はレコードのLFを条件にし、sleepだけで再現を決めない。60秒timeoutでprocess treeを終了させ失敗とする。fixtureは出力先fixturesのテスト名+GUIDで、起動前のDeletionGuardも働かせる。

PTYは同一端末にstdout/stderrが流れるため、所属はCore/CLIで別に確認する。進捗は結果行の前に (消去の空白・CRとともに) 残るので、結果行を読む前に除く (見え方はM05)。X28は警告順序とn中止、X29はEnter中止とyによる逐次削除、X30はコードページ932/65001での名前の表示と実行後のコードページの復元、X31はコードページ932のコンソールでのWindows PowerShell 5.1の保存形式 (`>`・`Out-File -Encoding utf8`) を担う。X31は保存形式と名前の文字化けを実測して記録し、UTF-16ならL02の拒否、それ以外なら「MATCHEDの2件だけを削除」か「入力エラーで削除0件」のどちらかという安全側の性質だけを判定する。字形・折り返し・視認性はM05/M08に残す。Porta.PtyはE2E専用依存で製品に追加しない。PTYはprocess tree終了・Dispose後にテストを終え、fixtureは共通の削除処理 ([fixture](#fixtures)) が削除する。PTYの後始末の失敗は黙殺せず、元の例外・terminal outputを保持する。wrapperも自作tempだけを片付け、外部exeや既存bin/objを削除しない。

X32は通常出力 (`delete --yes`) とJSONL (`delete --jsonl --yes --log`) × Strict/Fastで、逐次処理の途中のexeを強制終了し、[中断時の表示と実削除](spec/cli.md#interruption)の性質 (表示したDELETEDは削除済み、表示されない削除は最後の結果の次の1件だけでその候補に限る、JSONLは「stdout ⊆ ログ ⊆ 実削除」) を判定する。stdoutのパイプ容量を超える固定のfixture (一致する小さなファイル512件) で、最初の完全なDELETEDを受け取った後は強制終了まで読まないので、子は末尾のファイルまで進めない。判定はEOFまで回収した完全な行・レコードだけで行い、差が0件か1件かは記録するだけにする。Ctrl+Cの実入力ではなく、最終確認を省く退行は検出できない (S25などの最終確認のテストが担う)。

実端末は[MANUAL_TESTS](MANUAL_TESTS.md)、実施状態は[OPEN_ISSUES](OPEN_ISSUES.md#manual-status)。全製品テスト合格はM系や前提不成立を埋めない。

<a id="acceptance"></a>
## 受入条件

必須系列の全項目が通り、実測の条件付き結果と未確認の扱いが決まり、以下を確認した時点を製品のテスト完了とする。合格件数だけで完了を判断しない。

- analyzeは削除能力を持たず全件の分類・非破壊、FATALの判定済み/未判定を確認する。
- Prepare・entries入力エラー・確認中止で削除0件。旧CLIとdry-runは開始前に拒否する。
- deleteは1件ずつ1回open、同じハンドルで1回比較 (Strict)・M0全項目・指示・成立確認。選択外、特殊対象、ZIP自身、無関係ファイル、dirを削除しない。
- DELETE_FAILEDで非削除続行、未知エラーSTOP、親ID/最終パス/事前判定が働く。STOP前の削除は戻らず後続は未処理。指示後STOPは削除された可能性を保持する。強制終了時の表示と実削除は[中断時](spec/cli.md#interruption)のとおり。
- 共通安全性をFastでも確認し、同サイズ異内容の削除を仕様どおりと判定する。非読取と実測量非計上も確認する。
- 機械可読出力の完了境界 (`run`前は削除0件、書けなければ先へ進まない)、機械モードの`--yes`必須、`--jsonl`なしの不変性、実行ログとstdoutの一致を確認する。
- RARでも同じ受入条件を両操作・両モードで確認し、RAR固有の拒否 (Solid・分割・暗号化・SFX・リンク・辞書サイズ)、DLLが利用できないときのRARだけのFATAL (4つの原因ごとの説明と削除0件、target・entriesに触れないこと) とZIPへの無影響、Prepareの内容読み取り用openがデータを読まないこと、`RAR_EXTRACT` 不使用を確認する ([RAR](#rar))。
- 未確認・前提不成立・手順未確立・対象外を区別し、ファイルsymlink/placeholderを成立とみなさない。READMEの要約を契約・限界と同期する。

文書・コメントだけの変更では破壊を伴う実測や全製品テストを新規実行する必要はない。

### 観測ケースと手動の受入

S26/S34/S37/S38は観測ケースであり、現在の限定付き結果は[OPEN_ISSUES](OPEN_ISSUES.md#observations)にある。S26は親・祖父母・その上の祖先のそれぞれで、改名成功なら最終パス不一致STOP/非削除、失敗なら通常処理を確認する。S34はDELETED/STOP、DeletePending・終了状態・対象以外の非削除を記録する。S37は成功なら属性SKIPPED、open失敗なら識別確認のDELETE_FAILEDで、いずれも非削除を確認する。S38は属性/tagごとに一致/不一致を記録し、相違があれば事前判定の説明を検討する。未観測を推定でassertの期待値にしない。

手動の受入はM05・M08 (実端末での見え方)、M14 (GUI)、M15 (DLLの導入と実物のRAR) だけである ([手動に残す理由](MANUAL_TESTS.md#manual-purpose))。M09 (同時open) とM11 (書き込み可能なマップ) は受入から外した。M11の限界は[FS限界](spec/filesystem.md#limitations)に残し、未確認として扱う。X31は保存形式の実測を記録するテストで、記録を推定で置き換えない。
