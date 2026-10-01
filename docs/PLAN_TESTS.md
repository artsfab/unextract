# unextract MVP テスト計画

唯一の仕様基準は [`SPEC.md`](SPEC.md)。本書は仕様を検証するテストの観点と実施場所を定める。旧 v4 の #1〜#54 と X 番号は引き継がない (§10 の E2E の X 系は新しく付けた番号で、旧 v4 の X 番号とは無関係)。`Core` は副作用とエラーを注入できる偽ファイルシステムによる判定テスト、`Win` は一時 NTFS ディレクトリでの統合テスト、`E2E` はビルド済みの exe を別プロセスとして起動するテスト (§10)、`手動` は実環境での確認 ([`MANUAL_TESTS.md`](MANUAL_TESTS.md) の M 系) を表す。ZIP の異常 fixture はテスト専用の生成器で、正常な ZIP のヘッダー値やデータをバイト単位で書き換えて作る (`PLAN_VALIDATION.md` の V2 と同じ手法)。最初の依頼の必須12項目との対応は `PLAN_VALIDATION.md` の対応表にある。

**dry-run 一致の原則**: P、C、Z、R、T、O の各テストは `--dry-run` と通常実行 (確認に `n` を入力) の両方で実行し、**確認・削除フェーズに入る前までの初回分類**、FATAL 判定、表示内容 (確認プロンプトの有無を除く) が一致することを確認する。削除直前再検証は dry-run では行わないため、一致の対象に含めない (Y02)。

