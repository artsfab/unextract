# unextract MVP 実装計画

仕様の唯一の基準は [`SPEC.md`](SPEC.md)。本書は実装順と完了判定を定める。旧 v4 の ZIP 独自構造パーサ、Recycle Bin、ディレクトリ削除、クラウド識別、専用ログ、比較ハンドルを削除まで保持する案は引き継がない。テスト項目は [`PLAN_TESTS.md`](PLAN_TESTS.md)、技術判断の根拠は [`PLAN_DECISIONS.md`](PLAN_DECISIONS.md)、実測記録は [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) にまとめる。

2026-10-03 に実行モデルを改訂した (SPEC 冒頭、DEC-24〜DEC-34)。旧方式 (1回の実行で全件の初回分類 → 結果表示 → 確認 → 削除候補の再オープン・再検証・2回目の全バイト比較 → 削除。`--dry-run` は初回分類まで) は段階 A〜F で実装・検証済みであり、その記録は本書の §2・§3 と `PLAN_VALIDATION.md` に当時の事実として残す。本書の他の節は新方式 (`analyze` / `delete`) の計画である。新方式の実装は段階 R0〜R7 (§3.2) で行った。R1〜R6 と、R7 のうち自動テスト (Win・E2E・PTY・G5 の自動の実測) は 2026-10-03 に完了した。手動の M 系は未実施である (結果は `PLAN_VALIDATION.md` の「新方式の実装と検証」)。

## 1. 実装の境界

- Windows 11、C#、.NET 10 (LTS)。SDK は `global.json` で 10.0.401 (`rollForward: latestPatch`) に固定した (`PLAN_DECISIONS.md` DEC-15)。ZIP 読み取りは `System.IO.Compression.ZipArchive`、CRC-32 計算は Microsoft 公式パッケージ `System.IO.Hashing`。
- ZIP の EOCD/CD/LH/DD を独自に解析しない。ZIP64/DD/SFX はライブラリの挙動に任せる。期待 CRC は公開プロパティ `ZipArchiveEntry.Crc32` (.NET Core 2.1 / .NET Standard 2.1 で追加、.NET 10 に含まれる) から得る。
- Windows 側は文書化 API (`CreateFileW`、`GetFileInformationByHandleEx`、`SetFileInformationByHandle`、`GetFinalPathNameByHandleW`、`DeviceIoControl(FSCTL_READ_FILE_USN_DATA)`) を `LibraryImport` で呼ぶ。ntdll の未文書 API と reflection は使わない。**新方式で Win32 API・ハンドル構成は追加しない** (SPEC §8.1 の表のまま)。
- 判定ロジックと Windows のファイル個体・属性・削除操作を分離する。削除 API に未検証の ZIP パスを直接渡さない。`--entries` の文字列からパスを組み立てない。
- 調査やテスト用の ZIP fixture 生成器 (壊れた ZIP を作るためのバイト書き換えを含む) は許すが、テスト専用とし製品側の ZIP パーサを兼ねさせない。
- 操作は `analyze` と `delete` の2つ (SPEC §2)。両者は Prepare (SPEC §3.1) を共有する。`analyze` は比較用ハンドルだけを使い、削除の能力を型として持たない (`IDeletionProbe` を受け取らない)。`delete` は削除用ハンドルだけを使い、各エントリで「解決 → 事前判定 → オープン → 照合 → 検査と M0 → (Strict) 1回の全バイト比較 → 最終確認 (M0 の全項目) → 削除 → 成立確認」を同じハンドルで行う (SPEC §8.3)。
- `analyze` の全バイト比較と `delete` の全バイト比較は、同じ「エントリ内容の検証基準」(SPEC §5.2) の実装を共有する。違いは違反時の扱い (`analyze` は FATAL/MODIFIED、`delete` は STOP/MODIFIED) だけにする。
- 実行モードは Strict (既定) と Fast (`--fast`、SPEC §15) で、1回の実行全体で固定する。Fast は既存の `Unextract.Core`・`Unextract.Windows`・`Unextract.Cli` の構成の中のモード分岐として実装し、Fast 用の層、専用の実装クラス、抽象化、追加の状態管理は作らない (DEC-19、DEC-23)。新方式での Strict と Fast の処理差は「内容を読むかどうか」の1点で、`analyze` と `delete` が共有する内容検証の関数の1か所で分岐する (DEC-34)。
- 全件の削除候補とその状態 (旧スナップショット) を保持しない。`delete` が持つのはエントリ処理中のローカルな基準 (列挙由来の基準と M0。SPEC §8.2) だけ。
- S09 (target 内の ZIP 自身に対応するエントリ) は共有違反と識別確認一致の一般則に従い `DELETE_FAILED`、終了 1、ZIP を残して後続へ進む。2026-10-03 の人間判断で確定済み (DEC-27)。S09 専用の事前判定や製品コード分岐は追加しない。

## 2. 削除有効化前の確認ゲート

根拠、採用した挙動、試した fixture、未確認範囲を [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) に記録する。

