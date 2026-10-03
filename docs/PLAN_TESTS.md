# unextract MVP テスト計画

唯一の仕様基準は [`SPEC.md`](SPEC.md)。本書は仕様を検証するテストの観点と実施場所を定める。旧 v4 の #1〜#54 と X 番号は引き継がない。`Core` は副作用とエラーを注入できる偽ファイルシステムによる判定テスト、`Win` は一時 NTFS ディレクトリでの統合テスト、`CLI` は同一プロセス内で CLI を呼ぶテスト (`Unextract.Cli.Tests`)、`E2E` はビルド済みの exe を別プロセスとして起動するテスト (§11)、`PTY` は Windows PTY でのテスト、`手動` は実環境での確認 ([`MANUAL_TESTS.md`](MANUAL_TESTS.md) の M 系) を表す。ZIP の異常 fixture はテスト専用の生成器で、正常な ZIP のヘッダー値やデータをバイト単位で書き換えて作る (`PLAN_VALIDATION.md` の V2 と同じ手法)。

2026-10-03 の実行モデル改訂 (SPEC 冒頭、`PLAN_DECISIONS.md` DEC-24〜DEC-34) に合わせて本書を改訂した。旧方式 (サブコマンドなし・`--dry-run`・再オープンと2回目の比較) を前提にしたテスト (旧 D 系、Y 系、旧 O01〜O03・O05〜O07、旧 X01〜X15) は、新方式のテストに置き換える (対応は §14)。旧方式での実施結果は `PLAN_VALIDATION.md` に当時の記録として残る。新方式は 2026-10-03 に実装し、本書の自動テストを実施した (結果は `PLAN_VALIDATION.md` の「新方式の実装と検証」。手動の M 系は未実施)。

## 0. 共通の原則

**テストの系列**: `P` (Prepare と削除0件)、`C` (ZIP 内容の検証)、`Z` (ZIP 名・構造)、`R` (resource limits)、`T` (target 分類)、`A` (`analyze`)、`S` (逐次 `delete`)、`L` (`--entries`)、`K` (CLI の引数)、`O` (表示)、`X` (E2E)、`M` (手動)。`DEC-n` (判断の番号) とは別物。

**操作の割り当て**: 特に断らない限り、C、T の各テストは `analyze` で実施する (分類と FATAL の判定)。`delete` での同じ状況の扱いは S 系で確かめる。P、Z、R01〜R06 は Prepare の検査なので、`analyze` と `delete` (`--yes` 付き) の両方で実施し、どちらも同じ FATAL・入力エラー・削除0件になることを確認する。

**analyze の非破壊**: `analyze` を実行する全てのテストで、target 全体 (パス・サイズ・SHA-256・更新日時) と ZIP が実行前後で不変であることを確認する。Core では `OpenForDeletion`、`CheckIdentity`、`SetDispositionEx` が一度も呼ばれないことを呼び出し記録で確認する。

**削除0件**: Prepare (SPEC §3.1) の失敗と確認での中止を扱う全てのテストで、削除0件に加えて、target ルート以外の列挙・比較用オープン・削除用オープンが一度も呼ばれないこと (Core の呼び出し記録) を確認する。

**同じハンドル**: `delete` を扱う Core テストでは、各エントリでパスを使う呼び出しが `OpenForDeletion` 1回 (と、その失敗時の `CheckIdentity` 1回) だけで、`OpenForComparison` が一度も呼ばれないことを確認する。

**モード違いの再利用の原則** (SPEC §14、§15): 特に断らない限り、各テストの期待結果は Strict (既定) のものである。Strict と共通の安全性を確かめる次のテストは、同じ fixture・同じ種別で `--fast` を付けても実行する。

- 削除対象になる正常系の期待結果は、`MATCHED` を `SAME_SIZE` に読み替える (`delete` の `DELETED` はそのまま)。
- それ以外の、両モードに共通して適用される安全性の期待結果 (入力エラー、内容検証に依存しない FATAL・STOP、`MISSING`、`SKIPPED_SPECIAL_FILE`、削除0件、`DELETE_FAILED`、識別確認の判定、削除される対象と残る対象) は、Strict と同じとする。
- SPEC §5.2 の内容検証に依存する FATAL・STOP (内容比較候補の §5.2 の 1〜5 の違反、比較中の target の読み取り失敗、実測展開量) は、この原則に含めない。Fast では発生しない (SPEC §15.2)。Fast での扱いは C15、R09、T17、S06、S07 で確かめる。

対象: P02〜P08、Z01〜Z09、R01〜R06、T02〜T16 (T10 の「比較中の読取失敗」と T12 の「比較中 target-read 例外」の経路を除く)、A03 (FATAL の原因を内容に依存しないものにする)、A04〜A06、S01〜S04、S08〜S12、S16〜S24、S27〜S38 (S13〜S15、S25、S26 の比較中の経路を除く)、L 系、O08〜O10、O12〜O16。

**期待値の扱い**: 既存テストの期待値の変更は、SPEC 改訂に伴う書き換えとして行い、Skip や期待値の緩和はしない。**実測項目** (§12) は、期待結果を推測で確定せず、観測した結果を記録する。記録した結果が SPEC の記述・推定と異なれば SPEC と `PLAN.md` を見直す。