## 1. 削除開始前の全件ゲート

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| P01 | 先頭に MATCHED が複数、後方の内容比較候補に CRC 不一致エントリ | 全体 FATAL、`--yes` 付きでも削除0件。先頭の MATCHED は判定済みとして表示、原因エントリを表示、未判定は件数のみ | Core+Win |
| P02 | 先頭に MATCHED、後方に ZIP 名不正・resource limits 超過・target 安全判定 API 失敗・比較対象の共有違反 (それぞれ別 fixture) | いずれも全体 FATAL、削除0件 | Core+Win |
| P03 | ZIP が無効 (EOCD なし)、ZIP/target の読取権限なし | 入力エラーまたは全体 FATAL、削除0件 | Core+Win |
| P04 | 実行中に別プロセスが ZIP を書き込み用に開く・改名する | ZIP ハンドル保持により失敗し、ZIP は変わらない。既に書き込み用に開かれている ZIP は unextract 側のオープンが失敗して入力エラー | Win |
| P05 | 空 ZIP、空 target、削除候補0件 | 正常終了、削除0件、確認プロンプトなし | Core+Win |
| P06 | target が存在しない、ファイルである、非 NTFS、危険なルート/システム領域、最終成分が reparse | 入力エラー、作成・削除0件 | Core+Win |
| P07 | target の最終成分が junction (確認用ハンドルの `FileAttributeTagInfo` で判定)。確認用ハンドルでの確認の後、保持用ハンドルで開くまでに target が差し替えられ、File ID が変わる | どちらも入力エラー、作成・削除0件 (SPEC §3 の手順1) | Core+Win |
| P08 | target の最終パスが `\\?\UNC\` で始まる。`\\?\C:\` 形式の target の最終パスから組み立てた期待パスと、比較用ハンドルの最終パスの序数比較 (一致 / 大小文字だけ違う) | UNC は入力エラー。期待パスは `\\?\` 接頭辞を含むまま比較され、一致なら次の判定へ、大小文字だけの違いでも FATAL (SPEC §6.1 の手順4、§8.1) | Core |

## 2. ZIP 内容の検証と CRC (target 状態との組み合わせ)

同じ壊れたエントリ `x.bin` (宣言 `Length` = N) を含む ZIP を、target 側の状態だけを変えて実行する。各行を `--dry-run` と通常実行の両方で行い、初回分類と FATAL 判定が一致することを確認する。

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
| C09 | 内容比較候補で先頭付近のバイトが target と異なり、かつ後方で C02 の破損 | MODIFIED ではなく FATAL (読み切りを省略しない)。不一致位置を変えても結果が同じ | Core+Win |
| C10 | 内容比較候補で内容が1バイト異なり、ZIP は健全 | MODIFIED。FATAL にならない | Core+Win |
| C11 | 0バイトエントリ: CRC=0 と target 0バイト / CRC を 0 以外に書き換え | MATCHED / FATAL | Core+Win |
| C12 | Data Descriptor 付きエントリ (非シーク出力で作成) が target と一致 | MATCHED (期待 CRC は Central Directory の値) | Core+Win |
| C13 | ランタイム回帰検知: C01・C03・C07 の fixture を `ZipArchive` 単体で読み、例外の有無を記録 | 例外の有無が `PLAN_VALIDATION.md` V2 と異なったら記録を更新する。どちらでも unextract の結果は FATAL のまま | Core |
| C14 | `ZipArchiveEntry.Crc32` を reflection なしで直接参照するコードが、`global.json` で固定した SDK でコンパイルできる | ビルドが通る (導入版の確認を継続する) | Core |

CRC 不一致の検出は C01・C04・C11 で直接テストし、DD 付きエントリの期待 CRC は C12、削除フェーズの再比較での CRC 不一致は D13 でテストする。CRC が削除の根拠ではないこと (CRC が合っていても全バイト不一致なら MODIFIED) は C10 で確認する。

## 3. ZIP 名・構造・種類

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| Z01 | `../`、中間 `..`、絶対/ドライブ/UNC/デバイスパス、ADS コロン | 全体 FATAL、target 外へアクセスしない | Core |
| Z02 | `.`、空成分、NUL・制御文字、予約名 (拡張子付きを含む)、末尾ドット/空白、`<>"\|?*` | 全体 FATAL、名前は安全な表記で表示 | Core |
| Z03 | 同一名、大小文字だけ違う名、file/dir 同名、ZIP 内で file を親とする子 (`a` と `a/b.txt`) | 全体 FATAL、削除0件。target に `a/` がない場合の T02 (MISSING) と区別 | Core |
| Z04 | 外部属性の種別が symlink (`0xA000`)・FIFO・デバイス (ファイル名・ディレクトリ名の両方)、DOS 属性 reparse、区切りなしで DOS ディレクトリ属性、`Length` > 0 のディレクトリエントリ | 全体 FATAL | Core |
| Z04a | ファイルエントリ (`a.txt`) で上位16ビットの種別が `0x4000` (ディレクトリ) | 全体 FATAL | Core |
| Z04b | ディレクトリエントリ (`d/`) で上位16ビットの種別が `0x8000` (通常ファイル) | 全体 FATAL | Core |
| Z05 | ファイルエントリで種別が 0 (種別なし) または `0x8000`、ディレクトリエントリで種別が 0 または `0x4000`、DOS 属性 read-only/hidden/system/archive のみ | 受理し、それぞれ通常ファイル・ディレクトリとして分類 | Core |
| Z06 | UTF-8 フラグ付きの正しい UTF-8、フラグなしの CP437 (`é` `░` を含む)、フラグなしの UTF-8 バイト列 | それぞれ UTF-8、CP437 として復号した名前で照合。フラグなし UTF-8 は CP437 として読んだ別名になり、文字化け名での誤対応をしない | Core+Win |
| Z07 | UTF-8 フラグ付きで不正な UTF-8 | 全体 FATAL (復号名の U+FFFD を検出) | Core |
| Z08 | 正常な Stored/Deflate、ZIP64 (強制 ZIP64 エントリ、65,536 件超の ZIP64 EOCD)、DD、オフセット調整済み SFX、末尾ごみ付き | 読めて同じ分類になる。形式だけでは拒否しない | Core+Win |
| Z09 | 先頭にデータを付けただけでオフセットを調整していない ZIP | ZipArchive が開けず入力エラー、削除0件 | Core |

