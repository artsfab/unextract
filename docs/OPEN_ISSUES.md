# 未確認・未解決事項

役割: 現在の不確実性と限定付き観測の更新先。残課題・リリース判断、該当する安全性変更のときに読む。完了したら知識を担当正本へ反映し、行を閉じる。

[手動](#manual-status) / [観測範囲](#observations) / [RAR](#rar) / [対象外](#out-of-scope) / [リリース・運用](#release-decisions)

<a id="manual-status"></a>
## 手動確認の状態

現行手順 (M05・M08・M14・M15) の実施状態の唯一の更新先は本書。旧方式の合格は現行手順の合格に転用しない。未実施は前提不成立とも失敗とも異なる。自動化・削除した旧項目の置き換え先は[手動に残す理由](MANUAL_TESTS.md#manual-purpose)にある。

| 項目 | 状態・影響 | 次の確認 | 参照 |
|---|---|---|---|
| M05 | 未実施。進捗と逐次行の見え方 | stdout/stderr同一端末とリダイレクトで確認 | [M05](MANUAL_TESTS.md#m05) |
| M08 | 未実施。PTY X28は機能部分の回帰で視認性を証明しない | 実端末で警告の折り返し・位置を確認 | [M08](MANUAL_TESTS.md#m08) |
| M14 (GUI受入) | 2026-10-08 実施・合格 (自作fixtureで実施。シナリオの指定はM14の現記載より前)。基本の流れ、モニター間移動、エクスプローラーの起動、OSのフォルダー選択、フォーカス・Tab・スクロールの操作感、表示倍率100/150/200%で指摘なし。実データ・実GPUの性能と既存の安全性未確認事項は含まない。その後のRAR対応 (検索の `.rar`・「アーカイブ」への文言の変更) と検索欄の最小幅の調整 (2026-10-09) の後は、STA描画の写真 (ScreenRenderTests) の確認と、UI E2E (`run-gui-ui-tests.ps1`、2026-10-09、32件すべて成功。RARのfixtureは含まない) だけで、RAR対応後のM14の再確認 (手順1〜3) は未実施。2026-10-09の判断で、RAR対応のMVPでは必須とせず、未実施のままリリースを阻害しない (合格とは扱わない) | RAR対応後の手順1〜3を実施する。GUIの画面・配布を変えたときも影響する手順を再確認 | [M14](MANUAL_TESTS.md#m14) |
| M15 (UnRAR.dllの導入と実物のRAR) | 2026-10-09 手順1〜3を実施・合 (Windows 11 10.0.26300、publish版CLI (Release・win-x64、リポジトリ外)、WinRAR 7.23 試用版、Windows PowerShell 5.1、自作データ)。1: DLL無しのRARのanalyzeは「見つかりません」・読み込み元の絶対パス・必要な版・ZIPに影響しないことを示すFATALで終了コード1、同じexeのZIPのanalyzeは成功。2: READMEの手順 (rarlabから `unrardll-723.exe` を取得、7-Zipで `x64\UnRAR64.dll` だけを取り出し、`Get-FileHash` が `894B7D2D…9BCC`) で置くと全件MATCHED・DIRECTORY、同サイズで変えた `a.txt` はMODIFIED。3: deleteで一致した2件だけが消え、変えたファイルとフォルダーが残った (確認は `--yes`。対話の `[y/N]` はX28)。手順4 (GUIでのRAR) は未実施で、2026-10-09の判断でRAR対応のMVPでは必須とせず、リリースを阻害しない (合格とは扱わない。GUIのRARの経路はGui.TestsのIntegrationTestsが自動で通す)。手順5 (RAR4) はWinRAR 7.xがRAR4を作れず前提不成立。M14はRAR対応 (検索の `.rar`・文言の変更) の前の実施で、RARのGUI操作を含まない | 手順4を配布物のGUIで実施。手順5は旧版のWinRARがあれば実施 | [M15](MANUAL_TESTS.md#m15) |

<a id="observations"></a>
## 限定付き観測と未確認範囲

S26/S34/S37/S38の実測は2026-10-03、Windows 11 10.0.26300、NTFS、SDK 10.0.401。旧PoCは2026-10-02、同Windows・NTFS、.NET 10.0.12。環境・操作時点を越えた保証に拡張しない。

| 問題・影響 | 分かっている範囲 | 次の確認 | 根拠 |
|---|---|---|---|
| 祖先改名 S26 | Strict、同一プロセス別ハンドルから対象の親・祖父母・その上の祖先を改名すると、いずれも5で失敗し、対象は削除 (祖父母より上は2026-10-09)。共有モードの判定はファイルオブジェクト単位なので別プロセスでも同じと推定。全モード・全時点を証明しない | Windows/構成変更時にS26を再実行 | [Win S26](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)、[共有モードの理由](RATIONALE.md#sharing-limits) |
| read-only open S37 | H1で付与後のopenは成功、Strict/Fastで属性による非削除 | Windows/構成変更時にS37を再実行 | [Win S37](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs) |
| 書き込み可能なマップ (旧M11) | 他のプロセスが書き込み可能にマップした後でファイルハンドルだけを閉じた場合に、共有モードの判定に参加しない経路で削除用ハンドルを開いている間に内容が書き換わり得るかは未実測。結果によらず製品の動作と限界の記述は変わらないので、受入の必須項目にせず手順も持たない | 必要になったら別プロセスのhelperで再現方法を設計 | [FS限界](spec/filesystem.md#limitations) |
| 属性 S38 | archive/normal/hidden/not-content-indexed/read-only/system/temporary/offline/compressed/sparse/dir/junctionの12項目一致。一般保証ではない | EFSとファイルsymlink、環境差を確認 | [Win S38](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)、[事前判定](spec/filesystem.md#special-files) |
| ACL S34 | 実行前の対象DELETE拒否は親DELETE_CHILDによりStrict/Fastで削除。比較中H3での変更はChangeTime不一致STOP。H3はStrictのみ | 全時点・全モードに外挿せず必要な競合だけ再現 | [Win S34](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)、[ACL理由](RATIONALE.md#acl) |
| hardlink親ID | 非削除例外は仕様・Core/Win S20で確定。USNがどのリンクの親を返すかの環境差の理由は未確認 | API/環境変更時にリンク別の値を調査。通常候補の親照合を省かない | [例外](spec/filesystem.md#hardlink-parent-id)、[理由](RATIONALE.md#hardlink-parent-id) |
| ファイルsymlink差替え | 旧PoCは作成特権がなく前提不成立、実挙動未確認。判定不能を成立扱いしない | 作成可能な隔離fixtureでS17/S19系を確認 | [失敗境界](spec/filesystem.md#failure-boundary) |
| EFS削除 | 読み取り可能属性として許可するが実削除未確認、S38にも未確認 | EFSを作成できるfixtureでT08と削除を確認 | [属性](spec/filesystem.md#special-files) |
| クラウドplaceholder | 旧PoCに再現環境なし、未確認。同期意味論は製品対象外 | 必要な環境で非削除側の判定を確認 | [対象](SPEC.md#scope) |
| 列挙直後の改名 | 列挙中の改名による見落とし/旧名と新名の両方は旧PoCで観測。見つけた直後の改名→open失敗/ID不一致という組合せは推定 | 必要時に列挙→open間へ変更を注入 | [実名確認](spec/filesystem.md#real-names) |
| stdoutパイプの読み手の消失 | 2026-10-04、Windows 11 10.0.26300 / NTFS / SDK 10.0.401 / runtime 10.0.12のJ19では、通常Release exeとwin-x64 publish exeのStrict/Fastとも非検出。runのLF受信と別読取ハンドルからのログentry観測後、リダイレクトしたStandardOutput.BaseStreamの読み手をcloseした。終了0・stderr空で、ログは最後までentryとcompleted/0のresultを記録し、承認範囲の実削除と一致した。2026-10-08の同環境での再実施も同じ。.NETのWindowsコンソールstreamが`ERROR_BROKEN_PIPE`/`ERROR_NO_DATA`を書き込み成功として扱う実装と整合する (ランタイムのソースからの推定で未確認)。仕様は検出を保証せず、この観測を他のruntime・パイプ構成へ外挿しない | OS/runtime/出力経路変更時にJ19を再実施。検出時の非継続は実パイプの挙動に依存しないJ09/J12/J13の失敗注入で確認 | [J19](../tests/Unextract.E2E.Tests/MachineOutputE2ETests.cs)、[出力先](spec/machine-output.md#destinations)、[理由](RATIONALE.md#machine-output) |
| PowerShell 5.1の保存形式 (X31、旧M13/L18) | 2026-10-09、Windows 11 10.0.26300、Windows PowerShell 5.1.26100.9444、コードページ932のコンソール (ConPTY) で、`analyze` の出力を `>` で保存するとUTF-16LE (BOM付き) で、そのentriesはL02の入力エラー (削除0件)。`Out-File -Encoding utf8` はUTF-8 (BOM付き) だが、PowerShellがネイティブコマンドの出力をコードページ932として読むため、日本語名と `café░.txt` が文字化けし、ZIPに一致するエントリが無い入力エラー (削除0件) になった。このfixtureではどちらも削除0件。文字化け後の名前が別のZIPエントリと一致した場合の非削除は保証しない (READMEの注意を参照)。PowerShell 7・他のコードページは未確認 | Windows/PowerShellの更新時にX31の記録を確認 | [X31](../tests/Unextract.E2E.Tests/PtyConsoleTests.cs)、[entries](spec/cli.md#entries) |
| GUI: 同一実体の重複Target | 別パス (junction等) で同じディレクトリを指すTargetを同一ArchiveでGUIから順に削除する場合を、実CLIでは未実施。仕様上は2つ目が再検証でMISSING等になるだけ ([GUI重複](spec/gui.md#targets))。親子Targetが同じファイルを重複して指す場合はGUI IntegrationTestsで確認済み | junctionの自作fixtureで2 Targetの連続deleteを実CLIで確認 | [GUI検証](TESTING.md#gui) |
| GUI: 実データ・実GPUの性能 | 2026-10-08、Windows 11 10.0.26300、Release・STA・Opacity 0の模擬データ (100,000 entry×10 Target、10,000 Archive×3 Target) で、通常の選択・詳細切替・絞り込みは1秒の目標内。同日、自作の空のZIP 10,000件 (100フォルダー) の再帰検索も1回観測した (継続的な測定ではなく、テストは削除済み)。M14は自作の小さいfixtureで合格。実データの大量ZIP検索、実GPUでの描画・操作の体感、実CLIの出力を長時間受信している間の体感、GUIプロセス単独のメモリは未測定 (テストのメモリ値は並行テストを含む参考値)。模擬の結果を実データの性能保証や対応件数の上限にしない | 実利用で応答・メモリに問題が出たら測定し、全結果保持の制限が要るなら製品判断に回す | [PerformanceTests等](TESTING.md#gui)、[大量データの理由](RATIONALE.md#gui-state) |
| GUI: entries上限の実ZIP | 正常解析の候補がentries上限を超え得ることは契約値からの計算で、実ZIPでは再現していない。GUIの境界 (上限ちょうど許可・1バイト超過で未開始) は模擬で確認済み | 必要時に長い非ASCII名を多数持つ自作ZIPで再現 | [理由](RATIONALE.md#gui-entries-limit)、[仕様](spec/gui.md#delete-run) |
| CI権限 | 旧CI実績はランナー管理者権限の有無を確認していない | 権限依存ケースはログの前提不成立と環境を別に報告 | [テスト原則](TESTING.md#principles) |

<a id="rar"></a>
## RAR対応

RAR対応は[RAR仕様](spec/rar.md)の契約で製品コード (Core・Windows・CLI・GUIの検索と文言) を実装した。UnRAR.dllはリポジトリにも配布物にも含めず、利用者が置く ([版の固定](spec/rar.md#pinning)、[導入手順](../README.md#rar-dll))。採用版のDLLを使う自動試験は、テスト専用の生成器 (Stored) で作ったRARで行う ([TESTING](TESTING.md#rar))。WinRARの `Rar.exe` で作る実物のRAR (圧縮・BLAKE2・NTFSストリーム・実物のSolid・分割・SFX・暗号化・リンク) はローカル検証専用で、リポジトリに収録せずCIでも実行しない ([TESTING](TESTING.md#rar-real))。RAR4の実物は前提不成立 (RAR4を作れる旧版のWinRARが無い) で未確認。

<a id="rar-observations"></a>
### 未確認の範囲

PoCの観測条件は[理由](RATIONALE.md#rar)にある。製品経路での確認と、PoCで扱っていない範囲を区別する。

| 問題・影響 | 分かっている範囲 | 次の確認 | 根拠 |
|---|---|---|---|
| 実物のRARによる製品経路 | 生成器 (Stored) のRAR4・RAR5では、採用版のDLLで実NTFSのanalyze/delete (両モード)・publish版を含むexe・GUI経由の自動試験がある ([TESTING](TESTING.md#rar))。WinRARで作った実物はローカル検証 ([TESTING](TESTING.md#rar-real)) で確かめる。2026-10-09、`verify-real-rar.ps1` をpublish版のexe (Release・win-x64、リポジトリ外、隣に採用版のDLL) とRar.exe 7.23 (試用版) で実行し84項目すべて合格: RAR5の-m3・-m0・-m5 -htb (BLAKE2)・-rr5% -qo+ -ts+・-md1g の受理と変更の検出、Strictのdeleteの非削除 (同サイズの変更・余分なファイル・フォルダー)、Fastの同サイズ別内容の削除、日本語名の `--entries`、圧縮データの1バイト破損で `CONTENT_READ_FAILED` (CRC-32・BLAKE2とも。MATCHEDにならず、deleteはSTOPで残る)、`-os` のADSありで `SKIPPED_SPECIAL_FILE`・ADSなしでStrictの `CONTENT_TOO_LONG`/STOP・FastはSAME_SIZE、Solid・`-hp`・`-p`・分割の全4巻・SFX・`-oi1`・`-oh` が両操作・両モードで同じFATALでtarget不変。RAR4の実物は前提不成立 (WinRAR 7.xはRAR4を作れない) で、RAR4は生成器 (Stored) の自動試験だけ。Unix作成のRAR、`-ol` のsymlinkは未確認 | RAR4は旧版のWinRARがあれば作って確かめる。DLLの版や読み取り経路を変えたら再実行する | [理由](RATIONALE.md#rar)、[TESTING](TESTING.md#rar-real) |
| 名前の変換の範囲 | `\`・`:` (Unix名) と `/` だけ確認。`<>"\|?*`、末尾のドット・空白、予約名、制御文字、RAR5の不正なUTF-8をDLLがそのまま返すか変換するかは未確認。そのまま返せば共通検査で拒否 (安全側)、変換するなら「WinRARの展開名と一致」の範囲を見直す | 自作RARで各文字をDLLで列挙し、WinRARの展開名と比べる | [名前](spec/rar.md#names) |
| RAR4のUnicodeでない名前 | DLLが実行環境の設定で復号すると推定。どのコード ページ (ANSI/OEM) を使うか、コンソールのコード ページの変更 (CLIのUTF-8化) の影響、変換できないバイトの扱いは未確認 | CP932のバイト名のRAR4を、言語設定の異なる環境で列挙 | [名前](spec/rar.md#names)、[理由](RATIONALE.md#rar-names) |
| 作成元OSの報告値 | WindowsとUnixの報告を確認。RAR4のMS-DOS・OS/2・Mac OSなどの作成元をDLLがどう報告するかは未確認 (ソースからはWindowsかUnixに丸めると推定) | 該当する実物で列挙 | [種別と属性](spec/rar.md#types) |
| 実物のsymlink・junction | 作成権限が無く、WinRARの `-ol` の実物は未確認 (自作RAR5のredirとRAR4のUnix modeで確認) | 作成できる環境で実物を列挙 | [列挙](spec/rar.md#listing) |
| 大きな辞書・大きなエントリ | 1 GiB超の辞書は自作ヘッダーの宣言値だけで確認した (WinRAR 7.23の `-md2g` は小さいデータでは辞書を4 MBに縮めて記録し、実物で1 GiB超の宣言を作れなかった。2026-10-09)。`UCM_LARGEDICT` の経路も自作ヘッダー (4 GiB超の宣言) でだけ到達 ([DLLの使い方](RATIONALE.md#rar-dll-usage))。4 GiB超のエントリ、GB級の多数ファイルは未確認 | 実物で辞書・サイズの報告と上限を確認 | [上限](spec/rar.md#limits) |
| NTFSストリーム以外のservice header | `-os` のストリームが本体に続いて届くことは確認。ACL (`-ow`) などのデータも混ざるかは未確認 | `-ow` などで作った実物で内容経路を確認 | [限界](spec/rar.md#limitations) |

<a id="out-of-scope"></a>
## 対象外との区別

非NTFS・USN機能のないFSは現行対象外。必須検証の未実施と混同しない。クラウド同期の意味論も対象外だが、placeholderを安全に非削除へ倒す確認の未実施は上表に残す。

<a id="release-decisions"></a>
## リリース・運用で残る判断

| 問題・影響 | 分かっている範囲 | 次の確認・判断 | 根拠 |
|---|---|---|---|
| リリース番号 | 旧READMEに「新しいバージョン番号はリリース時に決める」と明示。現行の番号設定を文書再編で変更しない | リリース担当が番号・移行案内を決定 | `e618713:README.md`「実装状況」、[project](../src/Unextract.Cli/Unextract.Cli.csproj) |
| GUIのCI検証 | CIは `verify.ps1 -Ci` で、UI E2E を除く標準の検証 (GUI配布スモークを含む) を実行する ([標準の検証](TESTING.md#verify))。UI E2E は対話デスクトップが必要なので、ローカルの `verify.ps1` だけで実行する。CI上での `-Ci` の実行は未確認 (pushしていない) | 次のCI実行で全段 (特にGUI配布スモーク) が動くことを確認し、動かなければ原因を直す | [README配布ビルド](../README.md#配布ビルド)、[workflow](../.github/workflows/ci.yml) |
| CIのNode.js注釈 | 2026-10-02の旧CI記録にNode.js 20廃止予定の注釈と対応見送りがある。現在のCIでの再現・対応要否は未確認 | 次のCI実行で注釈と依存actionsを確認し、必要なら別のCI変更として扱う | `606d89c:docs/PLAN_VALIDATION.md`「CI の初回実行」、[workflow](../.github/workflows/ci.yml) |