実削除を伴う Win・E2E テストは、テストが自分で作った一意な fixture ディレクトリの中のファイルだけを削除する。削除指示の直前のフック (H5) で、テスト側のガード (`DeletionGuard`) が削除用ハンドルの最終パスが fixture の内側であること (`\` 境界付きの比較) と、fixture から対象の親までの各ディレクトリが reparse point でないことを確かめ、違反なら例外で中止する。このガードはテストの安全装置であり、製品の安全装置の代わりにしない。ACL を変えるテストは `finally` で元に戻し、終了後に `icacls` で DENY が残っていないことを確認する。fixture のディレクトリ自体はテストから削除しない (PTY の例外は §11)。

## 1. Prepare と削除0件

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| P01 | 先頭に MATCHED が複数、後方の内容比較候補に CRC 不一致エントリ | `analyze`: FATAL (終了 1)。先頭の MATCHED は判定済みとして表示、原因エントリを表示、未判定は件数のみ。`delete --yes` の同じ fixture は S10 (先頭は削除され、CRC 不一致で STOP) | Core+Win |
| P02 | 先頭に削除対象、後方に ZIP 名不正・resource limits 超過 (それぞれ別 fixture) | `analyze`・`delete --yes` とも Prepare の FATAL、削除0件、target のエントリに触れない | Core+Win |
| P03 | ZIP が無効 (EOCD なし)、ZIP/target の読取権限なし | 入力エラーまたは FATAL、削除0件 | Core+Win |
| P04 | 実行中に別プロセスが ZIP を書き込み用に開く・改名する | ZIP ハンドル保持により失敗し、ZIP は変わらない。既に書き込み用に開かれている ZIP は unextract 側のオープンが失敗して FATAL | Win |
| P05 | 空 ZIP、空 target、削除対象0件 | `analyze`: 終了 0。`delete --yes`: 終了 0、削除0件。`delete` (確認あり) では確認プロンプトが表示される (SPEC §3.2。案 A) | Core+Win |
| P06 | target が存在しない、ファイルである、非 NTFS、危険なルート/システム領域、最終成分が reparse | 入力エラー、作成・削除0件 | Core+Win |
| P07 | target の最終成分が junction (確認用ハンドルの `FileAttributeTagInfo` で判定)。確認用ハンドルでの確認の後、保持用ハンドルで開くまでに target が差し替えられ、File ID が変わる | どちらも入力エラー、作成・削除0件 (SPEC §3.1 の手順5) | Core+Win |
| P08 | target の最終パスが `\\?\UNC\` で始まる。`\\?\C:\` 形式の target の最終パスから組み立てた期待パスと、開いたハンドルの最終パスの序数比較 (一致 / 大小文字だけ違う) | UNC は入力エラー。期待パスは `\\?\` 接頭辞を含むまま比較され、一致なら次の判定へ、大小文字だけの違いは `analyze` で FATAL、`delete` で STOP (SPEC §6.1 の手順5、§8.1) | Core |
| P09 | ZIP 自身の個体の取得失敗を注入 | `analyze`・`delete` とも FATAL、削除0件、target のエントリに触れない | Core |
| P10 | `delete` の確認で `n`・空入力・EOF、非対話で `--yes` なし | 終了 2、削除0件。target のエントリに触れない (列挙・削除用オープン0回)。中止の文言 | Core+Win |
| P11 | `delete` で、Prepare の各段階 (引数、ZIP、entries、拒否位置、target ルート、ZIP 全体検査、ZIP 自身の個体、entries の照合) を1つずつ失敗させる | いずれも削除0件、確認プロンプトなし、列挙・削除用オープン0回、「削除開始前に中止しました。削除0件。」 | Core |

## 2. ZIP 内容の検証と CRC (target 状態との組み合わせ)

同じ壊れたエントリ `x.bin` (宣言 `Length` = N) を含む ZIP を、target 側の状態だけを変えて `analyze` で実行する。`delete --yes` での「サイズ = N」の列の扱いは S13 (Strict は STOP) と S07 (Fast は DELETED)、他の列は `delete` でも同じ分類 (削除しない) になる。

| ID | `x.bin` の異常 | target: 不存在 | target: サイズ ≠ N | target: サイズ = N | 種別 |
|---|---|---|---|---|---|
| C01 | Central Directory の CRC-32 だけを書き換え (データは正常、target と全バイト一致) | MISSING、FATAL なし | MODIFIED、FATAL なし | **FATAL** (ランタイムは例外を出さず、unextract の CRC 照合で検出) | Core+Win |
| C02 | Deflate データの途中を破損 | MISSING | MODIFIED | FATAL (`InvalidDataException`) | Core+Win |
| C03 | 圧縮サイズを半分にして Deflate を途中で切る | MISSING | MODIFIED | FATAL (ランタイムは例外なく短く終わる。バイト数不足で検出) | Core+Win |
| C04 | 宣言 `Length` を実データより小さくする (Deflate) | MISSING | MODIFIED | FATAL (出力は `Length` で切られる。CRC 不一致で検出) | Core+Win |
| C05 | 宣言 `Length` を実データより大きくする (Deflate、Stored) | MISSING | MODIFIED | FATAL (バイト数不足) | Core+Win |
| C06 | Stored で宣言 `Length` が圧縮サイズより小さい | MISSING | MODIFIED | FATAL (`Length` 超過の時点で中断し、それ以上読まない) | Core+Win |
| C07 | 暗号化フラグ付き (データは平文の Deflate のまま) | MISSING | MODIFIED | FATAL (ランタイムは読めてしまう。`IsEncrypted` で `Open()` 前に検出) | Core+Win |
| C08 | 未対応圧縮方式 (LZMA=14、BZip2=12、AES=99) | MISSING | MODIFIED | FATAL (`Open()` が `InvalidDataException`) | Core+Win |

MISSING とサイズ不一致の列では、テスト用フックで `ZipArchiveEntry.Open()` が呼ばれないこと、CRC を計算しないことも確認する。

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| C09 | 内容比較候補で先頭付近のバイトが target と異なり、かつ後方で C02 の破損 | MODIFIED ではなく FATAL (読み切りを省略しない)。不一致位置を変えても結果が同じ。`delete` では STOP (S13) | Core+Win |
| C10 | 内容比較候補で内容が1バイト異なり、ZIP は健全 | MODIFIED。FATAL にならない | Core+Win |
| C11 | 0バイトエントリ: CRC=0 と target 0バイト / CRC を 0 以外に書き換え | MATCHED / FATAL | Core+Win |
| C12 | Data Descriptor 付きエントリ (非シーク出力で作成) が target と一致 | MATCHED (期待 CRC は Central Directory の値) | Core+Win |
| C13 | ランタイム回帰検知: C01・C03・C07 の fixture を `ZipArchive` 単体で読み、例外の有無を記録 | 例外の有無が `PLAN_VALIDATION.md` V2 と異なったら記録を更新する。どちらでも unextract の結果は FATAL のまま | Core |
| C14 | `ZipArchiveEntry.Crc32` を reflection なしで直接参照するコードが、`global.json` で固定した SDK でコンパイルできる | ビルドが通る | Core |
| C15 | C01〜C08 の各 fixture と、C11 の CRC を 0 以外に書き換えた0バイトエントリを `--fast` で、target の3状態で `analyze` | 不存在は MISSING、サイズ ≠ N は MODIFIED。サイズ = N は FATAL ではなく `SAME_SIZE` (SPEC §15.3 の表)。どの列でも `ZipArchiveEntry.Open()` が呼ばれず、CRC を計算しない (テスト用フック) | Core+Win |

CRC が削除の根拠ではないこと (CRC が合っていても全バイト不一致なら MODIFIED) は C10 で確認する。C15 は、メタデータは読めて内容検証でだけ失敗するエントリを対象とする。メタデータを得る前に失敗する ZIP (P03、Z09) は両モードとも入力エラーまたは FATAL になる。

## 3. ZIP 名・構造・種類 (Prepare)

`analyze` と `delete --yes` の両方で、全て FATAL・削除0件・target のエントリに触れないことを確認する。

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| Z01 | `../`、中間 `..`、絶対/ドライブ/UNC/デバイスパス、ADS コロン | FATAL、target 外へアクセスしない | Core |
| Z02 | `.`、空成分、NUL・制御文字、予約名 (拡張子付きを含む)、末尾ドット/空白、`<>"\|?*` | FATAL、名前は安全な表記で表示 | Core |
| Z03 | 同一名、大小文字だけ違う名、file/dir 同名、ZIP 内で file を親とする子 (`a` と `a/b.txt`) | FATAL、削除0件。target に `a/` がない場合の T02 (MISSING) と区別 | Core |
| Z04 | 外部属性の種別が symlink (`0xA000`)・FIFO・デバイス (ファイル名・ディレクトリ名の両方)、DOS 属性 reparse、区切りなしで DOS ディレクトリ属性、`Length` > 0 のディレクトリエントリ | FATAL | Core |
| Z04a | ファイルエントリ (`a.txt`) で上位16ビットの種別が `0x4000` (ディレクトリ) | FATAL | Core |
| Z04b | ディレクトリエントリ (`d/`) で上位16ビットの種別が `0x8000` (通常ファイル) | FATAL | Core |
| Z05 | ファイルエントリで種別が 0 または `0x8000`、ディレクトリエントリで種別が 0 または `0x4000`、DOS 属性 read-only/hidden/system/archive のみ | 受理し、それぞれ通常ファイル・ディレクトリとして分類 | Core |
| Z06 | UTF-8 フラグ付きの正しい UTF-8、フラグなしの CP437 (`é` `░` を含む)、フラグなしの UTF-8 バイト列 | それぞれ UTF-8、CP437 として復号した名前で照合。フラグなし UTF-8 は CP437 として読んだ別名になり、文字化け名での誤対応をしない | Core+Win |
| Z07 | UTF-8 フラグ付きで不正な UTF-8 | FATAL (復号名の U+FFFD を検出) | Core |
| Z08 | 正常な Stored/Deflate、ZIP64 (強制 ZIP64 エントリ、65,536 件超の ZIP64 EOCD)、DD、オフセット調整済み SFX、末尾ごみ付き | 読めて同じ分類になる。形式だけでは拒否しない | Core+Win |
| Z09 | 先頭にデータを付けただけでオフセットを調整していない ZIP | ZipArchive が開けず FATAL、削除0件 | Core |

## 4. resource limits

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| R01 | エントリ数 100,000 / 100,001 | 許可 / FATAL | Core |
| R02 | 名前 1,024 / 1,025 UTF-16 コード単位、深さ 128 / 129 成分 | 許可 / FATAL | Core |
| R03 | 既定値: メタデータ総量がちょうど 134,217,728 バイト / +2 バイトになる入力。+1 は上限を奇数にした注入で確認する | 許可 / FATAL。計算式 (名前 UTF-16 バイト数 + 128 × 件数) を検証 | Core |
| R04 | 長い名前で 100,000 件より前に 128 MiB を超える ZIP | FATAL (件数上限とは独立に発動) | Core |
| R05 | 1エントリの宣言 `Length` が 16 GiB / 16 GiB + 1 | 許可 / FATAL | Core |
| R06 | 宣言 `Length` 合計がちょうど 64 GiB / 64 GiB + 1、**全エントリが target に存在しない (MISSING)**。`delete` では超過の原因のエントリを `--entries` で選ばない場合も | 許可 / FATAL。target に触れる前に判定され、target の状態・操作の種類・`--entries` で結果が変わらない | Core+Win |
| R07 | 内容比較候補で実データが宣言 `Length` を超える (C06) | `Length` を超えた時点で読み取りを中断し、`analyze` は FATAL、`delete` は STOP。それ以上バッファを確保しない | Core |
| R08 | 実測合計上限: 上限を小さな値に差し替えるテスト用の注入で、累計が上限ちょうど / +1 (`analyze`、`delete` それぞれ) | 許可 / `analyze` は FATAL、`delete` は超えたエントリで STOP | Core |
| R09 | R08 と同じ注入で実測合計の上限を 0 にし、サイズ一致の内容比較候補を含む ZIP を `--fast` で `analyze`・`delete` | FATAL・STOP にならず `SAME_SIZE` / `DELETED`。実測展開量を計上しない | Core |

## 5. target 分類と byte 比較 (`analyze`)

各行の `delete` での扱いは「delete」の列に示す (詳細は S 系)。

| ID | 入力・操作 | 期待結果 (`analyze`) | delete | 種別 |
|---|---|---|---|---|
| T01 | 同一内容、1 byte 変更、サイズ違い、0 byte | 順に MATCHED、MODIFIED、MODIFIED (ZIP 内容を読まない)、MATCHED。mtime の違いは内容一致を覆さない | DELETED、MODIFIED、MODIFIED、DELETED | Core+Win |
| T02 | 親成分の分類表の MISSING の行: ZIP の `a/b/c.txt` に対し、target の `a` または `a/b` が (1) 存在しない、(2) 大小文字だけ違うディレクトリ、(3) 通常ファイル | いずれも `c.txt` は MISSING。ZIP 内容は開かない | 同じ (MISSING) | Core+Win |
| T03 | 親成分の分類表の reparse の行: `a/b` が (1) ディレクトリ junction、(2) ディレクトリ symlink、(3) ファイル symlink | いずれも SKIPPED_SPECIAL_FILE。リンク先を読まず変更しない | 同じ | Core+Win |
| T04 | 親成分の分類表の判定不能の行: 親成分の属性取得 API の失敗、想定外の種類を偽ファイルシステムで注入 | FATAL、削除0件 | STOP (S10) | Core |
| T05 | ZIP にない target ファイルが、ZIP エントリと同じディレクトリに多数ある | 実名確認の列挙で走査はされるが、表示・集計・分類・削除されず、照合結果にも保持されない | 同じ | Core+Win |
| T06 | 実名確認の規則: 大小文字違い (ファイル名、途中のディレクトリ名)、8.3 名でだけ一致、ディレクトリ単位で大文字小文字を区別する NTFS ディレクトリ内の大小文字違い | いずれも MISSING、削除しない | 同じ | Win |
| T07 | ADS (`Zone.Identifier` を含む)、hardlink (リンク数2)、ファイル symlink、read-only、system、temporary、offline、その他許可集合外の属性 | `SKIPPED_SPECIAL_FILE` (理由を表示)、内容を読まない | 同じ。reparse と許可外属性は削除用ハンドルを開かない (S19)。ADS・hardlink はハンドル上で判定 | Core+Win |
| T08 | archive、hidden、not-content-indexed、NTFS 圧縮、sparse、EFS 暗号化の各属性だけを持つ同一内容ファイル | MATCHED (これらだけを理由にスキップしない) | DELETED (EFS の削除は SPEC §13 の未確認事項のため、実施できた環境での結果を記録) | Core+Win |
| T09 | ZIP に file があり target は directory、ZIP 自身に対応する対象 | `SKIPPED_SPECIAL_FILE`。ZIP 自身を変更しない | ディレクトリは事前判定で `SKIPPED_SPECIAL_FILE` (S19)。ZIP 自身は共有違反後の識別確認一致で `DELETE_FAILED`、終了 1、ZIP を残し後続へ進む (S09) | Core+Win |
| T10 | ADS 列挙・属性・リンク数・File ID・親 File ID (`delete` のみ。`analyze` は取得しない)・最終パスの取得失敗、存在確認後のオープン失敗、比較中の読取失敗 | SKIP ではなく FATAL | STOP (オープン失敗の 32・5 は S16・S17) | Core+Win |
| T11 | 比較対象を別プロセスが書き込みで開いたままにする (エディタで編集中を模擬) | 比較用ハンドルのオープンが共有違反になり FATAL。原因のパスを表示 | 識別確認が一致すれば DELETE_FAILED で続行 (S16) | Win |
| T12 | 比較用ハンドルの扱い | 各エントリの判定終了時に閉じられ、同時に開くのは1つまで。結果表示の時点で target ファイルのハンドルが開いていない。旧スナップショット・候補リストを作らない | 削除用ハンドルについて S04 | Core+Win |
| T13 | 実名確認の失敗: 列挙用ハンドルのオープン失敗、列挙の途中のエラー、列挙用ハンドルの File ID・最終パスが列挙で見つけた項目と不一致、開いたハンドルの File ID が列挙で見つけた項目と不一致 | FATAL | STOP | Core |
| T14 | 列挙で (1) 同じ名前の項目が2回返る (File ID が異なる2件)、(2) 照合する名前が返らない | (1) 最初の1件だけを採用する。開いたハンドルの File ID が採用した項目と異なれば FATAL。(2) MISSING、ZIP 内容を開かない | (1) 異なれば STOP、(2) MISSING (S36) | Core |
| T15 | ディレクトリ単位で大文字小文字を区別する NTFS ディレクトリに `Foo` と `foo` (内容が異なる) が共存し、ZIP が `Foo` / `foo` / `FOO` を指す | `Foo` と `foo` はそれぞれ正しい個体に対応して比較される。`FOO` は MISSING | 同じ (それぞれ正しい個体を削除・残す) | Win |
| T16 | ZIP のファイルエントリに対し target がディレクトリで、その `FileStreamInfo` が `ERROR_HANDLE_EOF` で失敗する | FATAL にならず `SKIPPED_SPECIAL_FILE`。`Directory` を最初に判定し、ストリーム一覧などを取得しない | 事前判定で `SKIPPED_SPECIAL_FILE`、削除用ハンドルを開かない (S19) | Core+Win |
| T17 | T01 と同じ4つを `--fast` で | 順に `SAME_SIZE`、`SAME_SIZE`、MODIFIED、`SAME_SIZE`。ZIP エントリの `Open()` と target の内容の読み取りが行われない | DELETED、DELETED、MODIFIED、DELETED (S07) | Core+Win |

## 6. analyze (A 系)

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| A01 | 全カテゴリー (MATCHED、MODIFIED (内容違い・サイズ違い)、MISSING、SKIPPED_SPECIAL_FILE、DIRECTORY) と ZIP にない target ファイル。Strict | 終了 0。全エントリの結果行と合計行。ZIP にないファイルは出ない。target・ZIP 不変。`OpenForDeletion`・`CheckIdentity`・`SetDispositionEx` の呼び出しが0回。全バイト比較は内容比較候補ごとに1回 | Core+Win |
| A02 | A01 と同じ fixture を `--fast` | `SAME_SIZE` (MATCHED と内容違い)、MODIFIED (サイズ違いのみ)。ZIP の `Open()`・target の読み取りが無い。警告がヘッダーの先頭行 | Core+Win |
| A03 | 判定中の FATAL (100 件中 40 件目): CRC 不一致、比較対象の共有違反、列挙失敗 (それぞれ別 fixture) | 終了 1。判定済み 39 件の結果行、`FATAL: 1 エントリ (#40)`、未判定 60 件 (stdout)。原因と「analyze は削除を行いません (削除0件)」(stderr)。target 不変 | Core+Win |
| A04 | 先頭に MATCHED が複数、後方に ZIP 名不正 (Prepare の FATAL) | 終了 1。target のエントリに触れない (target ルート以外の列挙・比較用オープン0回)。判定済み 0、未判定 N | Core |
| A05 | `analyze` に `--yes` / `-y` / `--entries <file>` / `--dry-run` | 入力エラー (K 系と同じ)。target に触れない | Core |
| A06 | 比較用ハンドルの解放 (T12) | 各エントリの判定終了時に閉じられ、同時に開くのは1つまで。旧スナップショット・候補リストを持たない | Core |
| A07 | 競合のない同じ fixture で `analyze` → `delete --yes` (Strict、Fast それぞれ)。削除権限が無いファイル・共有違反のファイル・D1 で扱いが変わる対象は含めない | `analyze` の MATCHED (Fast は SAME_SIZE) と `delete` の DELETED が同じエントリの集合。MODIFIED・MISSING・SKIPPED も同じエントリ。(この一致は SPEC §2 のとおり保証ではなく、競合なし・権限差なしの条件での確認) | Core+Win+E2E |
| A08 | `analyze` の後、`delete` の前に、MATCHED のファイルを (a) 内容だけ書き換える (File ID・サイズ・`LastWriteTime`・`ChangeTime`・属性を書き戻す)、(b) 削除して同名で作り直す (同じ内容 / 違う内容)、(c) ADS を追加、(d) read-only を付ける | Strict: (a) MODIFIED で削除しない (`delete` の比較が現在の内容を見る)、(b) 同じ内容なら DELETED (新しい個体を現在状態で検証)、違う内容なら MODIFIED、(c) SKIPPED_SPECIAL_FILE (ADS)、(d) SKIPPED_SPECIAL_FILE (属性。事前判定で開かない)。Fast: (a) DELETED (SPEC §15.5 どおり)、(b) サイズで判定、(c)(d) Strict と同じ。`analyze` の結果を許可証として使っていないこと | Win |

## 7. 逐次 delete (S 系)

`delete` のフック (`PLAN.md` §4): エントリの解決の後・削除用オープンの直前 (H1)、オープン直後・照合の前 (H2)、全バイト比較中 = target の最初の読み取りの直前 (H3、Strict のみ)、最終確認の直前 (H4)、削除指示の直前 (H5)。

### 7.1 基本

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| S01 | A01 の fixture で `delete --yes` (Strict) | 終了 0。MATCHED に相当するファイルだけが削除され、MODIFIED・SKIPPED・ZIP にないファイル・ディレクトリ・ZIP (SHA-256 不変) は残る。結果行が処理順に出て、要約の件数が一致する | Core+Win |
| S02 | `delete --yes` で `y`/`Y` の確認、`--yes` | 確認後 (または `--yes` で確認なしで) 逐次処理を開始する。確認待ちの間、target ファイルのハンドルは開いていない (開いているのは ZIP と target ルートだけ) | Core+Win |
| S03 | 確認のタイミング (案 A) | 確認は Prepare の全段階の後、最初の target エントリの処理 (target ルート以外の列挙・削除用オープン) の前に1回だけ。確認の後に Prepare の検査は行わない | Core |
| S04 | 削除用ハンドルの解放 | 各エントリの処理の終了時 (DELETED・MODIFIED・SKIPPED・STOP・例外を含む) に閉じられ、同時に開く削除用ハンドルは1つまで。終了時に開いているのは ZIP と target ルートだけ | Core |
| S05 | 呼び出し順 (Strict) | 各エントリ: (ディレクトリの初回のみ) 列挙 → `OpenForDeletion` 1回 → 同じハンドルで File ID・親 File ID・最終パスの照合 → 情報取得と M0 → 同じハンドルからの読み取りと ZIP の `Open()` 1回 → 同じハンドルで最終確認 (M0 の全項目の再取得) → `SetDispositionEx(0x3)` 1回 → `DeletePending` → クローズ。`OpenForComparison` は0回。ZIP の `Open()` はそのエントリで1回だけ | Core |
| S06 | 呼び出し順 (Fast) | S05 から読み取りと ZIP の `Open()` を除いたもの。最終確認を含め、その他は同じ | Core |
| S07 | Fast で、同サイズ・内容違いのファイル (`delete` の前から)、ZIP 側が CRC 不一致・破損・暗号化の同サイズエントリ | どちらも DELETED (Fast の契約どおり)。ZIP の `Open()`・CRC 計算なし。Strict の同じ操作は MODIFIED / STOP | Core+Win |
| S08 | ディレクトリエントリ、削除後に空になるディレクトリ | ディレクトリは削除しない。DIRECTORY は結果行を出さず、要約の件数だけ | Core+Win |
| S09 | ZIP 自身が target 内にあり、ZIP にも同じ target path に対応する file entry と、その後に正常な削除対象がある | 削除用オープンが共有違反 (32)、列挙由来の基準との識別確認一致で `DELETE_FAILED`。終了コード 1。ZIP 自身は残り内容も変わらない。STOP せず後続へ進み、正常な後続対象は実際に削除される。Strict・Fast 共通の正式な回帰要件 (SPEC §8.1・§8.4、DEC-27) | Core+Win |

### 7.2 STOP と部分削除

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| S10 | エントリ 1〜3 が MATCHED、4 が target 側の判定不能 (列挙失敗・情報取得失敗・File ID 不一致・CRC 不一致をそれぞれ注入)、5 以降が MATCHED | 1〜3 は削除されたまま (復元しない)、4 は STOPPED で残る、5 以降は未処理で残り、オープンされない。終了 1。要約に削除済み 3・未処理件数、「元に戻りません」の明示。stderr に停止の原因 | Core+Win |
| S11 | 1件目で STOP | 削除0件、未処理 N−1、終了 1 | Core |
| S12 | 最後のエントリで STOP | 未処理 0、それ以前の削除は残る、終了 1 | Core |
| S13 | Strict の比較で ZIP 側の異常 (CRC 不一致、`Length` 超過、不足、読み取り例外、暗号化) | そのエントリは STOP で残り、以後は未処理。`DELETE_FAILED` で続行しない。それ以前の削除は残る | Core+Win |
| S14 | Strict で内容が1バイト違う同サイズのファイル | MODIFIED で続行 (STOP ではない)。後続の MATCHED は削除される | Core+Win |
| S15 | 比較中の target の読み取り失敗を注入 | STOP | Core |
| S16 | 削除用オープンが 32 (他プロセスが書き込み中 / `FILE_SHARE_DELETE` なしで読み取り中)、5 (READ_DATA の ACL 拒否 / 対象の DELETE と親の DELETE_CHILD の拒否) で、識別確認が列挙由来の基準と一致 | `DELETE_FAILED` で続行。理由に「内容は確認していません」を含む (Strict でも)。後続の安全な対象は削除される。終了 1。同じ状況の `analyze` は FATAL (T11) | Core+Win |
| S17 | 削除用オープンで同一性に疑義: 2 (消失)、3 (親の消失・ファイル化)、5 で識別確認が別の個体・ディレクトリ・ディレクトリ junction・削除保留中と判明、表に無いコード、識別確認自体の失敗 | STOP | Core+Win |
| S18 | 識別確認の基準が列挙由来であること: 識別確認の結果の File ID・ボリューム・親 File ID・最終パスのどれか1つだけを列挙由来の値と変える (注入) | いずれも STOP | Core |
| S19 | 事前判定 (D1): 最終成分の列挙項目が、ディレクトリ / ディレクトリ junction / ファイル symlink (作成できる環境のみ) / read-only / system / 未定義ビット | SKIPPED_SPECIAL_FILE (理由付き)。`OpenForDeletion` が呼ばれない。同じ fixture の `analyze` も SKIPPED_SPECIAL_FILE | Core+Win |
| S20 | 事前判定を通過した後、開いたハンドルで判定: ADS、hardlink (リンク数2)、ZIP 自身、ハンドル上の属性が許可外 (列挙項目は許可内、ハンドル上は許可外を注入) | SKIPPED_SPECIAL_FILE。ハンドル上の判定を省略しない | Core+Win |

### 7.3 競合 (フックと別プロセス)

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| S21 | H1 で対象を削除して同名で作り直す / 別ファイルで置換 (`MoveFileEx`) / 改名して消す | File ID 不一致で STOP、または 2 で STOP | Win |
| S22 | H1 で親ディレクトリを同名の別ディレクトリに差し替え、同じファイルを移して入れる (File ID と最終パスは不変) | オープン直後の親 File ID の照合 (列挙でたどった親との不一致) で STOP。削除しない | Win |
| S23 | H1 で途中のディレクトリを元を指す junction に差し替え / 大小文字だけ改名 | オープン直後の最終パスの不一致で STOP | Win |
| S24 | H2・H3・H4 で別プロセスが対象を書き込み用・改名用・削除用に開こうとする | 共有違反で失敗し、対象は検証した個体のまま削除される (Strict は一致時) | Win |
| S25 | H3 (比較中) に別プロセスが ADS 追加 / hardlink 追加 / read-only 付与 / 属性変更 | 最終確認で検出して STOP。そのファイルは残る | Win |
| S26 | **実測**: H3 (比較中) に祖先ディレクトリの改名を試みる | 改名が成功したか失敗したかと、そのときの結果を記録する。改名が成功した場合は最終確認の最終パス不一致で STOP し、削除しないことを判定する。失敗した場合はそのファイルの処理が通常どおり進むことを判定する (SPEC §13 の未実測事項) | Win |
| S27 | H4 で ADS 追加 / hardlink 追加 / read-only 付与 / 親を同名の別ディレクトリに差し替え (可能な場合) | 最終確認で STOP (M0 の全項目の照合) | Win |
| S28 | H5 で read-only 付与。前に削除済み、後に未処理がある | 削除指示が失敗して STOP、そのファイルは残る、未処理件数を表示、それ以前は戻らない | Win |
| S29 | 逐次処理中に対象以外だけを変更 (ZIP にないファイルの追加・変更、他エントリの読み取り、target 内の別ディレクトリの作成) | 誤って STOP しない | Win |
| S30 | 情報取得 API の失敗を照合・検査・最終確認の各段階で注入 | STOP | Core |
| S31 | 削除指示の失敗、成立確認の失敗 (`DeletePending` の取得失敗) | STOP。成立確認の失敗では「削除された可能性あり」と報告し、削除済み件数を正確に表示 | Core |
| S32 | 削除指示の呼び出し内容 | 削除用ハンドルに1回だけ、flags がちょうど 0x3 で 0x10 を含まない | Core |
| S33 | 削除指示が成功を返すが `DeletePending` が false のまま | DELETED にせず STOP、「削除された可能性あり」 | Core |
| S34 | **実測**: ACL で対象の DELETE だけを拒否 (親の DELETE_CHILD は許可)。拒否する時点を (a) `delete` の実行前、(b) H3 (比較中) | 結果 (DELETED / STOP、`DeletePending`、終了状態) を記録する。判定は誤削除がないこと (対象以外が残り、STOP なら対象も残ること)。`PLAN.md` §4 の推定 ((a) 削除される、(b) `ChangeTime` の変化で最終確認により STOP) と異なれば記録して見直す | Win |
| S35 | 列挙のキャッシュと逐次削除: 同じディレクトリに ZIP エントリが多数あり、前半を削除しながら後半を処理する。H1 でそのディレクトリに ZIP にない新しいファイルと、後続のエントリと同名の新しいファイル (列挙後に作成) を追加する | そのディレクトリの列挙は1回だけ (呼び出し記録)。自分の削除で後続の判定が変わらない。列挙後に作られた同名ファイルは MISSING (削除しない) | Core+Win |
| S36 | 列挙で同じ名前が2回返る / 照合する名前が返らない (T14 の `delete` 版) | 最初の1件を採用し、オープンの File ID が異なれば STOP。返らない名前は MISSING | Core |
| S37 | **実測**: 列挙の後 (H1) に read-only を付けたファイルを削除用ハンドルで開く | オープンが成功したか (5 で失敗したか) を記録する。成功した場合はハンドル上の属性判定で SKIPPED_SPECIAL_FILE、失敗した場合は識別確認により DELETE_FAILED になること、どちらでも削除しないことを判定する | Win |
| S38 | **実測**: 許可集合の各属性・許可外の各属性・reparse を付けた項目で、列挙 (`FileIdExtdDirectoryInfo`) の属性・reparse tag とハンドル (`FileBasicInfo`・`FileAttributeTagInfo`) の値を比較する | 一致・不一致を属性ごとに記録する。不一致があれば SPEC §7 の事前判定の記述を見直す (安全性は事前判定に依存しないことを S20 で確認済みであること) | Win |

## 8. `--entries` (L 系)

入力エラーは全て **終了 1・削除0件・確認プロンプトなし・target のエントリに触れない (target ルート以外の列挙・削除用オープン0回)**。L01〜L13・L15 は Core (純粋関数と Runner)、代表を CLI・E2E (X21) で確認する。

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| L01 | 正常: LF、CRLF、混在、末尾改行あり・なし、先頭に UTF-8 BOM 1個 | 全て受理し、同じ選択集合 | Core |
| L02 | 不正な UTF-8、UTF-16LE/BE の BOM、UTF-32 の BOM、UTF-8 BOM が2個 | 入力エラー。UTF-16 は「UTF-16 で保存されています。UTF-8 で保存してください」相当の案内。BOM 2個目は名前の一部となり未知エントリ (または不正) として入力エラー | Core |
| L03 | 空行 (途中、先頭、末尾の余分な空行)、空ファイル、BOM だけのファイル | 入力エラー (空行は行番号付き) | Core |
| L04 | 行末以外の CR、先頭・末尾の空白 (` a.txt` という ZIP エントリがあれば一致、無ければ未知) | CR は入力エラー。空白は trim せず名前の一部として照合 | Core |
| L05 | 重複行 | 入力エラー (両方の行番号) | Core |
| L06 | ZIP に無い名前、大小文字だけ違う名前、区切りだけ違う名前 (`bin\a.dll` と `bin/a.dll`) | 入力エラー。大小文字違い・区切り違いでは ZIP 側の正しい FullName をヒントに表示 | Core |
| L07 | ディレクトリエントリ (`docs/`)、明示エントリの無い暗黙ディレクトリの名前 (`docs`) | 入力エラー (前者は「ディレクトリエントリは指定できません」、後者は未知エントリ) | Core |
| L08 | `#` で始まる行、`*` を含む行 | コメント・glob として扱わず名前として照合し、無ければ未知エントリの入力エラー | Core |
| L09 | 上限: ファイルサイズ 128 MiB ちょうど / +1 バイト、1行 4,096 / 4,097 バイト、行数 100,000 / 100,001 (テスト用に小さくした上限の注入でも確認) | 許可 / 入力エラー。サイズ上限を超えるファイルは全体を読まない | Core |
| L10 | entries の誤りがファイルの最後の行にあり、先頭の行は全て MATCHED のエントリ | 入力エラー、削除0件 (途中まで削除しない) | Core+Win |
| L11 | 正常な entries で `delete --yes` | 指定したエントリだけを処理する。指定外は MATCHED でも削除しない・解決もしない (指定外のエントリだけを含むディレクトリは列挙しない。呼び出し記録) | Core+Win |
| L12 | entries で指定したエントリが MISSING / MODIFIED / SKIPPED | それぞれの結果で続行。STOP・DELETE_FAILED が無ければ終了 0 | Core |
| L13 | 指定外のエントリに ZIP 名不正・宣言合計の超過がある | FATAL、削除0件 (ZIP 全体の事前検査は `--entries` に関係なく行う) | Core |
| L14 | `analyze` の表示の Entry をそのまま entries に書く (日本語名、CP437 名、`\` 区切りの名前、先頭空白の名前) | 全て一致して選択される | Core+E2E |
| L15 | 書式文字 (`\u200B` など) を含むエントリ名 | `analyze` の表示はエスケープされ転記不可の印が付く。実際の文字を UTF-8 で書いた entries では一致する | Core |
| L16 | entries ファイルが target 内にあり、ZIP にも同名・同内容のエントリがある | entries は確認の前に読み終えて閉じている。そのファイルが削除されても処理は正常に続く | Win |
| L17 | entries ファイルが存在しない・読めない・ディレクトリ | 入力エラー、削除0件 | Core+Win |
| L18 | **実測**: Windows PowerShell 5.1 で `unextract analyze ... > out.txt` として保存したファイルから作った entries (`Get-Content`/`Set-Content` を使わずリダイレクトしたもの) と、`Out-File -Encoding utf8` で保存したもの | リダイレクトの保存形式 (UTF-16LE と BOM か) を記録する。UTF-16 なら L02 の入力エラーと案内になること、`Out-File -Encoding utf8` (BOM 付き UTF-8) なら L01 として受理されることを判定する | E2E または手動 (M13) |

## 9. CLI の引数 (K 系)

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| K01 | `analyze`・`delete` の正常な引数 (オプションの順序を変えたもの、`--fast`・`--entries`・`--yes`/`-y` の組み合わせ) | 受理。モード・entries・yes が正しく渡る。`--fast` なしは Strict | Core+CLI |
| K02 | サブコマンドなし (旧形式 `unextract a.zip --target d`、`unextract a.zip --target d --yes`)、不明なサブコマンド、大小文字違い (`Analyze`、`DELETE`) | 入力エラーと使い方 (両サブコマンド)。旧形式は廃止の案内。削除処理を開始しない | Core+CLI+E2E |
| K03 | `--fast` の重複、`--entries` の重複・値なし・`--entries=x`・値が `-` 始まり・空、`--yes` と `-y` の併用、`--target` の各不正 (旧方式と同じ)、ZIP なし・2つ | 入力エラー | Core |
| K04 | `--dry-run` (`analyze`・`delete`・旧形式のそれぞれで、どの位置でも) | 入力エラーで `analyze` を案内。削除処理を開始しない | Core+CLI+E2E |
| K05 | `analyze` に `--yes` / `-y` / `--entries` | 入力エラー | Core |
| K06 | 使い方の表示 | 両サブコマンドの形 (`PLAN.md` §5.5) | Core |

## 10. 表示 (O 系)

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| O04 | 表示できない名前 (制御文字など、FATAL の原因として) | エスケープ表記とエントリ番号で表示 (既存の `SafeDisplay.Escape` の表記) | Core |
| O08 | `analyze` の行形式 | 状態名 20 桁 + 空白2 + Entry + ` -> ` + Target。Entry は 23 桁目から。カテゴリー順・カテゴリー内 ZIP 順。0件のカテゴリーは行なし、合計行には全カテゴリー (0件を含む) | Core |
| O09 | Entry に `\`、先頭空白、日本語、CP437 由来の文字を含む名前 | Entry は変換なし (`\` を `\\` にしない)。Target は `\\?\` を除いた target の最終パス + 成分 | Core |
| O10 | 書式文字 (補助平面の Cf を含む)・C1 制御・U+2028 を含む名前 | Entry・Target ともエスケープ表示と転記不可の印。FATAL の原因もエスケープ。通常の補助平面文字は変換しない | Core |
| O11 | Strict / Fast のカテゴリーと警告 | Strict に `SAME_SIZE` なし、Fast に `MATCHED` なし。Fast は `analyze` のヘッダーの先頭行に警告 (標準出力)。Strict では出ない。結果表示に至らない終了 (入力エラー、ZIP を開けない) では両モードとも出ない | Core |
| O12 | `analyze` の FATAL 表示: Prepare の FATAL と判定中の FATAL | 判定中: 判定済みの結果行・`判定済み`・`FATAL: 1 エントリ (#n)`・`未判定` (stdout)、原因と「analyze は削除を行いません (削除0件)」(stderr)。Prepare の FATAL: 結果行なし、`判定済み: 0 エントリ`・`未判定` (stdout) | Core |
| O13 | `delete` の逐次出力 | 各ファイルエントリの結果が処理順に1行ずつ stdout に出る (エントリの処理ごとのコールバックの順序で確認)。DIRECTORY・処理対象外は行なし。要約の件数が結果行と一致し、選択対象外と STOP 後の未処理を区別する。結果行・要約の出力失敗後も削除0件と断定しない (削除の副作用も確認)。CLI の例外報告自体が失敗した場合も同じ | Core+CLI |
| O14 | `delete` の確認プロンプトと Fast 警告の位置 | 確認は Prepare の後・最初のエントリ処理の前に1回 (案 A)。文言は `PLAN.md` §5.4。Fast は `delete` のヘッダーの先頭行と確認の直前の行に警告。`--yes`・非対話では確認と直前の警告は出ず、ヘッダーの警告だけ。Strict では出ない | Core+PTY |
| O15 | `delete` の STOP・`DELETE_FAILED`・削除された可能性あり | stdout に `STOPPED` 行・要約・「元に戻りません」「削除していません」(または「削除された可能性があります」)・「未処理の g 件には触れていません」。stderr に停止の原因。`DELETE_FAILED` の行に理由 (内容未確認を含む)。STOP なしで `DELETE_FAILED` があればエラー終了の理由 | Core |
| O16 | 進捗と逐次結果の混在 (stdout と stderr を同じ書き込み先にした偽の端末) | 結果行が進捗の行の途中に続かない。stderr がリダイレクトされていれば進捗なし。`analyze` は `Checking n / total`、`delete` は `Processing n / total` | CLI+手動 (M05) |

## 11. E2E (X 系)

`tests/Unextract.E2E.Tests` の X 系は、`unextract.exe` を `Process.Start` で別プロセスとして起動し、終了コード・stdout・stderr・ファイルシステムの結果を検証する。プロセス境界の挙動 (標準入力のリダイレクトによる非対話の判定、stdout と stderr の分離、出力のエンコーディング、終了コード、単一ファイル exe での動作) を担う。同じ project に Windows PTY テストも置く。

- 起動する exe: 環境変数 `UNEXTRACT_E2E_EXE` があればそれ (publish 済みの単一ファイル exe。`scripts/run-e2e-tests.ps1`、CI の E2E ステップ)。なければテストアセンブリの位置から相対で `src/Unextract.Cli/bin/<構成>/<TFM>/unextract.exe`。どちらでも見つからなければ失敗にする (成功・前提不成立にしない)。
- X 系は stdin・stdout・stderr をリダイレクトし、stdout・stderr は UTF-8 で読む。60 秒で終わらなければプロセスツリーを kill して失敗にする。`Porta.Pty 2.2.2` は E2E test project だけの依存で、製品コードには PTY 関連依存を加えない。
- 作業ディレクトリはテストの出力先の `fixtures/<テスト名>-<GUID>/`。X 系の fixture はテストから削除しない。PTY テストは自分が作った GUID 付き fixture を、PTY / process tree の終了・Dispose 完了後に削除する。cleanup failure はテストを失敗させ、元の失敗情報と terminal output を保持する。junction・ACL・symlink は使わない。ZIP は生成器 (`ZipFixture`、`ZipPatcher`) をリンクで共有して作る。
- 実削除を伴う実行 (`delete --yes`、および誤って削除に入った場合に備えて中止の実行) の前に、領域外ガード (`DeletionGuard` をリンクで共有) で、target と target 内の全ディレクトリの最終パスが fixture の内側であり、各成分が reparse point でないことを確かめる。違反なら例外で中止し、exe を起動しない。

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| X16 | 全カテゴリー (MATCHED、MODIFIED (内容違い・サイズ違い)、MISSING、SKIPPED_SPECIAL_FILE (ZIP ではファイル、target ではディレクトリ)、DIRECTORY) と ZIP にない target ファイルを含む fixture で `analyze` | 終了 0。target 全体と ZIP が不変。stdout にヘッダー・凡例・全エントリの結果行・合計行。ZIP にないファイルは出力されない。stderr は空 | E2E |
| X17 | X16 と同じ fixture で `delete --yes` | 終了 0。MATCHED だけが削除され、他は残る (ZIP の SHA-256 不変)。stdout に結果行と `要約: 削除済み 2、...、未処理 0` | E2E |
| X18 | X16 と同じ fixture で `delete` (`--yes` なし)。stdin は (1) 何も書かずに閉じる、(2) `n\n`、(3) `y\n` | いずれも非対話として中止。終了 2、削除0件。stdout に非対話の文言と「中止しました。削除0件。」、`[y/N]` は表示されない | E2E |
| X19 | 先頭に MATCHED 2件、3番目に Central Directory の CRC-32 だけを書き換えたエントリ (target にサイズ一致のファイルあり)、後方に MATCHED 2件 | `analyze`: 終了 1、判定済み 2・FATAL #3・未判定 2 (stdout)、原因 (stderr)、target 不変。`delete --yes`: 終了 1、1・2 番目は削除、3 番目は STOPPED で残る、4・5 番目は未処理で残る。stdout に部分削除の明示、stderr に停止の原因 (`#3 "bad.txt"`) | E2E |
| X20 | 引数の不正: 旧形式 (`unextract a.zip --target d`、`--yes` 付き)、`--dry-run`、サブコマンドなし・不明、`--target=dir`、ZIP なし・2つ、重複、`--entries` の値なし、`analyze --yes` | 終了 1。stderr に入力エラーと使い方 (旧形式・`--dry-run` は案内付き)。stdout は空。fixture 全体が不変 | E2E |
| X21 | `--entries` の正常 (2件だけを指定して `delete --yes`) と入力エラー (最後の行が未知エントリ / UTF-16 で保存 / 空行) | 正常: 指定の2件だけが処理され、指定外の MATCHED は残る。入力エラー: 終了 1、削除0件、stderr に行番号付きの入力エラー | E2E |
| X22 | target が存在しない、target がファイル、ZIP が存在しない。`delete --yes` | 終了 1。stderr に入力エラー (ZIP が開けない場合は FATAL) と削除0件の明示。stdout は空。存在しない target は作成されない | E2E |
| X23 | UTF-8 フラグ付きの日本語名の ZIP、UTF-8 フラグなしで CP437 の名前バイト (`café░.txt`) の ZIP で `analyze` → stdout の Entry をそのまま entries に書いて `delete --entries ... --yes` | stdout (UTF-8) に名前がそのまま出る (U+FFFD なし)。entries が受理され、MATCHED のファイルだけが削除される。`UNEXTRACT_E2E_EXE` の単一ファイル exe でも同じ | E2E |
| X24 | `analyze`、`delete --yes`、STOP する `delete`、入力エラーの各実行 | 結果行・要約は stdout、FATAL・STOP の原因・入力エラーは stderr。stderr がリダイレクトされているため進捗 (`Checking`、`Processing`、改行を伴わない CR) は出ない | E2E |
| X25 | 削除対象0件 (全て MODIFIED・MISSING)、空 ZIP で `delete --yes` / `delete` (`--yes` なし、stdin 空) | `--yes`: 終了 0、削除0件、要約。`--yes` なし: 非対話として中止 (終了 2)、削除0件 (案 A では削除対象0件でも確認が必要なため) | E2E |
| X26 | Fast: X16 の fixture で `analyze --fast`、`delete --fast --yes`。X19 の fixture で `delete --fast --yes` | `analyze --fast`: 先頭行が警告、`SAME_SIZE` に MATCHED と内容違い、MODIFIED にサイズ違いだけ、`MATCHED` は出ない。`delete --fast --yes`: 先頭行が警告、`SAME_SIZE` に相当するファイルだけを削除。X19 の fixture: STOP せず CRC を書き換えたエントリの target も削除、終了 0 | E2E |
| X27 | 終了コード 0 / 1 / 2 | X16〜X26 の各実行の終了コードが SPEC §2 どおり | E2E |
| X28 | PTY: `delete` の対話実行で、確認の表示後に `n` を送る (Strict、Fast) | 終了 2、target 非削除。確認文と `[y/N]` が表示される。Fast はヘッダーと確認の直前に警告 (計2回)、最後の警告から確認文・`[y/N]` までに別出力が無い。Strict は警告なし (旧 M08 / O07 の PTY テストの置き換え) | PTY |

**手動に残す項目** ([`MANUAL_TESTS.md`](MANUAL_TESTS.md) の M 系): 実端末での確認入力 (空 Enter・`y` を含む)、Ctrl+C、進捗と逐次結果の見え方、コードページとフォントによる表示、確認待ち・逐次処理中の別ウィンドウからの変更、Fast の警告の視認性、§12 の実測項目のうち手動で行うもの。PTY による機能確認は、人間が実端末で見る表示の検証を代替しない。

## 12. 実測項目 (G5)

期待結果を推測で確定せず、観測した結果を `PLAN_VALIDATION.md` に記録するテスト。結果がどちらでも削除しない側に倒れることを判定する。

| ID | 未実測の事項 (SPEC §13) | 実施 |
|---|---|---|
| S26 | 配下のファイルの削除用ハンドルを開いている間の祖先ディレクトリの改名 | Win (自動) |
| S34 | 対象の DELETE だけを ACL で拒否した場合の新方式での結果 | Win (自動) |
| S37 | read-only のファイルを削除用ハンドルで開けるか | Win (自動) |
| S38 | 列挙の属性・reparse tag とハンドルの値の一致 | Win (自動) |
| L18 | PowerShell 5.1 のリダイレクトの保存形式と entries の扱い | E2E または手動 M13 |
| M09 | 比較用・削除用ハンドル (相当) の同時オープン | 手動 (近似) |
| M10 | `delete` の途中の Ctrl+C と、削除の指示の後・クローズ前の中断の扱い (`DeletePending`) | 手動 |
| M11 | 書き込み可能なメモリマップ経由の書き込み | 手動 (手順未確立。未実施のまま残す) |
| M12 | 祖先ディレクトリの改名、read-only の DELETE オープン (S26・S37 の近似による補助) | 手動 (近似) |

## 13. 実機確認と終了条件

- SPEC §13 の PoC 1〜8 は実施済み (旧方式。結果は新方式にも適用する) で、結果は [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) にある。§13 の未確認・未実測の事項は、実装テストまたは実機確認で扱いを決めるまで成立と見なさない (SPEC §14)。
- 実削除テストは作業専用の一時ディレクトリと自作 fixture だけで行い、ZIP 自身、無関係のファイル、ディレクトリが残ることを確認する。
- Core の結果だけで Windows API の同一個体保証を主張しない。実 Win で再現不能なケースは未確認として残す。
- Fast (SPEC §14 の追加条件): モード違いの再利用の原則に挙げたテストが `--fast` でも通り、C15、R09、T17、S06、S07、X26 が通ること。Fast で同サイズの変更ファイルが削除されること (T17、S07、A08 (a)、X26) は仕様どおりの挙動として扱い、失敗としない。
- 全必須項目が通り、実測項目 (§12) の結果が記録され、`analyze` が何も削除しないこと、Prepare の失敗と確認の中止で削除0件、`--entries` の入力エラーで削除0件、比較したハンドルでの削除、`DELETE_FAILED` で続行、STOP で以後未処理・それ以前の削除は残ることが確認できた時点を MVP のテスト完了とする。

## 14. 旧テストとの対応 (2026-10-03 の改訂)

旧方式のテストのうち、ID を残したもの (P、C、Z、R、T、O04) は期待結果を上のとおり改めた。置き換えたものの対応は次のとおり。旧方式での実施結果は `PLAN_VALIDATION.md` に残る。

| 旧 | 新 |
|---|---|
| D01 (中止・非対話で削除0件) | P10 |
| D02 (`y`・`--yes` で削除候補だけを削除) | S01、S02 |
| D03、D24 (再オープン方式の呼び出し順) | S05、S06 |
| D04、D05 (確認待ち中の変更で停止) | 確認が比較より前になったため、A08 (`analyze` 後の変更は現在状態で判定) と S21〜S27 (逐次処理中の競合) |
| D06 (再検証の情報取得の失敗) | S30 |
| D07 (無関係な変更で止まらない) | S29 |
| D08 (削除指示・成立確認の失敗) | S31 |
| D09、D23 (削除用ハンドルを開いている間は書き込み・改名できない) | S24 |
| D10 (内容だけの書き換えを再比較で検出) | A08 (a) |
| D11 (オープン拒否で識別確認が一致 → `DELETE_FAILED`) | S16 |
| D13 (再比較中の ZIP 異常で停止) | S13 |
| D14、D15 (オープンで同一性に疑義・未知のコード → 停止) | S17、S18 |
| D16 (最終確認直前の変更) | S27 |
| D17、D18 (削除指示の内容、`DeletePending` が false) | S32、S33 |
| D19 (親を同名の別ディレクトリに差し替え) | S22 (オープン直後の親 File ID の照合) |
| D20 (最終パスだけが違う差し替え) | S23 |
| D21 (削除指示直前の read-only 付与) | S28 |
| D22 (対象の DELETE だけを ACL で拒否) | S34 (実測) |
| D25 (Fast で内容違いが削除される) | S07、A08 (a) |
| D26 (`--fast` の解析) | K01、K03 |
| Y01、Y02 (dry-run と通常実行の一致) | 廃止 (`--dry-run` の廃止。DEC-24)。`analyze` と `delete` の対応は A07 (保証ではない)、時間差の扱いは A08 |
| O01、O05 (全カテゴリーの表示、モード別カテゴリー) | O08、O11 |
| O02 (解析途中の FATAL の表示) | O12 |
| O03 (削除途中の停止と `DELETE_FAILED` の表示) | O15 |
| O06、O07 (Fast の警告の位置) | O11、O14、X28 |
| X01〜X15 | X16〜X28 |