## 4. resource limits

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| R01 | エントリ数 100,000 / 100,001 | 許可 / 全体 FATAL | Core |
| R02 | 名前 1,024 / 1,025 UTF-16 コード単位、深さ 128 / 129 成分 | 許可 / 全体 FATAL | Core |
| R03 | 既定値: メタデータ総量がちょうど 134,217,728 バイト / +2 バイトになる入力 (名前長と件数で調整)。既定値では各項 (名前の UTF-16 バイト数、1エントリ当たり 128) が偶数で総量が必ず偶数になり、+1 バイトの入力は作れない。+1 は上限を奇数にした注入 (上限 = 総量 − 1) で確認する | 許可 / 全体 FATAL。計算式 (名前 UTF-16 バイト数 + 128 × 件数) を検証 | Core |
| R04 | 長い名前で 100,000 件より前に 128 MiB を超える ZIP | 全体 FATAL (件数上限とは独立に発動) | Core |
| R05 | 1エントリの宣言 `Length` が 16 GiB / 16 GiB + 1 (ヘッダー値の書き換えで作成。実データは小さい) | 許可 / 全体 FATAL | Core |
| R06 | 宣言 `Length` 合計がちょうど 64 GiB / 64 GiB + 1、**全エントリが target に存在しない (MISSING)** | 許可 / 全体 FATAL。target に触れる前に判定され、target の状態や dry-run の有無で結果が変わらない | Core+Win |
| R07 | 内容比較候補で実データが宣言 `Length` を超える (C06) | `Length` を超えた時点で読み取りを中断し FATAL。それ以上バッファを確保しない | Core |
| R08 | 実測合計上限: 実測カウンタを小さな値に差し替えるテスト用フックで、累計が上限ちょうど / +1 | 許可 / 全体 FATAL | Core |