| ID | 確認内容 | 状態と通過条件 |
|---|---|---|
| G1 | .NET 10、ZipArchive の名前復号と ZIP64/DD/SFX/Stored/Deflate の挙動 | **通過** (旧方式の実装で)。調査 (V1、V3) と Z 系テストの合格。SPEC §4.1・§5.4 と一致。新方式でも ZIP の読み方は変えない |
| G2 | 内容比較候補の CRC-32、宣言サイズと実測量、破損、暗号化、未対応方式。`Crc32` の導入版 | **通過** (旧方式の実装で)。調査 (V2)。ランタイムは CRC・不足・暗号化を検出しないため unextract が検出する (SPEC §5.2)。C 系テストの合格。新方式でも検証基準は変えない |
| G3 | 属性の許可集合、ZIP 特殊種別、ADS、hardlink、reparse、case strict、File ID、親 File ID、ハンドル設計 | **通過 (未確認の事項を除く)** (旧方式の実装で)。非破壊の実測 (V4〜V7)、SPEC §13 の PoC 1〜8、D 系テストの実 NTFS での合格、E-2 の実測 (D22 を含む)。§13 の未確認の事項は成立と見なさない。§4 の対応表に反映済み |
| G4 | resource limits | **通過** (旧方式の実装で)。値と適用範囲は確定 (SPEC §11)。R 系の境界テストの合格。benchmark は不要 |
| G5 | 新方式 (`analyze` / `delete`) で生じた未実測の事項 (SPEC §13 の「2026-10-03 の実行モデル改訂で生じた未実測の事項」) | **未実施**。`PLAN_TESTS.md` の S26、S34、S37、S38、L18 と手動の M09〜M13 で実測し、結果を `PLAN_VALIDATION.md` に記録する。SPEC の設計はこれらの結果を削除の根拠にしない (結果がどちらでも削除しない側に倒れるように定めている) が、結果が SPEC の推定・記述と異なれば SPEC・本書を見直す。成立と見なさない |

G1〜G4 は旧方式の実装 (段階 A〜F) で通過した。新方式は Win32 API・ハンドル構成・ZIP の読み方を変えず、検査・比較・削除の順序と時点を変えるものなので、G1〜G4 の根拠 (調査・PoC) はそのまま使う。G1〜G4 に対応する自動テストは新方式の実装後に再度合格させる (§5)。

Fast (SPEC §15) は、新しい Windows API、ハンドル構成、ZIP の読み取り方法を加えず、Strict の内容読み取りを実行しないだけなので、新しいゲートは設けない。

ゲート不成立なら、推測で「正常」と扱わない。SPEC §13 の未確認・未実測の事項は成立と見なさず、SPEC §14 に従う。

## 3. 実装順

### 3.1 旧方式の実装 (完了。記録)

| 段階 | 作業 | 状態 |
|---|---|---|
| A. 基盤 | ソリューション、`global.json`、CLI 引数、結果型、README とライセンスの骨格 | 完了 |
| B. ZIP 事前検証 | ZIP ハンドル保持、CP437 指定の ZipArchive アダプタ、U+FFFD 拒否、全エントリのパス・構造・特殊種別検証、resource limits (宣言値) | 完了。新方式の Prepare でそのまま使う |
| C. target 分類・内容検証 | target ルート検査と保持、親成分の分類表、比較用ハンドル、特殊判定、サイズ比較、内容比較、旧スナップショット | 完了。分類部分は新方式の `analyze` と共通関数に再構成する |
| D. dry-run と表示 | 全カテゴリー全パス、FATAL 時の表示、progress、Fast の警告 | 完了。新方式で `analyze` の表示に置き換える |
| E-1. 削除 PoC | SPEC §13 の PoC 1〜8 | 完了。結果は新方式にも適用する |
| E-2. 削除 | 確認、削除用ハンドルの再オープン、同一性再検証、2回目の全バイト比較、最終確認、ハンドルベース削除、成立確認、`DELETE_FAILED` と停止、識別確認 | 完了。新方式で逐次削除に再構成する |
| F. 仕上げ | Windows CI、実機確認、README、配布ビルド | 完了 |
| Fast | A、C、D、E-2、F へのモード分岐 | 完了 (2026-10-03 記録) |

### 3.2 新方式の実装 (R1〜R6 完了。R7 は手動の M 系が未実施)

