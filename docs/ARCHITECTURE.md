# 現在の実装構造

役割: コード配置・依存方向・資源寿命の地図。実装を配置/分岐/抽象化するときに読む。契約・検査順序は仕様が定める。

[依存](#dependencies) / [GUI](#gui) / [共有経路](#shared-path) / [寿命](#lifetimes)

<a id="dependencies"></a>
## 配置と依存方向

Cli → Core、Windows → Core。CoreはWin32にもWindows projectにも依存しない。CliがWindowsFileSystemProbeとCoreのcommandを組み立てる。Win32は文書化kernel32/FSCTLだけをLibraryImportとSafeFileHandleで呼び、ntdll未文書API/reflectionを使わない。

RAR ([RAR仕様](spec/rar.md)) の配置の制約: UnRAR.dllのP/Invoke・照合・ロードはWindows側に置き、CoreはZIPと共通のアーカイブの抽象 (IArchiveSource) と、DLLの型・定数を持たないRARの受理規則・読み取りセッションの抽象だけを持つ。RAR専用の判定層・Fast専用の分岐を作らず、名前・構造・上限・内容検証の6基準とファイル安全性はZIPと同じコードを通す。形式は拡張子だけで決め (ArchiveFormats)、ZIPの経路は呼び出し先も引数も変えない。CLIだけがRARを開く関数 (DLLの読み込み元を実行中のexeのフォルダーに固定) をCommandContextに渡し、ZIPの実行では呼ばれない。GUIはDLLを参照しない。DLLはbuild・publish出力にも配布物にも置かず (利用者が置く。[版の固定](spec/rar.md#pinning))、配布処理 (`scripts/publish-gui.ps1`) とDeploymentTests・RarE2ETestsが含まれないことを検査する。

| 場所 | 現在の担当 |
|---|---|
| [CliApplication](../src/Unextract.Cli/CliApplication.cs)、[Program](../src/Unextract.Cli/Program.cs) | 引数、ConsolePrompt/ProgressLine、stdout/stderr、Windowsとの組立てと例外境界 |
| [MachineOutput](../src/Unextract.Cli/MachineOutput.cs)、[MachineOutputWriter](../src/Unextract.Cli/MachineOutputWriter.cs)、[ExecutionLog](../src/Unextract.Cli/ExecutionLog.cs) | Core型からv1レコードへの対応と明示的JSON生成、報告専用の途中件数、ログ先行の同期byte配送、CLI所有ログの作成・終了 |
| [Preparation](../src/Unextract.Core/Commands/Preparation.cs)、[AnalyzeCommand](../src/Unextract.Core/Commands/AnalyzeCommand.cs)、[DeleteCommand](../src/Unextract.Core/Commands/DeleteCommand.cs)、[CommandNotifications](../src/Unextract.Core/Commands/CommandNotifications.cs) | 共通Prepare・寿命と2入口、値だけの同期通知。AnalyzerにIDeletionProbeを渡さず削除能力を型で持たせない |
| [ZipArchiveSource](../src/Unextract.Core/Zip/ZipArchiveSource.cs)、[ZipPrevalidator](../src/Unextract.Core/Zip/ZipPrevalidator.cs)、[EntriesList](../src/Unextract.Core/Entries/EntriesList.cs) | ZIP公開APIのアダプター、名前/種別/構造/宣言量、exact選択入力 |
| [IArchiveSource](../src/Unextract.Core/Zip/IArchiveSource.cs)、[EntryBudget](../src/Unextract.Core/Zip/EntryBudget.cs) | ZIP・RAR共通のアーカイブの抽象と形式の判定、件数・名前長・メタデータ総量の判定 (ZIPは手順6、RARは列挙中に同じ計算を呼ぶ) |
| [IRarArchiveSource](../src/Unextract.Core/Rar/IRarArchiveSource.cs)、[RarPrevalidator](../src/Unextract.Core/Rar/RarPrevalidator.cs) | RARのソース・読み取りセッション・押し込み型の内容の抽象 (実体はWindows)、手順6のRAR固有の受理規則 (ZipPrevalidatorの前。種別・属性はRAR側だけで行う) |
| [TargetRoot](../src/Unextract.Core/Target/TargetRoot.cs)、[TargetResolver](../src/Unextract.Core/Analysis/TargetResolver.cs)、[RealNameResolver](../src/Unextract.Core/Analysis/RealNameResolver.cs) | root検証、検証済み列挙と実名対応・結果再利用 |
| [HandleInspection](../src/Unextract.Core/Analysis/HandleInspection.cs)、[ContentComparer](../src/Unextract.Core/Analysis/ContentComparer.cs) | 共通の個体/特殊性/サイズ検査と内容検証・比較 |
| [Analyzer](../src/Unextract.Core/Analysis/Analyzer.cs)、[SequentialDeleter](../src/Unextract.Core/Deletion/SequentialDeleter.cs) | 操作別のハンドル種別、FATAL/STOP、逐次結果・指示/成立確認 |
| [Display](../src/Unextract.Core/Display/ReportText.cs) | ReportText/AnalyzeOutput/DeleteOutput/SafeDisplayの純粋な表示生成 |
| [WindowsFileSystemProbe](../src/Unextract.Windows/WindowsFileSystemProbe.cs)、[WindowsHandles](../src/Unextract.Windows/WindowsHandles.cs) | Core抽象のWin32実装・SafeFileHandle所有 |
| [HandleOpener](../src/Unextract.Windows/HandleOpener.cs)、[FileInformation](../src/Unextract.Windows/FileInformation.cs)、[DirectoryEnumerator](../src/Unextract.Windows/DirectoryEnumerator.cs)、[ProtectedLocations](../src/Unextract.Windows/ProtectedLocations.cs) | 用途別open、同一ハンドル情報、列挙、拒否位置の実体解決 |
| [UnrarLibrary](../src/Unextract.Windows/Rar/UnrarLibrary.cs)、[UnrarNative](../src/Unextract.Windows/Rar/UnrarNative.cs)、[UnrarCallbackState](../src/Unextract.Windows/Rar/UnrarCallbackState.cs) | DLLの照合 (保持したままSHA-256) とロード (LoadLibraryExW/GetProcAddress、インスタンスごとに1回)、構造体・定数、ネイティブ呼び出しの差し替え口 (IUnrarApi。テストは台本の偽物)、例外を境界の外へ出さないコールバック |
| [RarArchiveSource](../src/Unextract.Windows/Rar/RarArchiveSource.cs)、[RarReadSession](../src/Unextract.Windows/Rar/RarReadSession.cs)、[UnrarHeaderBuffer](../src/Unextract.Windows/Rar/UnrarHeaderBuffer.cs) | 手順2 (署名 → DLL → 一覧用open → ボリューム → 列挙と上限) と手順10、前進・ヘッダー照合 (生の値はWindows側だけが持つ)・RAR_SKIP/RAR_TEST、ハンドルごとのNativeMemoryのバッファ |

<a id="gui"></a>
## GUIの配置と実行配置

`src/Unextract.Gui` は WPF / `net10.0-windows` の `unextract-gui.exe` で、利用契約は[GUI仕様](spec/gui.md)、方式と画面構成の理由は[RATIONALE](RATIONALE.md#gui)にある。`Views` は画面、`ViewModels` は画面状態、`Models` はGUI所有の値、`Services` は外部I/Oの境界を担う。小さいMVVM構成で、GUI用の別Core層や汎用の状態管理フレームワークは持たない。外部I/Oの差し込み口はプロセス実行、検索、設定保存、entries所有、ログパス生成、フォルダー選択の責務に限る。

GUIはCore / Windows / CliのDLLを参照しない。受信DTO・列挙はGUI所有で、CLIの内部型をコピーして共通契約にしない。`CliLocation` はアプリケーション配置先の `cli/unextract.exe` だけを解決する。CLIの安全性判定をGUIへ移さない。

[ArchiveSearch](../src/Unextract.Gui/Services/ArchiveSearch.cs)はUI外でディレクトリを逐次列挙し、属性の確認後に次のディレクトリへ進む。ZIP・RAR (拡張子 `.zip`・`.rar`) は名前とメタデータだけを取得し、形式を判定・検査しない。検索診断は結果と分けて返す。存在の表示用観測もこの境界に置き、属性取得の失敗を不存在に変換しない。内部のIArchiveSearchFileSystemは列挙途中の失敗・差替えを検証するadapterであり、削除の安全性を判定する層ではない。

[TargetTemplate](../src/Unextract.Gui/Models/TargetTemplate.cs)はテンプレート解決と入力形式検査だけを行う。生の解決パス、重複比較キー、[表示用変換](../src/Unextract.Gui/Models/DisplayText.cs)を分け、最終パスやFile IDは求めない。[MainViewModel](../src/Unextract.Gui/ViewModels/MainViewModel.cs)が検索セッションと操作ロックを所有し、[ArchiveViewModel](../src/Unextract.Gui/ViewModels/ArchiveViewModel.cs)がTarget登録順と親チェック、[TargetViewModel](../src/Unextract.Gui/ViewModels/TargetViewModel.cs)が個別の選択・表示用観測を保持する。一括選択を保持するのは `TargetViewModel.IsSelected` だけで、Archive単位の三状態はそこから導出し保持しない。表示フィルタは全検索モデルを置き換えず、表示用の参照リストだけを更新する。画面 ([MainWindow](../src/Unextract.Gui/Views/MainWindow.xaml)) は、表示中のArchiveの後にそのTargetを並べた1つの仮想化された作業一覧 (`MainViewModel.WorkItems`) と、閲覧対象1件の詳細欄からなる。閲覧対象 (`MainViewModel.Viewed`) は一括選択 (`TargetViewModel.IsSelected`) と別の状態で、閲覧・絞り込み・行の再生成は選択・キュー・計画を変えない。絞り込みで一覧から消えた閲覧対象は詳細欄に残してその旨を示し、除去された閲覧中のTargetはそのArchiveの表示へ戻る。状態バッジ・行の要約・操作できない理由・次の操作の案内・選択の要約 (`MainViewModel.View.cs`) は表示専用で、実行可否の判定には使わない。一括処理中はこれらの更新をまとめ、最後に1回だけ作り直す。共通の色・ボタン・フォーカス枠は[Theme](../src/Unextract.Gui/Views/Theme.xaml)にあり、各ウィンドウが自分で読み込む。ウィンドウは作業領域より大きくせず、作業一覧と詳細欄に最低限の高さを確保できない小さい寸法ではウィンドウ全体が縦にスクロールする (一覧は常に有限の高さを持ち、仮想化を保つ)。

[SearchSettings](../src/Unextract.Gui/Services/SearchSettings.cs)は検索ディレクトリ1件の小さいJSONを直接読み書きする。読み込み失敗は既定値、書き込み失敗は画面の通知へ返す。検索結果・Target・選択・CLI結果を保存せず、ファイルの削除・改名による置換を行わない。

[GuiDataRoot](../src/Unextract.Gui/Services/GuiDataRoot.cs)は、UI E2E専用の内部機構である。合成ルート (`App.OnStartup`) だけが環境変数 `UNEXTRACT_GUI_TEST_DATA_ROOT` を読み、設定ファイルとログの保存先を `<root>\settings.json` と `<root>\logs` へ向ける。未設定なら従来の `%LOCALAPPDATA%\unextract` で、値が絶対パスの既存ディレクトリでなければ、通常の保存先へフォールバックせず、エラーを表示して起動を中止する。利用者向け機能ではなく、READMEの案内に載せない。entriesの一時ディレクトリとCLIの場所には作用しない (テストは `TMP`/`TEMP` と配置の差し替えで隔離する)。

[CliJob](../src/Unextract.Gui/Models/CliJob.cs)の受信値はGUI所有で、生のname・64bit length・診断を保持する。[JsonlReceiver](../src/Unextract.Gui/Services/JsonlReceiver.cs)はstdoutのbyte列をLFで組み立て、[機械出力の契約](spec/machine-output.md)に従い型・順序・版・起動指定との対応を検査する。途中entryは受信事実であり、正常解析スナップショットではない。互換な未完了出力と、非互換・破損出力を別に保持し、resultと実終了コードが食い違ってもresultを診断用に残す。

[CliProcessRunner](../src/Unextract.Gui/Services/CliProcessRunner.cs)は1本のプロセスを所有し、`UseShellExecute=false`・`CreateNoWindow=true`・ArgumentListで起動する (シェル文字列を作らない)。起動・受信・待機はUI外で実行し、stdout/stderrを独立してEOFまで読む。出力異常後は解釈を打ち切ってもdrainを続ける。画面は不変のProgressスナップショットを参照でき、画面更新のcallbackを受信経路へ挟まない。stderrは先頭の限られた範囲だけ保持して省略を明示する。完了には実終了と両EOFが必要で、起動・終了が未確認なら同じrunnerで次のプロセスを開始できない。

終了要求はanalyzeのキャンセル登録からだけ行い、deleteには登録しない。プロセスadapterのDisposeはハンドルを閉じるだけで、強制終了・timeout・終了連動Job Objectを持たない。IChildProcessはrunner内部の寿命検証用adapterであり、追加の外部I/O境界ではない。entriesの所有は下記のEntriesStoreが担う。

通常buildはCLIへの `ReferenceOutputAssembly=false` / `Private=false` のproject参照でビルド順だけを確保し、`GetTargetPath` の出力情報から、その構成・RIDのCLI実行ファイル一式をGUI出力の `cli/` へコピーする。空の出力情報では列挙せずビルドエラーにする。publishではこの参照・コピーを使わず、[配布スクリプト](../scripts/publish-gui.ps1)がGUIとCLIを独立してpublishする。CLIの既存profileは維持し、GUIは自己完結型・トリミングなしのフォルダー配布とする。GUIのprofileだけをpublishした出力にはCLIが含まれないため、配布にはスクリプトを使う。

[AnalysisSnapshot](../src/Unextract.Gui/Models/AnalysisSnapshot.cs)は、正常完了した解析の全entry・モード・解析時のrun.target・分類別件数・候補のlength合計 (128bit加算) を持つ。候補は固定的にStrictのMATCHED、FastのSAME_SIZEで、Directoryのlengthは数えない。TargetViewModelは正常解析スナップショット、delete開始履歴、スナップショット消費済み、最新の解析ジョブ結果 (失敗・キャンセル・未実行)、直前のdelete結果1件 (表示内容・ログパス・ログ作成の確認有無) を別々に保持し、結果ラベルから可否を逆算しない。解析開始時に従来のスナップショットを実行用から外し、失敗・キャンセル・再解析失敗でも復活させない。モード変更は確認後に全スナップショットと最新の解析ジョブ結果を破棄し、delete履歴による編集ロックと直前のdelete結果は残す。直前のdelete結果はCLIが起動した (成否不明を含む) deleteの結果だけで、次に起動したdeleteの結果でだけ置き換わる。CLIを起動しなかったステップ (後続の未実行・準備失敗・確実な起動失敗) は直近の一括削除の状態として別に持ち、直前のdelete結果とログパスを保持する。一括削除の状態は次の一括削除に加わるときに消す。履歴・永続化はしない。消費済みスナップショットの候補はセッションの削除候補合計に数えない。[MainViewModel.Analysis](../src/Unextract.Gui/ViewModels/MainViewModel.Analysis.cs)は1本の逐次キューを所有し、バッチ開始から終了まで`SetBusy`で設定全体をロックする (閲覧・フィルタ・キャンセルは可)。対象はTarget単独、Archive配下の未解析、選択中の未解析で、表示フィルタは条件にしない。Targetごとに存在を再観測し (不存在のままなら起動せず未実行)、`CliProcessRunner`の`Succeeded`の時だけスナップショットを採用する。進捗はrunnerの不変Progressを定期的に読み、run.selectedと受信entry件数、Target位置から表示する。失敗の詳細は終了コード・出力異常・result.errorをDisplayTextで表示変換して示し、コードで分岐しない。runnerが前のプロセスの終了を確認できず拒否した場合はキューを止める。ウィンドウを閉じる要求は解析のキャンセルと終了確認後の閉鎖に使う。結果一覧は[EntryRowList](../src/Unextract.Gui/Models/AnalysisSnapshot.cs)がフィルタ後のindexだけを保持し、行は仮想化リストの要求時に作る。結果の一覧ビューは閲覧中のTargetの詳細欄だけが生成し、絞り込みの状態はTargetごとに保持する。


[DeletionPlan](../src/Unextract.Gui/ViewModels/DeletionPlan.cs)は、選択中のTargetのうち、現在のモードの正常解析スナップショットがあり、削除実行済みでなく、不存在でなく、候補が1件以上あるものを固定する。候補はStrictのMATCHED、FastのSAME_SIZEのファイルだけで、ZIP順の生のFullName・index・lengthを保持し、表示フィルタやその後の選択変更・再解析では変わらない。確認には削除するTargetを実行順にすべて (Archive → Target、候補件数・論理サイズ) 示し、計画時にArchiveの表示フィルタで隠れていた選択済みTargetは対象に残したまま印と件数で示す。除外したTargetも理由とともにすべて示す。確認は一括で1回、明示承認の引数が無ければCLI・entries・ログに触れない。承認後も計画が現状 (モード・スナップショット・Target) と一致しなければ開始しない。[MainViewModel.Deletion](../src/Unextract.Gui/ViewModels/MainViewModel.Deletion.cs)は各Targetの起動直前に、entriesのUTF-8実バイト量 (各行のLF込み) をCLIのファイル全体上限と比べ、超過・UTF-8化不能・ログ場所の準備失敗・entries作成失敗では起動せず未開始として後続を止める (解析結果は保持し、delete実行済みにしない)。[EntriesStore](../src/Unextract.Gui/Services/EntriesStore.cs)はOS一時ディレクトリ直下に`CreateNew`で作った自分のファイルだけを所有し、BOMなしUTF-8・LFで生のFullNameを書き込みcloseしてからCLIを起動する。後始末はプロセスの実終了を確認できた後 (または確実な未起動・準備失敗) に、所有ファイルへだけ行う唯一の`File.Delete`で、失敗や終了未確認の残置はパス付きで通知しCLIの結果を変えない。[LogLocation](../src/Unextract.Gui/Services/LogLocation.cs)は`%LOCALAPPDATA%\unextract\logs`に`delete-<UTC開始時刻>-<3桁以上の通し番号>.jsonl`を選び、同名があればGUID付きの別名にする。ログ自体はCLIが作り、GUIは読まない・加工しない。CLIプロセスが起動した (成否不明を含む) 時点でTargetはdelete実行済み・編集不可・スナップショット消費済みになり、確実な未起動だけが再解析なしの再実行を保つ。確実な未起動とは、OSの起動失敗と、runnerが`RunAsync`の呼び出し時点で同期的に拒否した場合 (プロセス未作成) だけである。実行中のジョブから想定外の例外が伝わった場合は起動成否不明として扱い、delete実行済み・結果不明とし、entriesを残して後続を止める。アプリ終了の要求は実行中のdeleteを終了せず、現在のTargetの完了後に後続を始めず閉じる。

[DeleteReport](../src/Unextract.Gui/Models/DeleteReport.cs)は終了したdeleteプロセス1件の表示内容を、受信できた範囲から作る。成功はCliJobResultの正常判定と同じ時だけで、削除成功件数と`DELETED`のlength合計 (128bit) を出す。出力が非互換・異常、実終了・stdout EOFを確認できない場合、またはrun.selectedが承認候補数と違うか承認していない名前のentryを受信した場合は、部分内容から件数を確定せず、該当する原因とともに「結果不明」とログの確認を示す。runを受信していなければ、予定ログパスはCLIが作成したと確認できないものとして表示する。互換な出力で`run`が無ければ削除0件とし、`result`があればそのerrorを示す。`run`があり正常でない場合は、resultの件数・停止理由 (stage/step/code/win32/message)、`DELETE_FAILED`とpossibly_deletedを示し、実終了コードがresultと違えば終了コードを優先してresultを詳細に残す。`result`が無い、または`internal_error`で`deletion_started`が真の場合は、承認した候補をZIPのindex順に並べ、最後に受信したentryより後の最初の1件を「不明 (削除された可能性あり)」、それ以降を未処理とする。数値+1やentriesの行順は使わず、次の候補が無ければ不明対象を作らない。ただし、run.entries_totalが解析時のエントリ数と違うか、受信entryのindexが同名の承認候補の解析時indexと違う (解析後にZIPが変わった) 場合は、1件を特定せず、entryを受信していない承認候補をすべて結果不明とする。result.errorのpossibly_deletedとentry_nameがあれば、変化の検出有無にかかわらずその対象を削除された可能性ありとし、解析時の順による推定より優先する (承認していないentry_nameは結果不明)。受信内容に表れない置き換えは検出できない (GUIはZIPの同一性を検証しない) ため、解析時のZIP順で特定した1件や未処理件数には推定である旨とログ確認を添える。受信範囲のlength合計は総削除サイズではないと明示する。run.targetが解析時と違う場合は表示だけに使う。

<a id="shared-path"></a>
## 共通解決・検査・比較とモード分岐

Prepare、TargetResolver/RealNameResolver、HandleInspector、ContentComparer.Verifyを両操作で共有する。ContentComparerは基準3〜6を任意長のチャンクで判定する検証器 (ContentVerifier) と、ZIPのpullループ・RARの押し込み (RAR_TESTのコールバック) の2つの読み方に分かれ、判定はどちらも同じ検証器が行う。RARではAnalyzer・SequentialDeleterが、借用したセッション (Strictだけ) をエントリの処理の最初に前進させる。前進の失敗は前進先のFATAL/STOPで、deleteは照合を終えるまでディレクトリを数えずに保留する ([読む範囲と順序](spec/rar.md#session))。操作差は呼び出し側に明示する。analyzeは比較用、deleteは削除用だけを使う。モード分岐はContentComparer.Verifyの非読取経路であり、Fast専用の層・実装クラス・抽象化・追加状態・API・ハンドル構成を作らない。[採用理由](RATIONALE.md#structure)と[モード契約](SPEC.md#modes)による。

ZIP構造の独自パーサを製品へ入れない。テストのZipFixture/ZipPatcherは異常を作る専用生成器で、製品パーサを兼ねない。フックは内部差し込み口で[TESTING](TESTING.md#hooks)が位置と適用条件を定める。

Prepareの失敗はCommandOutcomeのPreparationFailureに保持する。PrepareStageによる入力エラー/FATALの区別、FatalError、EntriesErrorの種別・行番号、取得済みの全エントリ数を型で渡し、人間向けMessageやDescribeから復元しない。未取得件数はnull、取得済み0件は0である。既存PrepareErrorは同じFatalErrorへの参照を維持する。FatalErrorのEntryStep/Win32Errorは診断用の情報で、判定やAPI呼び出しを追加しない。

共通FS・内容検証では、LookupResult/Resolution、HandleFailure、ContentOutcomeが元のWin32値を保持する。Analyzerは解決・open・照合・検査・比較の段階とともにFatalErrorへ渡す。TargetRootとProtectedLocationsのPrepare診断にも元の値を保持し、失敗していないAPIや値不一致には番号を付けない。HandleInspector.FinalCheckはFinalCheckFailureで情報取得失敗とM0不一致を区別し、従来の表示説明をDetailに残す。診断のための情報再取得は行わない。

deleteのDELETE_FAILED/STOPPEDはDeleteEntryResult.FailureにDeleteFailureを保持する。共通原因のFatalKindまたはdelete固有のDeleteFailureKindの一方と、判明したEntryStep・元のWin32値を渡す。Reasonは従来の表示文言で、DeleteOutputは引き続きこれを使う。通知されたSTOPPEDとDeleteReport.Stopは同じ結果なので、エントリと終了原因に同じ診断を使える。ProcessFile内の例外は段階を推測せずUnexpectedExceptionとしてSTOPし、処理器外の通知例外とは区別する。コード対応と番号・省略の契約は[機械出力](spec/machine-output.md#codes)による。

CommandContext.Notificationsは人間向け表示に代わる小さい同期通知口である。Prepare成功時にはPreparedCommandInfoでパス・モード・全件数・選択件数・entries有無だけを渡し、Preparedやハンドルは公開しない。通知を設定したcommandは人間向けstdout/stderr・進捗を使わず、Prepare失敗も構造化outcomeだけを返す。deleteの通知経路はAssumeYesを要求し、確認入力を参照しない。

Programは引数解析前の完全一致で機械経路を選び、標準出力StreamをCliApplication.RunMachineへ渡す。人間向けのUTF-8コードページ変更・復元とTextWriter入口は維持する。RunMachineは引数解析、必要なログ作成、環境の組立ての順に進み、同じAnalyzeCommand/DeleteCommandへ通知を接続する。機械経路ではProgressLineを作らず、ConsolePromptのIsInteractive/Askも使わない。別の処理エンジンや機械専用のcommandはない。

MachineOutputはPreparedCommandInfo・EntryResult/DeleteEntryResult・command outcomeからv1レコードを生成する。状態・原因・SkipReason・段階は明示的な対応表で変換し、enum名や日本語messageを解析しない。messageにはCoreが表示用に整えた説明 (entriesのヒントは整形済み) をそのまま入れ、JSONのエスケープ以外に再エスケープしない。Numberをindexに、生のFullNameをnameに、WithoutDevicePrefixを適用した最終パスをrun.targetに渡す。件数と省略は[機械出力のresult](spec/machine-output.md#result)に従い、Prepare・通常完走・FATAL・STOPを区別する。MachineDeleteProgressはPrepareで判明した件数、処理済みファイルの件数、完結したDIRECTORY累計、STOP診断だけを保存する報告専用集計であり、削除の許可や再開状態は持たない。RunMachineは配送前にObserveすることで、配送失敗した対象も処理済みとして内部エラーを報告する。

MachineOutputは出力値をUtf8JsonWriterとJavaScriptEncoder.Defaultで明示的に書き、ASCIIだけのUTF-8 byte列とLFを生成する。nullのフィールドは省略する。MachineOutputWriterはレコードを一度だけbyte列化し、ログwrite/通常flush、stdout write/flushの順で同期配送する。write/flushに失敗した先は再利用せず、通常通知はMachineOutputExceptionを呼び出し元へ伝える。唯一のWriteResultは失敗していない先だけへ配送し、終端失敗はfalseで終了1の必要を伝える。再度のresultやresult後の通常配送は拒否する。

Analyzer.OnResultはDIRECTORYを含む結果をZIP順に通知し、FATAL原因は通知しない。SequentialDeleterは既存OnResultでファイル結果 (STOPPEDを含む) を渡し、OnDirectoryCountで完結したDIRECTORYの累計だけを渡す。選択外はどちらにも通知しない。EntryResult/DeleteEntryResult.Lengthは検証済みZIPの宣言値から渡し、追加の内容・FS取得を行わない。呼び出し側は配送前に通知された処理済み事実を保存できるため、配送失敗後の件数を配送済み件数と混同しない。

<a id="lifetimes"></a>
## 資源の寿命

PreparationのPreparedがZIPとtargetルート (StrictのRARでは内容読み取りのセッションも) を実行終了まで所有し、Disposeでセッション → targetルート → アーカイブの順に閉じる。前のcloseが例外を投げてもfinallyで後の終了処理を行う。要求の組立て時 (Prepared.SessionFor) に、StrictのRARならセッションあり、ZIPとFastならなしを確かめ、崩れていれば例外にする。RARの一覧用・内容読み取り用のDLLのハンドルは、close → コールバックの状態 (GCHandle) → ヘッダー構造体・バッファの順に解放し、closeの失敗は解放の後に例外 (internal_error) にする (元の失敗の処理中なら元の失敗を優先)。finalizerは置かない。entriesはPrepare中に読み終え閉じ、選択情報だけを使う。確認待ちに個々のtargetハンドルはない。列挙用は検証/列挙の間だけ開き、照合結果だけをRealNameResolverが再利用する。

各エントリの比較用/削除用はその判定/処理中だけ所有し、usingで通常結果・STOP・例外の各経路で閉じる。M0と列挙由来の削除基準はエントリ中だけで、全件の候補/状態を保持しない。内容比較器の実測累計は実行内だけ。具体的なアクセス・共有・フラグ、情報項目と処理順は[FS仕様](spec/filesystem.md#handles)で定める。

Prepare成功通知はPreparedのusing内、エントリ通知はClassify/ProcessFileから戻ったclose後に実行する。通知が例外を投げたら次の解決・openへ進まず、Preparedを終了処理して呼び出し元へ伝える。DeleteCommandの人間向けcatchは通知経路では例外を吸収しない。正常outcomeが呼び出し元へ戻るのもPreparedの終了処理後なので、終了処理の失敗を成功resultの前に検出できる。エントリ内で捕捉したSTOPと通知例外の境界は変えない。

ExecutionLogはCreateNew/Write/ShareReadで新規作成したFileStreamを所有し、MachineOutputWriterはログも借用stdoutも閉じない。FileStreamのユーザー空間バッファを無効にして、失敗後のcloseで未配送byteを再flushしない。Disposeは1回だけ行い、close失敗はMachineOutputExceptionで返す。RunMachineはPreparedの終了処理後に唯一のresultを配送し、finallyでログを閉じる。通知例外・終了処理例外はCLIでinternal_errorへ変換し、Preparedのcloseが通知例外を覆ってもwriterが検出済みの出力失敗を優先する。DeletionStartingで実際の逐次処理開始を保持する。終端配送/ログclose失敗は追加resultなしで実終了1に反映し、全出力先が失敗した場合だけ契約外のstderrを試す。例外とログの契約は[機械出力](spec/machine-output.md#result)による。