## 5. target 分類と byte 比較

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| T01 | 同一内容、1 byte 変更、サイズ違い、0 byte | 順に MATCHED、MODIFIED、MODIFIED (ZIP 内容を読まない)、MATCHED。mtime の違いは内容一致を覆さない | Core+Win |
| T02 | 親成分の分類表 (SPEC §6.1) のうち MISSING の行: ZIP の `a/b/c.txt` に対し、target の `a` または `a/b` が (1) 存在しない、(2) 大小文字だけ違うディレクトリ、(3) 通常ファイル | いずれも `c.txt` は MISSING。ZIP 内容は開かない | Core+Win |
| T03 | 親成分の分類表の reparse の行: `a/b` が (1) ディレクトリ junction、(2) ディレクトリ symlink、(3) ファイル symlink | いずれも SKIPPED_SPECIAL_FILE。リンク先を読まず変更しない | Core+Win |
| T04 | 親成分の分類表の判定不能の行: 親成分の属性取得 API の失敗、想定外の種類を偽ファイルシステムで注入 | 全体 FATAL、削除0件 | Core |
| T05 | ZIP にない target ファイルが、ZIP エントリと同じディレクトリに多数ある | 実名確認の列挙で走査はされるが、表示・集計・分類・削除されず、照合結果にも保持されない (テスト用フックで保持される名前を確認) | Core+Win |
| T06 | 実名確認 (SPEC §6.2) の規則: target 側の大小文字違い (ファイル名、途中のディレクトリ名)、8.3 名でだけ一致 (ファイル名、途中のディレクトリ名)、ディレクトリ単位で大文字小文字を区別する NTFS ディレクトリ内の大小文字違い | いずれも MISSING、削除しない | Win |
| T07 | ADS (`Zone.Identifier` を含む)、hardlink (リンク数2)、ファイル symlink、read-only、system、temporary、offline、その他許可集合外の属性 | `SKIPPED_SPECIAL_FILE`、内容を読まない | Core+Win |
| T08 | archive、hidden、not-content-indexed、NTFS 圧縮、sparse、EFS 暗号化の各属性だけを持つ同一内容ファイル | MATCHED (これらだけを理由にスキップしない) | Core+Win |
| T09 | ZIP に file があり target は directory、ZIP 自身に対応する対象 | `SKIPPED_SPECIAL_FILE`。ZIP 自身を変更しない | Core+Win |
| T10 | ADS 列挙・属性・リンク数・File ID・親 File ID・最終パスの取得失敗、存在確認後のオープン失敗、比較中の読取失敗 | SKIP ではなく全体 FATAL、削除0件 | Core+Win |
| T13 | 実名確認の失敗: 列挙用ハンドルのオープン失敗、列挙の途中のエラー (`ERROR_NO_MORE_FILES` 以外)、列挙用ハンドルの File ID・最終パスが列挙で見つけた項目と不一致、比較用ハンドルの File ID が列挙で見つけた項目と不一致 (解析中の差し替え) | 全体 FATAL、削除0件 | Core |
| T14 | 実名確認の列挙で、(1) 同じ名前の項目が2回返る (File ID が異なる2件、偽ファイルシステムで注入)、(2) 照合する名前が返らない (列挙中の改名による見落としを注入) | (1) 最初の1件だけを採用する。比較用オープンの File ID が採用した項目と異なれば全体 FATAL。(2) その名前は `MISSING`、ZIP 内容を開かず削除しない | Core |
| T15 | ディレクトリ単位で大文字小文字を区別する NTFS ディレクトリに `Foo` と `foo` (内容が異なる) が共存し、ZIP が `Foo` / `foo` / `FOO` を指す | `Foo` と `foo` はそれぞれ正しい個体 (列挙の File ID = 比較用オープンの File ID) に対応して比較される。`FOO` は `MISSING` | Win |
| T16 | ZIP のファイルエントリに対し target がディレクトリで、そのディレクトリの `FileStreamInfo` が `ERROR_HANDLE_EOF` で失敗する (T09 の補強) | FATAL にならず `SKIPPED_SPECIAL_FILE`。`Directory` を最初に判定し、ストリーム一覧などを取得しない (SPEC §7 の判定順序) | Core+Win |
| T11 | 比較対象を別プロセスが書き込みで開いたままにする (エディタで編集中を模擬) | 比較用ハンドルのオープンが共有違反になり全体 FATAL、削除0件。原因のパスを表示 | Win |
| T12 | 比較用ハンドルの扱い | 各エントリの判定終了時に閉じられ、結果表示・確認待ち中に target ファイルのハンドルが開いていない (テスト用フックで開いているハンドル数を確認) | Core+Win |