| 段階 | 作業 | 完了条件 |
|---|---|---|
| R0. 文書 | SPEC・PLAN・PLAN_TESTS・PLAN_DECISIONS・README・MANUAL_TESTS の改訂 | 2026-10-03 完了。実装開始前のレビュー |
| R1. 共通関数の抽出 | `ClassificationRun` から、親成分・最終成分の解決、ハンドル上の検査 (照合・特殊判定・サイズ・M0 の取得)、内容検証のモード分岐を抽出する (§6.1)。旧方式の挙動を変えない純粋なリファクタリング | 既存テストが全て通る |
| R2. analyze | `analyze` のループ (スナップショット・候補リストなし)、`EntryResult` の期待パス、`analyze` の表示 (§5)、表示用エスケープの追加 | A 系・O08〜O12 が通る |
| R3. entries | `--entries` のパースと照合 (純粋関数) と上限 | L 系 (Core) が通る |
| R4. 逐次削除 | `delete` のループ (事前判定 D1、列挙由来の基準、親 File ID の照合、M0、全項目の最終確認、識別確認の基準変更、`DELETE_FAILED` / STOP / 削除された可能性あり) | S 系 (Core) が通る |
| R5. CLI | サブコマンドの解析、旧形式と `--dry-run` の入力エラー、Runner の分割 (Prepare 共有)、確認 (案 A)、進捗と逐次結果の表示、`delete` の表示 | K 系・O13〜O16・CLI テストが通る |
| R6. 旧コードの削除 | `DeletionPhase` の再オープン方式、`--dry-run`、旧スナップショット、2回目の比較 (`ComparisonPass.Recheck`) の削除。既存テストの書き換え (`PLAN_TESTS.md` §2) | 警告ゼロ、全テスト合格。Skip・期待値の緩和なし |
| R7. 実機と仕上げ | Windows 統合テスト (競合・実測項目)、E2E・PTY、手動 M 系、README の最終確認、CI | `PLAN_TESTS.md` の必須項目の合格と、G5 の実測の記録 |

## 4. 主要な実装上の約束

### Prepare (両操作に共通)

SPEC §3.1 の順に行う: 引数 → ZIP のオープンと保持 → (`delete --entries`) entries の読み込みと形式の検査 → 拒否対象の解決 → target ルートの確認と保持 → ZIP 全エントリの事前検証 → ZIP 自身の個体 → (`--entries`) entries と ZIP の照合。どの段階の失敗も削除0件で、target のエントリ (target ルート以外の列挙・比較用/削除用ハンドルのオープン) には触れない。`delete` の確認 (案 A) は Prepare の後、最初の target エントリの処理の前に1回だけ行う (DEC-31)。

ZIP の全エントリの名前・パス・重複・case 衝突・file-dir 構造・特殊種別と resource limits (宣言値) は、`--entries` の有無に関係なく全エントリに行う。

### Windows の対象解決 (両操作に共通)

