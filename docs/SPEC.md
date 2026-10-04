# 製品契約と実行全体

役割: 対象・非目標、実行の段階境界、モード契約の正本。初回とPrepare/実行モデル/保証を変更するときに読む。

[対象](#scope) / [Prepare](#prepare) / [実行](#execution) / [削除0件](#zero-deletions) / [異常](#failure-stages) / [モード](#modes)

規範本文は本書と[CLI](spec/cli.md)、[ZIP](spec/zip.md)、[ファイル安全性](spec/filesystem.md)、[機械可読出力](spec/machine-output.md)の5本文と、GUIを変更するときだけ読む[GUI](spec/gui.md)である。担当領域の条件・例外・文言は各領域が定める。READMEは利用者向け要約、ARCHITECTUREは実装案内、RATIONALEは理由、TESTINGは検証方法であり仕様を追加しない。矛盾時は明示された決定・詳細規定を調べ、未決の意味変更は[未解決一覧](OPEN_ISSUES.md)へ出す。

<a id="scope"></a>
## 目的と対象

特に断らない限り Strict の仕様である。Fast の保証・非保証は[モード契約](#modes)による。

Windows 11 の CLI。単一の ZIP と、利用者が明示した既存の NTFS ディレクトリ `target` を受け取り、ZIP 内の通常ファイルと現在の target 内の通常ファイルを対応付ける。操作は2つある。

- `analyze`: ZIP 全体を検査し、全エントリを target 側で分類して表示する。**完全な非破壊操作**であり、削除用ハンドルを開かず、何も削除しない。
- `delete`: 処理対象のエントリを ZIP の順に1件ずつ、**その時点の target の状態で**検証し、条件を満たしたファイルをその場で削除する。Strict では、**削除に使う同じハンドルから読んで全バイト一致を確認したファイルだけ**を削除する。`analyze` の結果は削除の根拠にしない。

「その ZIP から展開された」という来歴は証明しない。利用者が追加・変更したファイルを誤って削除しないことを最優先とし、不明・判定不能なら削除しない。

**削除安全性の根拠は、`ZipArchive` から実際に読み出した内容と target の全バイト一致である。** CRC-32 は偶発的な破損・切り詰め・記録と実データの食い違いを検出する補助検査であり、削除の根拠でも、敵対的に作られた ZIP への防御でもない ([CRC](spec/zip.md#crc))。

ZIP にない target 内のファイルは表示・集計・分類・削除しない (完全に無視する)。実名を確認するために target 内のディレクトリの項目を走査することはあるが ([実名確認](spec/filesystem.md#real-names))、走査した名前は ZIP エントリの成分名との照合にだけ使い、照合しなかった名前は保持も出力もしない。ディレクトリは、ZIP に明示されていても、ファイル削除後に空になっても削除しない。target と ZIP 自身も削除しない。クラウド placeholder やクラウド同期の意味論は対象外とし、安全に通常ファイルと認定できない対象は削除しない。

同梱のGUI (`unextract-gui.exe`) は、ZIPの検索と複数Targetの管理・順次実行を担うラッパーで、このCLIを子プロセスとして起動し[機械可読出力](spec/machine-output.md)を使う。一致判定・削除の安全性はCLIだけが判断し、GUIの契約は[GUI](spec/gui.md)が定める。

実装は .NET 10 (LTS) と `System.IO.Compression.ZipArchive`、Windows の文書化された Win32 API (kernel32 と `winioctl.h` の FSCTL) を使う。独自の ZIP 構造パーサは作らない。

<a id="prepare"></a>
## Prepare

1. 引数を検査する ([引数と終了状態](spec/cli.md#arguments))。(`--log` を指定した場合) 続けてログファイルを新規作成し、実行終了まで保持する ([実行ログ](spec/machine-output.md#log))。失敗は入力エラー。
2. ZIP を `FileShare.Read` (他者の書き込み・削除・改名を拒否) で開いて**実行終了まで保持**する。`ZipArchive` で読み取り専用で開く (CP437 を指定。[名前の復号](spec/zip.md#decoding))。ZIP64、Data Descriptor (DD)、SFX は独自の形式判定を設けず、`ZipArchive` の読み取り結果に委ねる ([内容検証](spec/zip.md#content))。
3. (`delete` で `--entries` を指定した場合) entries ファイルを読み、[entries](spec/cli.md#entries) の形式の検査を行う。読み終えたら閉じ、以後は参照しない。
4. 拒否対象の既知フォルダーを取得し、[最終パスへ解決](spec/filesystem.md#target-root)する。失敗は入力エラー。
5. [targetルートの確認と保持](spec/filesystem.md#target-root)を行う。確認用ハンドルと保持用ハンドルの同一性、ディレクトリ・NTFS・最終パス・拒否位置を検証し、実行終了まで保持する。判定不能は入力エラー。
6. **ZIP の全エントリ**の名前、種類、重複・衝突、resource limits ([ZIP上限](spec/zip.md#limits)) を検査する ([ZIP名と構造](spec/zip.md#names))。`--entries` の有無に関係なく全エントリに行う。この段階は target に触れず、target の状態に依存しない。1件でも不正または判定不能なら FATAL。
7. ZIP 自身の個体 ([特殊ファイルと属性](spec/filesystem.md#special-files)) のボリュームシリアルと File ID を、ZIP のパスを `FILE_READ_ATTRIBUTES` のみで reparse をたどるハンドルで開いて得る (ZIP は保持済みで改名・削除されないため、実体の ID になる)。失敗は FATAL。
8. (`--entries` を指定した場合) entries の各行を手順6を通過したエントリと照合する ([entries](spec/cli.md#entries))。
9. 処理対象を決める: `analyze` は全エントリ。`delete` は全エントリ、または `--entries` で選んだエントリ (ZIP の順)。

実行全体で保持するハンドルは ZIP と target ルートの2つだけである (`--log` を指定した場合はログファイルを加えた3つ)。

<a id="execution"></a>
## エントリの処理

- `analyze`: 処理対象を ZIP の順に1件ずつ target 側で解決・分類する ([target分類](spec/filesystem.md#classification)、[特殊ファイルと属性](spec/filesystem.md#special-files))。内容比較候補は Strict では全バイト比較し、Fast では内容を読まずに `SAME_SIZE` と分類する ([モード契約](#modes))。比較用ハンドルは**そのエントリの判定が終わった時点で閉じる**。最初の FATAL で打ち切り、以後は未判定とする。結果を表示して終了する ([表示](spec/cli.md#output))。
- `delete`: 処理対象を ZIP の順に1件ずつ、[削除順序](spec/filesystem.md#delete-flow) の手順で処理する。各エントリで削除用ハンドルを1回だけ開き、同じハンドルで検査・(Strict の) 全バイト比較・最終確認・削除・成立確認を行い、閉じてから次へ進む。数千〜数万件のハンドルを同時に保持しない。結果は1件ごとに表示する ([表示](spec/cli.md#output))。STOP ([失敗の境界](spec/filesystem.md#failure-boundary)) した場合、以後のエントリは処理しない。指示前なら対象を残し、指示後に成立を確認できなければ削除された可能性を報告する。

<a id="zero-deletions"></a>
## 削除0件の範囲

Prepareのどの段階の入力エラー・FATALでも、確認での中止でも削除0件である。entriesの全形式検査・照合もこの範囲に含む。analyzeは常に削除0件である。機械可読出力では `run` レコードの有無がこの境界を示す ([完了境界](spec/machine-output.md#boundary))。

逐次delete開始後のSTOPでは、それ以前の削除は戻らず、以後は未処理となる。指示前のSTOPは対象を削除しない。指示後に成立を確認できない場合や指示後の例外では、対象は削除された可能性がある。[失敗の境界](spec/filesystem.md#failure-boundary)を参照する。

<a id="failure-stages"></a>
## FATALとSTOPの段階境界

<a id="prepare-failures"></a>
### PrepareのFATAL・入力エラー

- ZIP を開けない・`ZipArchive` が読めない。target の入力エラー ([引数と終了状態](spec/cli.md#arguments))。entries の入力エラー ([entries](spec/cli.md#entries))。
- 危険な ZIP パス、Windows 不正名、U+FFFD を含む名前、重複・case 衝突、ZIP 内部の file/dir 構造衝突、ZIP の特殊エントリ ([ZIP名と構造](spec/zip.md#names))。
- 宣言側の resource limits の超過 ([ZIP上限](spec/zip.md#limits))。
- ZIP 自身の個体の取得失敗 ([Prepare](#prepare) の手順7)。

<a id="analyze-failures"></a>
### analyzeのエントリ処理中のFATAL

- 内容比較候補の [内容検証基準](spec/zip.md#verification) の 1〜5 の違反: 暗号化、`Open()`・読み取り中の例外 (破損・未対応圧縮方式を含む)、実測バイト数が `Length` を超過または不足、CRC-32 不一致。実測展開量の合計の超過。
- target 側で存在・種類・属性・特殊性を判定する API の失敗 ([実名確認](spec/filesystem.md#real-names) の実名確認の列挙・列挙用ハンドルの検証の失敗を含む)、存在確認後のオープン失敗 (**他プロセスによる共有違反を含む**)、列挙で見つけた項目と開いたハンドルの File ID の不一致、比較中の target 読み取り失敗、最終パスの不一致 ([target分類](spec/filesystem.md#classification)、[特殊ファイルと属性](spec/filesystem.md#special-files))。

<a id="delete-failures"></a>
### deleteのエントリ処理中のSTOP

[失敗の境界](spec/filesystem.md#failure-boundary) に従う。[analyzeのFATAL](#analyze-failures)の各条件は、`delete` では STOP になる (共有違反・アクセス拒否のオープン失敗は、識別確認により `DELETE_FAILED` または STOP)。加えて、親 File ID の不一致 ([解決順序](spec/filesystem.md#resolution) の手順4)、最終確認の不一致 ([削除順序](spec/filesystem.md#delete-flow) の手順7)、削除の指示と成立確認の失敗が STOP になる。

`MISSING`・サイズ不一致の `MODIFIED`・`SKIPPED_SPECIAL_FILE` のエントリについては内容を読まないため、その圧縮データの破損・暗号化・未対応方式だけでは FATAL・STOP にならない。名前・パス・構造・特殊種別・宣言側の resource limits の検査は Prepare で全エントリに行う。

<a id="modes"></a>
## Strict / Fastの契約

既定はStrict。`--fast`で明示的にFastを選び、モードは実行全体で固定する。両操作に指定できる。`--yes`は確認だけを省き、検証を省かない。事前確認にはdeleteと同じモードのanalyzeを使う。異なるモードの結果は対応しない。

Strictは安全な通常ファイルのサイズ一致後にZIPとtargetを読み、内容検証を満たす全バイト一致をMATCHEDとする。Fastは内容を読むステージを実行せず、同じ候補をSAME_SIZEとする。0バイトも特例なく扱う。MODIFIEDはStrictではサイズまたは内容の差、Fastではサイズの差だけである。

Fastは、内容の一致、正常な展開、暗号化・破損・未対応方式でないこと、宣言内容と実データの整合、削除する内容を保証しない。**同一パス・同一サイズの変更ファイルも削除され得る。** 確認できるのはopen時のメタデータと削除直前までのM0一致までであり、Strictの誤削除防止優先は内容の点で明示的に緩める。

ファイル安全性は両モード共通。パス検証、実名解決、target外逸脱の防止、reparse/hardlink/ADS、File ID・親File ID・最終パス、判定不能の非削除、M0全項目の最終確認、削除・成立確認は省かない。宣言側のZIP上限とentries上限も共通。FastではZIP/target内容・CRCを一切読まず、内容検証によるFATAL/STOPと実測展開量の計上は発生しない。MISSING・SKIPPED_SPECIAL_FILE・DIRECTORYの扱いは同じ。

| 安全な通常ファイルの状態 | Strict | Fast |
|---|---|---|
| サイズ一致・全バイト一致・内容検証成立 | MATCHED / deleteで削除 | SAME_SIZE / deleteで削除 |
| サイズ一致・内容不一致 | MODIFIED | SAME_SIZE / deleteで削除 |
| サイズ一致・ZIP内容検証異常 | analyzeはFATAL、deleteはSTOP | SAME_SIZE / deleteで削除 |
| サイズ不一致 | MODIFIED | MODIFIED |

ZIPにないtargetファイルは両モードで無視する。警告の全文と位置は[CLI](spec/cli.md#warning)、内容検証は[ZIP](spec/zip.md#verification)、削除順序は[FS](spec/filesystem.md#delete-flow)が定める。