## 6. 削除・削除直前再検証・CLI

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| D01 | dry-run、通常実行の `n`/空入力/EOF、非対話で `--yes` なし | 削除0件。全件解析結果は表示 | Core+Win |
| D02 | `y`/`Y`、`--yes` | 検証済み MATCHED だけを個別削除。ディレクトリ、ZIP、ZIP にない target ファイルは残る | Core+Win |
| D03 | 削除処理の呼び出し順 | 各対象について「期待パスでの削除用オープン1回 → 同じハンドルでの同一性再検証 → 同じハンドルからの読み取りと ZIP エントリの再展開による全バイト比較 → 同じハンドルでの最終確認 → 同じハンドルへの削除指示 → 同じハンドルでの成立確認 → クローズ」の順。オープン後にパスを使う呼び出し (パス指定の削除・属性取得・オープン) がないことを偽ファイルシステムの呼び出し記録で確認 | Core |
| D04 | 確認待ち中に別プロセスが対象を変更する: 内容とサイズの変更 / 内容のみ変更 (mtime 自動更新) / 削除して同名で作り直す (File ID 変化) / 別ファイルで置換 (`MoveFileEx` で上書き) | 再検証で検出し、その対象を削除せず以後の削除を停止。停止理由を表示 | Win |
| D05 | 確認待ち中に ADS を追加 / hardlink を追加 / read-only・system 属性を付与 / ファイルを symlink に置換 / 途中のディレクトリを junction または同名の別ディレクトリに置換 | 同上 (停止) | Win |
| D06 | 再検証の情報取得 API が失敗するよう偽ファイルシステムで注入 | 削除せず停止 | Core |
| D07 | 確認待ち中に、対象以外の変更だけを行う (ZIP にない target ファイルの追加・変更、他の MATCHED の読み取り、target 内の別ディレクトリの作成) | 対象の再検証・再比較は一致し、停止せず削除される (誤って停止しない) | Win |
| D08 | 削除指示の失敗、成立確認の失敗 (偽ファイルシステムで注入) | 安全性破綻として停止。成立確認の失敗では「削除された可能性あり」と報告し、削除済み件数を正確に表示 | Core |
| D09 | 再検証直後フックで、別プロセスが対象を書き込み用・改名用に開こうとする | 共有違反で失敗し、対象は unextract が検証した個体のまま削除される | Win |
| D10 | 確認待ち中に、File ID・サイズ・`LastWriteTime`・`ChangeTime`・属性を保ったまま内容だけを書き換える (書き込み後にタイムスタンプを書き戻す) | 同一性の再検証は通るが、2回目の全バイト比較で検出され、削除されず以後が停止する | Win |
| D11 | 確認待ち中に別プロセスが対象を書き込みで開いたままにする / `FILE_SHARE_DELETE` なしで読み取り中にする / ACL で対象の READ_DATA を拒否する / ACL で対象の DELETE と親の DELETE_CHILD を拒否する | 削除用オープンが共有違反 (32)・アクセス拒否 (5) になり (SPEC §13 の PoC 7 #1・#2・#3・#5)、識別確認の時点でスナップショットと一致する通常ファイルに見えるため `DELETE_FAILED` (削除しない)。後続の安全な MATCHED は削除される | Core+Win |
| D13 | 2回目の比較中に ZIP 側の異常を注入する: CRC 不一致、`Length` 超過、終端欠落 (バイト数不足)、読み取り例外 | 削除せず以後を停止する。`DELETE_FAILED` で続行しない | Core |
| D14 | 削除用オープンの段階で同一性に疑義が出る: 対象消失 (2)、親の消失・ファイル化 (3)、対象の reparse 化、親ディレクトリの差し替え、オープンは成功したが File ID 不一致、アクセス拒否 (5) だが識別確認で別の個体・ディレクトリ・ディレクトリ junction と判明、削除保留中 (削除用オープンも識別確認のオープンも 5) | いずれも停止。`DELETE_FAILED` で続行しない | Core+Win |
| D15 | 削除用オープンで対応表に無いエラーコード、識別確認自体の失敗を注入 | 停止 | Core |
| D16 | 最終確認直前フックで ADS を追加 / hardlink を追加 / read-only を付与 | 最終確認で検出して停止 (最終確認より後の追加は SPEC §12 の限界であり、検出を保証するテストにしない) | Win |
| D17 | 削除指示の呼び出し内容 | `SetFileInformationByHandle(FileDispositionInfoEx)` が削除用ハンドルに対して1回だけ呼ばれ、flags がちょうど `DELETE \| POSIX_SEMANTICS` (0x3) で、`IGNORE_READONLY_ATTRIBUTE` (0x10) を含まない (偽ファイルシステムの呼び出し記録) | Core |
| D18 | 削除指示の API が成功を返すが、同じハンドルの `DeletePending` が false のまま (偽ファイルシステムで注入。実機で `DELETE` ビットを含まない flags が成功を返すことに相当) | `DELETED` にせず停止し、「削除された可能性あり」と報告する。API の成功だけで成立としない | Core |
| D19 | 確認待ち中に、親ディレクトリ `B` を同名の別ディレクトリに差し替え、**同じファイルを移して入れる** (File ID と最終パスは不変) | 親 File ID の不一致で検出し、削除せず停止 | Win |
| D20 | 確認待ち中に、途中のディレクトリを元のディレクトリを指す junction に差し替える / 途中のディレクトリを大文字小文字だけ改名する (親 File ID は不変) | 最終パスの不一致で検出し、削除せず停止 | Win |
| D21 | 最終確認の直後・削除指示の直前のフックで、別プロセスが対象に read-only を付与する。対象の前に削除済みの MATCHED と、後に未処理の MATCHED がある | 削除指示が失敗 (5) し、その対象は残り、以後の削除が停止する。それまでに削除したファイルは戻らない。未処理の件数を表示 | Win |
| D22 | ACL で対象の DELETE だけを拒否する (親の DELETE_CHILD は許可のまま)。拒否する時点を (a) 初回分類の前、(b) 確認待ち中、の2通りで行う | 削除用オープンは成功する (SPEC §13 の PoC 7 #4)。E-2 の実測 (`PLAN_VALIDATION.md` の「E-2 実測」): (a) 段階1・2・5がすべて成功し (`DeletePending == true`)、対象は削除される。Windows の通常の ACL の挙動として受け入れる (`PLAN_DECISIONS.md` DEC-12)。(b) ACL の変更で `ChangeTime` が変わるため段階2の再検証で停止し、対象は残り、以後は未処理になる。どちらの場合も ZIP 自身と無関係のファイルは残る。テストは段階5の結果 (削除・停止の別、`DeletePending`、終了状態) をテスト出力に出す。段階5が失敗した場合 (実測では起きていない) は、そのファイルを残して以後の削除を停止することを期待する (SPEC §8.4)。判定は誤削除がないこと (対象以外が残り、停止なら対象も残ること) | Win |
| D23 | 削除用ハンドルを開いている間 (再比較中フック) に、別プロセスが対象を書き込みで開こうとする (旧テスト番号 D12。DEC-12 との重複を避けて改番) | 共有違反で開けない | Win |

競合の再現には、解析完了直後・確認待ち・再オープン直前・再検証直後・再比較中・最終確認直前・削除指示直前に注入できるテスト用フックを使う。他プロセスの模擬は別プロセスのヘルパーで行う。

実削除を伴う Win テストは、テストが自分で作った一意な fixture ディレクトリ (テストの出力先の `fixtures/<一意名>/`) の中のファイルだけを削除する。削除指示の直前のフックで、テスト側のガードが削除用ハンドルの最終パスが fixture の内側であること (`\` 境界付きの比較) と、fixture から対象の親までの各ディレクトリが reparse point でないことを確かめ、違反なら例外で中止する。このガードはテストの安全装置であり、製品の安全装置の代わりにしない。ACL を変えるテストは `finally` で元に戻す。fixture のディレクトリ自体はテストから削除しない。

## 7. dry-run と通常実行

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| Y01 | C01〜C08 の各 target 状態、R06、T02〜T04、T07〜T11 の fixture で、`--dry-run` と通常実行 (`n` で中止) | 確認・削除フェーズに入る前までの初回分類、FATAL 判定、表示が一致する | Core+Win |
| Y02 | 初回分類の後、確認待ち中に target の MATCHED ファイルの内容を変更する。同じ操作を `--dry-run` の結果表示後にも行う | 通常実行は削除直前再検証で停止する。dry-run は削除用ハンドルの再オープン・再検証・2回目の比較・削除を行わず (呼び出し記録で確認)、表示済みの MATCHED のまま正常終了する。この差は仕様どおり | Core+Win |

## 8. 表示

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| O01 | 全カテゴリー (MATCHED、MODIFIED、MISSING、SKIPPED_SPECIAL_FILE、DIRECTORY) を含む正常完走 | 各カテゴリーの全パスと件数を表示。進捗は `Checking n / total` | Core+Win |
| O02 | 解析途中の FATAL (100 件中 40 件目) | 判定済みの 39 件のパス、原因エントリと原因、未判定 60 件は件数のみ、削除0件を明示 | Core+Win |
| O03 | 削除途中の停止と `DELETE_FAILED` | `DELETE_FAILED` のパスと理由、停止原因のパスと理由、削除済み・DELETE_FAILED・未処理の件数 | Core+Win |
| O04 | 表示できない名前 (制御文字など、FATAL の原因として) | エスケープ表記またはエントリ番号で表示 | Core |

## 9. 実機確認と終了条件

- SPEC §13 の PoC 1〜8 は実施済みで、結果は [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) にある。§13 の未確認の事項 (ファイル symlink への差し替え、クラウド placeholder) は、実装テストまたは実機確認で扱いを決めるまで成立と見なさない (SPEC §14)。D22 は扱いが確定済み (`PLAN_DECISIONS.md` DEC-12) で、段階5の結果は E-2 の実機テストで実測し `PLAN_VALIDATION.md` の「E-2 実測」に記録した。
- 実削除テストは作業専用の一時ディレクトリと自作 fixture だけで行い、ZIP 自身、無関係のファイル、ディレクトリが残ることを確認する。
- Core の結果だけで Windows API の同一個体保証を主張しない。実 Win で再現不能なケースは未確認として残す。
- 全必須項目が通り、削除開始前の FATAL で削除0件、`DELETE_FAILED` で継続、削除直前再検証の不一致・未知のエラーで停止、dry-run と通常実行の初回分類の一致が確認できた時点を MVP のテスト完了とする。

## 10. E2E (X 系)

`tests/Unextract.E2E.Tests` は、`unextract.exe` を `Process.Start` で別プロセスとして起動し、終了コード・stdout・stderr・ファイルシステムの結果を検証する。同一プロセス内で CLI を呼ぶ `Unextract.Cli.Tests` では確かめられない、プロセス境界の挙動 (標準入力のリダイレクトによる非対話の判定、stdout と stderr の分離、出力のエンコーディング、終了コード、単一ファイル exe での動作) を担う。

- 起動する exe: 環境変数 `UNEXTRACT_E2E_EXE` があればそれ (publish 済みの単一ファイル exe。CI の E2E ステップ)。なければテストアセンブリの位置から相対で `src/Unextract.Cli/bin/<構成>/<TFM>/unextract.exe` (ビルド順は `ProjectReference` の `ReferenceOutputAssembly=false` で保証する)。どちらでも見つからなければ失敗にする (成功・前提不成立にしない)。
- stdin・stdout・stderr は常にリダイレクトし、stdout・stderr は UTF-8 で読む。60 秒で終わらなければプロセスツリーを kill して失敗にする。
- 作業ディレクトリはテストの出力先の `fixtures/<テスト名>-<GUID>/` (テストからは削除しない)。junction・ACL・symlink は使わない。ZIP は段階 B の生成器 (`ZipFixture`、`ZipPatcher`) をリンクで共有して作る。
- 実削除を伴う実行 (`--yes`、および誤って削除フェーズに入った場合に備えて X03 と X12 の中止の実行) の前に、領域外ガード (`Unextract.Windows.Tests` の `DeletionGuard` をリンクで共有) で、target と target 内の全ディレクトリの最終パス (確認用ハンドルから取得) が fixture の内側であり、fixture からその項目までの各成分が reparse point でないことを確かめる。違反なら例外で中止し、exe を起動しない。

| ID | 入力・操作 | 期待結果 | 種別 |
|---|---|---|---|
| X01 | 全カテゴリー (MATCHED、MODIFIED (内容違い・サイズ違い)、MISSING、SKIPPED_SPECIAL_FILE (ZIP ではファイル、target ではディレクトリ)、DIRECTORY) と ZIP にない target ファイルを含む fixture で `--dry-run` | 終了コード 0。target 全体 (パス・サイズ・SHA-256・更新日時) と ZIP の SHA-256・更新日時が実行前後で不変。stdout に各カテゴリーの全パスと件数、合計行。ZIP にないファイルは出力されない。stderr は空 | E2E |
| X02 | X01 と同じ fixture で `--yes` | 終了コード 0。MATCHED だけが削除され、MODIFIED・SKIPPED・ZIP にないファイル・ディレクトリ・ZIP (SHA-256 不変) は残る。stdout の最後が「削除済み 2、DELETE_FAILED 0、未処理 0」 | E2E |
| X03 | X01 と同じ fixture で `--yes` なし。stdin は (1) 何も書かずに閉じる、(2) `n\n`、(3) `y\n` | いずれも非対話として中止。終了コード 2、削除0件。stdout に「標準入力が対話的でなく --yes も無いため、確認できません。」と「中止しました。削除0件。」、`[y/N]` は表示されない (E-2 の実測と同じ) | E2E |
| X04 | 先頭に MATCHED 2件、3番目に Central Directory の CRC-32 だけを書き換えたエントリ (target にサイズ一致のファイルあり)、後方に2件。`--yes` | 終了コード 1、削除0件。stderr に FATAL と原因エントリ (`#3 "bad.txt"`)、削除0件の明示。stdout に判定済みの件数とパス、未判定の件数 (SPEC §10 により判定済み・未判定の件数は stdout)。未判定のパスは出力されない | E2E |
| X05 | 引数の不正: `--target=dir`、ZIP なし、不明なオプション、オプションの重複、ZIP が2つ、`--target` の値が空 | 終了コード 1。stderr に入力エラーと使い方。stdout は空。fixture 全体が不変 | E2E |
| X06 | target が存在しない、target がファイル、ZIP が存在しない (拒否対象ではない通常の入力の誤り)。`--yes` 付き | 終了コード 1。stderr に入力エラー (ZIP が開けない場合は FATAL) と削除0件の明示。stdout は空。存在しない target は作成されない | E2E |
| X07 | 削除候補0件 (全て MODIFIED・MISSING)、空 ZIP。`--yes` なし、stdin は空 | プロンプトなしで終了コード 0 (「削除候補はありません。」)。target は不変 | E2E |
| X08 | UTF-8 フラグ付きの日本語名の ZIP で `--dry-run`、続けて `--yes` | stdout (UTF-8 で読む) に日本語名がそのまま出る (U+FFFD なし)。終了コード 0。`--dry-run` と `--yes` の解析結果の一覧が一致し、`--yes` で MATCHED の日本語名ファイルだけが削除される | E2E |
| X09 | UTF-8 フラグなしで CP437 の名前バイト (`café░.txt`) を持つ ZIP で `--dry-run`、続けて `-y` | CP437 として照合されて MATCHED。終了コード 0。削除される。`UNEXTRACT_E2E_EXE` の単一ファイル exe でも同じ | E2E |
| X10 | `--dry-run`、`--yes`、FATAL、入力エラーの各実行 | 解析結果の一覧と削除の結果は stdout、FATAL・入力エラーは stderr。stderr がリダイレクトされているため進捗 (`Checking`、`Deleting`、改行を伴わない CR) は出ない | E2E |
| X11 | 同じ fixture で `--dry-run`、続けて `--yes` | stdout の解析部分 (先頭から合計行まで) が一致する (SPEC §2)。その後は `--dry-run` が「--dry-run のため削除しません。」、`--yes` が削除の結果 | E2E |
| X12 | 同じ fixture で `--dry-run` / 不明なオプション / `--yes` なし (非対話) | 終了コード 0 / 1 / 2。target は不変。各コードは X01〜X11 でも確認している (0: X01・X02・X07〜X09・X11、1: X04〜X06、2: X03) | E2E |

**自動化しない項目** (手動手順書 [`MANUAL_TESTS.md`](MANUAL_TESTS.md) の M 系で扱う): E2E は stdin・stdout・stderr をリダイレクトして起動するため、exe からは常に非対話に見え、進捗も表示されない。そのため、対話的なコンソールでの `[y/N]` の入力 (`n`・空 Enter・`y`。M01〜M03)、確認待ちでの Ctrl+C (M04)、標準エラー出力が端末のときの進捗の1行上書き (M05)、コンソールのコードページ (932 / 65001) とフォントによる表示 (M06)、実際の確認待ち中に別のウィンドウから行う変更による停止 (M07。検出ロジックは D04 で自動化済み) は自動化しない。疑似コンソール (ConPTY) を使えば一部は自動化できるが、テスト側に端末エミュレーションを持ち込むことになり、検証対象 (実際の端末での見え方) とも一致しないため MVP では採らない。