target ルートは、先に確認用ハンドル (`FILE_READ_ATTRIBUTES` のみ、`OPEN_REPARSE_POINT` 付き) で最終成分が reparse でないことと File ID を確認してから、`FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES` (DELETE を共有しない) で開いて保持し、File ID の一致を確かめて最終パス (`\\?\` 形式のまま) と File ID を記録する (SPEC §3.1 の手順5、§8.1)。実名は SPEC §6.2 のとおり、検証済みのディレクトリハンドルから `FileIdExtdDirectoryInfo` で項目を列挙し、ZIP の成分名と序数比較して確認する (ディレクトリごとに1回列挙し、照合しなかった名前は保持しない。探す名前は処理対象のファイルエントリから集める。列挙の失敗は `analyze` で FATAL、`delete` で STOP。1回の列挙で同じ名前が複数回返ったら最初の1件だけを採用し、見落とされた名前は `MISSING`)。`delete` でも列挙結果を後続のエントリに再利用し、再列挙しない (SPEC §6.2 の「`delete` での列挙結果の再利用」、DEC-25)。

### analyze

最終成分は比較用ハンドルで開き、File ID が列挙で見つけた項目と一致することと、最終パスの序数一致を確認してから、同じハンドルで属性許可リスト・reparse・リンク数・ADS・ZIP 自身を判定し、サイズを比べ、Strict は内容比較を行う。API 失敗、存在確認後のオープン失敗 (共有違反を含む) は FATAL。ハンドルは判定が終わった時点で閉じる。スナップショット・候補リストは作らない。

### delete

各エントリについて SPEC §8.3 の手順1〜9 を行う。

- 事前判定 (D1、DEC-28): 最終成分の列挙項目の属性がディレクトリ (0x10) を含む、reparse (0x400 または reparse tag ≠ 0)、許可集合外のビットを含む (`FileAttributeRules.IsSpecial`) なら、削除用ハンドルを開かずに `SKIPPED_SPECIAL_FILE`。理由は `SkipReason` で表す (ディレクトリ → `Directory`、reparse → `ReparsePoint`、属性 → `Attributes`)。
- 削除用ハンドル (`GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE`、`FILE_SHARE_READ`、`OPEN_REPARSE_POINT | OPEN_NO_RECALL`) を期待パスで1回だけ開く。比較用ハンドルは開かない。
- オープン直後の照合: ボリュームシリアル (= target ルート)、File ID (= 列挙項目)、親 File ID (`GetParentFileId` の値 = 列挙でたどった親の File ID。target ルート直下はルートの File ID)、最終パス (= 期待パス)。不一致・取得失敗は STOP (DEC-29)。
- 検査と M0: `Directory` (true なら `SKIPPED_SPECIAL_FILE`)、`DeletePending` (true なら STOP)、basic・tag・streams の取得 (失敗は STOP)、特殊判定、サイズ。M0 (SPEC §8.2) を内容比較の前に同じハンドルから取得する。
- 全バイト比較 (Strict のみ): 同じハンドルから target を読み、ZIP の展開ストリームを開いて SPEC §5.2 の基準で1回だけ比較する。実測展開量はこの比較で計上する。1〜5 の違反・target の読み取り失敗・実測合計の超過は STOP、6 だけの不成立は `MODIFIED` で続行。
- 最終確認: M0 の全項目 (File ID とボリュームシリアル、親 File ID、最終パス、`EndOfFile`、`LastWriteTime`、`ChangeTime`、属性、リンク数、ストリーム一覧、reparse 状態) と `Directory`・`DeletePending` を同じハンドルで再取得して照合する。旧方式の `DeletionPhase.Revalidate` 相当の全項目検査を流用する (DEC-29)。親 File ID と最終パスは、片方だけでは検出できない差し替えがあるため、どちらも省略しない (DEC-13)。
- 削除: `SetFileInformationByHandle(FileDispositionInfoEx)` に flags `FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS` (0x3) を渡し、`IGNORE_READONLY_ATTRIBUTE` は使わない。成立は同じハンドルの `DeletePending == true` で判定し、API の成功だけでは判定しない (DEC-11)。パスベースの削除は使わない。
- ハンドルは各エントリの処理の中で閉じる (STOP・例外を含むどの経路でも)。STOP の後のエントリは処理しない。既に削除したファイルは戻さない。

### Fast モード

Fast (SPEC §15) は既存の構成の中のモード分岐として実装する (DEC-19、DEC-23)。置き場所は次のとおり。

| 項目 | 置き場所 |
|---|---|
| `--fast` の解析 | 引数解析 (`Unextract.Core` の `CommandLineParser`)。`analyze`・`delete` の両方で受け付ける。重複は入力エラー。`Unextract.Cli` は解析結果のモードを実行要求に渡すだけにする |
| モード (Strict / Fast) と分類 `SAME_SIZE` | `Unextract.Core`。モードは実行全体で1つの値とする |
| 内容を読むかどうかの分岐 (SPEC §6.1 の手順8、§8.3 の手順6) | `Unextract.Core` の内容検証の関数1か所 (`analyze`・`delete` 共通。DEC-34) |
| Win32 の呼び出し | `Unextract.Windows` のまま。Fast 用の API やハンドル構成を加えない。比較用・削除用ハンドルの構成は両モードで同じ (削除用ハンドルの `GENERIC_READ` は Fast では読み取りに使わないが、構成を変えない。DEC-22) |

**検証・削除フロー上の Strict と Fast の明示的な処理差は、内容を読むかどうかの1点だけとする** (DEC-34)。表示、分類名、`--fast` の解析はこれに含めない。

| 箇所 | Strict | Fast |
|---|---|---|
| サイズ一致の安全な通常ファイル (`analyze`: SPEC §6.1 の手順8、`delete`: §8.3 の手順6) | SPEC §5.2 の内容検証・全バイト比較を行う。`analyze` は `MATCHED` / `MODIFIED` / FATAL、`delete` は最終確認へ / `MODIFIED` / STOP | ZIP エントリと target のどちらの内容も読まず `SAME_SIZE`。`delete` はそのまま最終確認へ |

この1点を実行しない結果として、Fast では次が発生しない。いずれも該当する処理が呼ばれないことの帰結として扱い、**個別の Fast 分岐は加えない** (SPEC §15.2)。

- ZIP エントリの内容 (entry body) の読み取り。`IsEncrypted` の確認、`Open()`、CRC-32 の計算を含む。
- SPEC §5.2 の 1〜5 の違反と、比較中の target の読み取り失敗による FATAL・STOP。
- 1エントリの実測展開量と実測展開量の合計の計上と、その上限による FATAL・STOP。

次は分岐せず両モードで同じに行う: Prepare、SPEC §6.1 の手順1〜7、§6.2、§7 (`delete` の事前判定を含む)、比較用ハンドルと共有違反の FATAL (`analyze`)、削除用ハンドル、オープン直後の照合、M0、最終確認 (全項目)、削除方式 (flags 0x3)、`DeletePending` による成立確認、`DELETE_FAILED` と STOP の境界 (下の対応表を含む)、終了コード。

### エラーコードと `DELETE_FAILED` / STOP の対応 (delete の削除用オープン)

SPEC §8.4 の境界を、Win32 エラーコードに次のように対応付ける。SPEC §13 の PoC 7 の実測 (Windows 11 10.0.26300、NTFS、他プロセスの操作は別プロセス) で確定した。削除用オープンで観測したコードは 32、5、2、3 の4種類である。**表に無いコードは全て STOP とする。**

| 段階 | Win32 エラー | 扱い | 根拠 |
|---|---|---|---|
| オープン | `ERROR_SHARING_VIOLATION` 32 | 識別確認が列挙由来の基準と「一致に見える」なら `DELETE_FAILED` (内容比較なし)、不一致・失敗・判定不能なら STOP | PoC 7 #1・#2。他プロセスが書き込み中、または `FILE_SHARE_DELETE` なしで読み取り中。識別確認はどちらも「一致に見える」 |
| オープン | `ERROR_ACCESS_DENIED` 5 | 同上 | PoC 7 #3・#5・#6・#7・#11。ACL 拒否、対象のディレクトリ化・ディレクトリ junction 化、削除保留中が**同じ 5** になり、コードだけでは区別できない。識別確認が必須 |
| オープン | `ERROR_FILE_NOT_FOUND` 2、`ERROR_PATH_NOT_FOUND` 3 | STOP | PoC 7 #8・#9・#10・#14。対象消失で 2、親の消失・親のファイル化・リンク先のない junction で 3。識別確認も同じコードで失敗する |
| オープン | reparse・名前解決関連 (`ERROR_CANT_ACCESS_FILE` 1920、`ERROR_CANT_RESOLVE_FILENAME` 1921、`ERROR_NOT_A_REPARSE_POINT` 4390 など)、クラウド関連 (`ERROR_CLOUD_FILE_*`) | STOP | PoC では観測していない。表に無いコードと同じく STOP とすることを明示する |
| オープン | その他全て | STOP | 未知・曖昧 |
| オープン成功 | — | 照合・検査へ | PoC 7 #4・#13・#15・#16。差し替えは照合 (SPEC §8.3 の手順4) と最終確認 (手順7) で検出する |
| 照合・検査・比較・最終確認 | 全ての失敗・不一致 (比較の §5.2 の 6 だけの不成立を除く) | STOP | SPEC §8.4 |
| 削除 (`SetFileInformationByHandle`) | 全ての失敗 (read-only による `ERROR_ACCESS_DENIED` 5 を含む) | STOP | PoC 1〜3・6 で read-only の付与は指示の失敗 (5) になった。原因を識別できないため STOP (DEC-12) |
| 成立確認 | `DeletePending` が true でない、または取得できない | STOP (「削除された可能性あり」と報告) | SPEC §8.3 の手順9、§8.4。API の成功だけでは成立としない |

PoC 7 の各状況での実測 (2026-10-02。PoC は旧方式の削除フェーズを前提に、識別確認をスナップショットと比較して判定した。SPEC では 32 と 5 のときだけ識別確認を行うが、PoC では全状況で実行して判定を記録した。新方式では識別確認の比較基準が列挙由来の基準 (SPEC §8.2) に変わるが、比較する項目は同じである):

| # | 状況 | 削除用オープン | 識別確認の判定 | 旧方式での分類 |
|---|---|---|---|---|
| 1 | 他プロセスが `GENERIC_WRITE` (共有 R\|W\|D) で開いている | 32 | 一致に見える | `DELETE_FAILED` |
| 2 | 他プロセスが `GENERIC_READ` (共有 R のみ) で開いている | 32 | 一致に見える | `DELETE_FAILED` |
| 3 | ACL: 対象の READ_DATA を拒否 | 5 | 一致に見える | `DELETE_FAILED` |
| 4 | ACL: 対象の DELETE を拒否 (親の DELETE_CHILD は許可) | **成功** | 一致に見える (PoC で比較した項目では段階2の比較も差なし) | 段階2へ (下記) |
| 5 | ACL: 対象の DELETE と親の DELETE_CHILD を拒否 | 5 | 一致に見える | `DELETE_FAILED` |
| 6 | 対象がディレクトリに差し替えられた | 5 | 不一致 (File ID、`Directory`) | 停止 |
| 7 | 削除保留中 (他プロセスが削除を指示してハンドルを保持) | 5 | 失敗 (識別確認のオープンも 5) | 停止 |
| 8 | 対象消失 | 2 | 失敗 (2) | 停止 |
| 9 | 親ディレクトリ消失 | 3 | 失敗 (3) | 停止 |
| 10 | 親がファイルに差し替えられた | 3 | 失敗 (3) | 停止 |
| 11 | 対象がディレクトリ junction に差し替えられた | 5 | 不一致 (File ID、`Directory`、reparse) | 停止 |
| 12 | 対象がファイル symlink に差し替えられた | **未確認** (作成に必要な特権がなく再現できなかった) | — | 表に従う (オープンが成功すれば照合の reparse・File ID 比較、失敗すれば上表のコードの行) |
| 13 | 途中のディレクトリが junction に差し替えられた (同じファイルに到達) | 成功 | 不一致 (最終パス) | 最終パス不一致 → 停止 |
| 14 | 途中のディレクトリが、リンク先の存在しない junction に差し替えられた | 3 | 失敗 (3) | 停止 |
| 15 | 同名の別ファイルに差し替えられた | 成功 | 不一致 (File ID) | File ID 不一致 → 停止 |
| 16 | 親ディレクトリを他プロセスが `FILE_LIST_DIRECTORY` (共有 R) で保持 (対象は未使用) | 成功 | 一致に見える (段階2も差なし) | 段階2へ (妨げにならない) |
| 17 | クラウド placeholder | **未確認** (再現環境なし) | — | 表に従う (reparse・クラウド関連のエラーは STOP。オープンが成功すれば照合の reparse 比較。新方式では列挙項目の reparse 属性による事前判定 (D1) が先に働く) |

**#4 (対象の DELETE だけを ACL で拒否) の扱い**: 親が DELETE_CHILD を許可していると、削除用オープンは成功し、オープンの段階では検出できない。ACL 自体は M0・旧スナップショットの比較項目に含まれない。旧方式の E-2 の実測 (`PLAN_VALIDATION.md` の「E-2 実測」、D22) では、初回分類の前に拒否した場合は段階1・2・5がすべて成功して削除され、確認待ち中に拒否した場合は ACL の変更で `ChangeTime` が変わり段階2の再検証で停止した。扱いは、Windows の通常の ACL の挙動として受け入れることに確定している (案 A。ファイルの ACL は比較対象にしない。DEC-12)。新方式では、`delete` の実行前に拒否した場合は削除され (オープン直後の M0 にはその後の ACL が反映されているため)、全バイト比較中に拒否した場合は `ChangeTime` の変化として最終確認で STOP になると推定している。新方式での実測は S34 で行う (G5)。この ACL の扱い自体を理由として、別のファイルを削除対象とするものではない。

識別確認は、オープン失敗時にだけ `FILE_READ_ATTRIBUTES` のみのハンドルで行う (SPEC §8.4)。「一致」は識別確認の時点で列挙由来の基準と一致する通常ファイルに見えるという意味で、拒否された個体との同一性や拒否の理由は保証しない。そのため一致しても `DELETE_FAILED` (削除しない) より強い扱いにはしない。識別確認自体が失敗した場合も STOP とする。

### テスト用フック

製品 CLI の公開機能にはしない内部の差し込み口として、次を設ける。

- `analyze`: 結果表示の直後。
- `delete`: 確認プロンプトの直前 (確認待ち)、エントリの解決の後・削除用オープンの直前 (H1)、オープン直後・照合の前 (H2)、全バイト比較中 = target の最初の読み取りの直前 (H3、Strict のみ)、最終確認の直前 (H4)、削除指示の直前 (H5)。
- エラーコードと API の失敗を注入できる偽ファイルシステム (`FakeFileSystem`)。

## 5. 表示の文言 (SPEC §10 の具体化)

利用者向けの文言は日本語を基本とし、既存の接頭辞 (「入力エラー: 」「FATAL: 」「停止: 」「内部エラー: 」) と文末 (「確認できません。」「削除しません。」) の文体に合わせる。状態名は英大文字。

### 5.1 結果行

```text
MATCHED               bin/a.dll -> D:\Work\A\bin\a.dll
SKIPPED_SPECIAL_FILE  cache/data.bin -> D:\Work\A\cache\data.bin (ADS)
```

- 状態名を 20 桁に空白で左詰め + 空白2個 + Entry + ` -> ` + Target。Entry は各行の 23 桁目から始まる (先頭空白を含む Entry もそのまま保たれる)。
- Entry の表示用エスケープ: 書式文字 (Unicode の Cf)、C1 制御 U+0080〜U+009F、U+2028・U+2029 を `\u{XXXX}` で表し、行末に ` [表示用にエスケープ済み: --entries へそのまま転記できません]` を付ける。`\` と `"` は変換しない (FATAL・STOP の原因の表示に使う既存の `SafeDisplay.Escape` は、表記を一意にするため `\` と `"` も変換するまま残す)。
- Target の表示用エスケープ: Entry と同じ文字だけ。`\` はそのまま。
- `SKIPPED_SPECIAL_FILE` の理由 (`SkipReason` → 表示): 親 reparse → `(親が reparse)`、ディレクトリ → `(ディレクトリ)`、reparse → `(reparse)`、hardlink → `(hardlink)`、ADS → `(ADS)`、ZIP 自身 → `(ZIP 自身)`、属性 → `(属性)`。
- `DELETE_FAILED` の行は ` : <理由>` を続ける。オープンできなかった場合の理由は「削除用に開けません (<Win32 の説明>)。識別確認の時点では同じファイルに見えるため、削除せずに残しました。内容は確認していません」。

### 5.2 ヘッダーと凡例

```text
Archive: <ZIP のパス (指定されたまま、表示用エスケープ)>
Target:  <target の最終パス (\\?\ を除く)>
Mode:    Strict | Fast
凡例: Target は target 内の対応する場所です。MISSING の場合は実在しない期待位置を示します。Target は確認用で、--entries には Entry を書きます。
```

Fast では、ヘッダーの最初の行に次の警告を置く (DEC-20 の文言を維持)。

```text
警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。
```

### 5.3 analyze

- 見出し行 `Status                Entry -> Target` の後に結果行。
- 完走: `合計: N エントリ (MATCHED a、MODIFIED b、MISSING c、SKIPPED_SPECIAL_FILE d、DIRECTORY e)` (Fast は `SAME_SIZE` を先頭に)。続けて `analyze は削除しません。削除は unextract delete で行います (delete は実行時の状態を改めて検証します)。`
- FATAL: stdout に `判定済み: N エントリ (...)`、`FATAL: 1 エントリ (#n)`、`未判定: M エントリ`。ZIP 事前検証の FATAL では結果行と見出しを出さず `判定済み: 0 エントリ` と `未判定: M エントリ`。stderr に `FATAL: エントリ #n "<名前>": <原因> (<詳細>)` と `解析を中止しました。analyze は削除を行いません (削除0件)。`

### 5.4 delete

- ヘッダーの後に `対象: 全 N エントリ` または `対象: N エントリ中 K エントリ (--entries)`。
- 確認プロンプト (Fast は直前の行に 5.2 の警告):

  ```text
  最大 K 件のファイルエントリを1件ずつ検証し、条件を満たしたものをその場で完全に削除します。
  途中で停止した場合、それまでに削除したファイルは元に戻りません。
  続行しますか? [y/N] 
  ```

  中止: `中止しました。削除0件。` 非対話で `--yes` なし: `標準入力が対話的でなく --yes も無いため、確認できません。` の後に `中止しました。削除0件。` (旧方式と同じ文言)。
- 結果行は処理した順に1行ずつ出す。STOP の対象は `STOPPED` の行。
- 要約: `要約: 削除済み a、MODIFIED b、MISSING c、SKIPPED_SPECIAL_FILE d、DIRECTORY e、DELETE_FAILED f、処理対象外 h、未処理 g`。処理対象外は `--entries` で選択されなかった ZIP エントリの件数、未処理は STOP 後に処理しなかった選択対象の件数。対象外の target は解決しない。
- STOP した場合 (stdout): `途中で停止しました。それまでに削除した a 件は元に戻りません。<Entry> は削除していません。未処理の g 件には触れていません。` (成立確認の失敗などでは「削除していません」を「削除された可能性があります」にする)。stderr: `停止: エントリ #n "<名前>": <理由>[ (削除された可能性あり)]` と `以後の処理を停止しました (削除済み a、DELETE_FAILED f、未処理 g)。`
- STOP なしで `DELETE_FAILED` がある場合 (stderr): `DELETE_FAILED が f 件あるため、エラーとして終了します (削除済み a)。`
- Prepare の失敗: stderr に `入力エラー: ...` または `FATAL: ...` と `削除開始前に中止しました。削除0件。`

### 5.5 入力エラーの案内

- 使い方:

  ```text
  使い方: unextract analyze <archive.zip> --target <dir> [--fast]
          unextract delete <archive.zip> --target <dir> [--fast] [--entries <file>] [--yes|-y]
  ```

- サブコマンドなし・旧形式: `入力エラー: サブコマンド (analyze または delete) を指定してください。旧形式 (unextract <archive.zip> --target <dir>) は廃止しました。` + 使い方。
- `--dry-run`: `入力エラー: --dry-run は廃止しました。削除せずに結果を確認するには unextract analyze を使ってください。` + 使い方。
- entries: `入力エラー: --entries の <n> 行目: <理由>` (例: 「ZIP に一致するエントリがありません ("Bin/a.dll")。大小文字だけが違うエントリ "bin/a.dll" があります」「UTF-8 として読めません」「UTF-16 で保存されています。UTF-8 で保存してください」「空行です」「3 行目と同じです」「ディレクトリエントリは指定できません」)。

### 5.6 進捗

stderr に `Checking n / total` (`analyze`)、`Processing n / total` (`delete`) を CR で1行上書きする。最初・100 件ごと・最後だけ書く (旧方式の `ProgressLine` の間隔を維持)。stderr がリダイレクトされているときは出さない。`delete` では結果行を stdout に書く前に、進捗の行を消してから書く (同じ端末で行が混ざらないようにする)。

## 6. コードの変更計画

R1〜R6 で次のように変更する。製品コードの変更は R1 以降で、本書の改訂時点では行っていない。

### 6.1 Core

| 現行 | 変更 |
|---|---|
| `ZipPrevalidator`、`ZipStructure`、`EntryPath`、`ZipEntryTypeRules`、`ZipArchiveSource`、`TargetRootValidator`、`FileAttributeRules`、`RealNameResolver` | そのまま再利用。`RealNameResolver` は処理対象のエントリだけで構築する (呼び出し側の変更のみ) |
| `ClassificationRun.ClassifyFile` の親成分・最終成分の解決 | 抽出し `analyze`・`delete` で共有する。結果: MISSING / SKIPPED(親 reparse) / 判定不能 / 見つかった (列挙項目、期待パス、親の File ID) |
| `ClassificationRun.ClassifyOpened` の照合・特殊判定・サイズ・M0 の取得と `SpecialReason` | 抽出し `IComparisonHandle` に対して書く (`IDeletionHandle : IComparisonHandle` なので両方のハンドルに使える)。`delete` 用に親 File ID の照合と `DeletePending` の確認を加える |
| 手順7 の Strict/Fast 分岐 | 内容検証の関数として抽出 (Fast の唯一の分岐点) |
| `ClassificationPipeline` / `ClassificationRun` | `analyze` のループに縮小 (旧スナップショットと候補リストを除く) |
| `DeletionPhase` | 分解。`Revalidate` (最終確認に流用)、`IdentityMismatch` (基準を列挙由来に)、`DeletionOpenErrors`、削除指示・成立確認・「削除された可能性あり」の判定を逐次削除へ移す。再オープン方式は削除 |
| (新規) 逐次削除 | `delete` のループ (SPEC §8.3)。エントリごとの結果をコールバックで通知し (逐次表示用)、最後に要約を返す |
| (新規) entries の解析と照合 | バイト列 → 選択集合の純粋関数 (SPEC §3.3) |
| `ContentComparer` | `ComparisonPass`・`RecheckComparisons` を削除。実測合計はその実行の全比較で数える |
| `AnalysisResult` | `DeletionCandidates`・`_matched` を削除。`EntryResult` に期待パス (Target 表示用) を追加 |
| `TargetSnapshot`、`MatchedFile`、`DeletionRequest`、`IDeletionPhase`、`DeletionReport` | 削除 (`TargetSnapshot` の項目は M0 の内部の値型に移す)。`DeleteFailure`、`DeletionStop` は再利用 |
| `UnextractRunner` | 共通の Prepare と `analyze`・`delete` の2入口に分割。確認のタイミングの判断は Core が持つ |
| `RunRequest.DryRun` | 削除 |
| `DeletionHooks`、`RunHooks` | §4 のテスト用フックに再定義 |
| `CommandLineParser` | サブコマンド・`--entries`・旧形式と `--dry-run` の入力エラー |
| `AnalysisReport` | `analyze` 用と `delete` 用の書式に分ける。`DeletingProgress` → `Processing` |
| `SafeDisplay` | Entry/Target 用の表示用エスケープ (エスケープの有無を返す) を追加 |
| `Limits` | entries の上限 (128 MiB、4,096 バイト、100,000 行) を追加 |

### 6.2 Windows

`IFileSystemProbe`、`IDeletionProbe`、`IComparisonHandle`、`IDeletionHandle` と `Unextract.Windows` の製品コードは変更しない (新しい API・ハンドル構成なし)。

### 6.3 CLI

- サブコマンドで振り分け、Core の2入口を呼ぶ。`CliEnvironment.DeletionPhase` (テスト用の差し替え) は `IDeletionProbe` の差し替えに置き換える。
- 進捗と逐次結果の仲介 (§5.6)。
- `ConsolePrompt` と `ExitCodes` は変更しない。

### 6.4 実装結果 (2026-10-03)

上の計画の実際の名前: 解決は `TargetResolver`、ハンドル上の照合・検査・M0・最終確認は `HandleInspector` (`HandleState` が M0)、内容検証のモード分岐は `ContentComparer.Verify`、`analyze` のループは `Analyzer` (`AnalyzeRun`)、逐次削除は `SequentialDeleter` (`DeleteRun`、フックは `DeleteHooks` の H1〜H5)、Prepare は `Preparation`、2入口は `AnalyzeCommand` と `DeleteCommand`、entries は `EntriesList`、表示は `ReportText`・`AnalyzeOutput`・`DeleteOutput`。`ClassificationPipeline`、`DeletionPhase`、`UnextractRunner`、`AnalysisReport`、`TargetSnapshot`、`MatchedFile`、`DeletionRequest`、`DeletionReport`、`ComparisonPass` は削除した。`IConfirmationPrompt` は `Unextract.Core.Commands` に移した。`Unextract.Windows` の製品コードは変更していない。`analyze` は親 File ID を取得しない (照合は `delete` だけ)。

## 7. 完了判定と成果物

- [`PLAN_TESTS.md`](PLAN_TESTS.md) の Core、Win、CLI、E2E、PTY、手動の必須テストを実行し、失敗・未実施・前提不成立の項目を区別する。手動の項目は [`MANUAL_TESTS.md`](MANUAL_TESTS.md) (M 系) の手順で行う。
- E2E を、ビルド済みの exe と、`scripts/run-e2e-tests.ps1` による publish 版の単一ファイル exe の両方で実行し、全件が合格する。
- [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) に G5 の実測結果と実施環境を記録し、新方式の必須テスト対応表の全項目にテストがあることを確認する。.NET のパッチ更新で ZipArchive の挙動が変わっても SPEC §5.2 の結果が変わらないことを Z・C 系テストで継続的に確認する。
- README に、完全削除で復旧手段がないこと、`analyze` と `delete` の役割、`analyze` の結果は削除の許可証でないこと、途中の STOP で削除済みのファイルが戻らないこと、`--entries` の形式、旧形式と `--dry-run` の廃止、内容比較候補の異常や共有違反の扱い (`analyze` は FATAL、`delete` は STOP / `DELETE_FAILED`)、64 GiB 上限、MISSING とサイズ不一致の内容を読まないこと、`analyze` → `delete` で I/O が約2倍になること (Strict)、独自 ZIP 構造パーサを作らない理由、CRC の位置づけ、SPEC §12 の限界を書く。Fast については、分類 `SAME_SIZE`、Fast の契約と保証しない事項、警告の表示箇所 (§5.2・§5.4)、Strict との性能差を書く。
- `src/`、`tests/`、`docs/`、`scripts/`、`.github/` に双方向制御文字 (U+202A〜U+202E、U+2066〜U+2069、U+200E、U+200F、U+061C) と不可視文字 (U+200B〜U+200D、U+2060〜U+2064、U+FEFF、U+00AD) が実際の文字として残っていないことを grep で確認する (結果が空であること)。テストやコードで必要な場合は `\u202E` のようなエスケープ表記で書く。
- 履歴は [`PLAN_HISTORY.md`](PLAN_HISTORY.md) に記す。仕様変更が必要なら SPEC を先に変更する。
