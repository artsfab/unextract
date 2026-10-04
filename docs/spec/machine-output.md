# 機械可読出力

役割: `--jsonl` のレコード、出力先 (標準出力と実行ログ)、完了境界、コード、版の正本。機械出力やその利用側 (GUI など) を変えるときに読む。人間向けの文言・表示・通常の引数は [CLI](cli.md) が定める。環境ごとの観測・未確認範囲は [OPEN_ISSUES](../OPEN_ISSUES.md#observations) による。

[位置づけ](#scope) / [起動](#invocation) / [出力先](#destinations) / [実行ログ](#log) / [レコード](#records) / [result](#result) / [完了境界](#boundary) / [コード](#codes) / [版](#versioning)

<a id="scope"></a>
## 位置づけ

- `--jsonl` を指定した実行 (以下、機械モード) だけに適用する。指定しない実行の引数・出力・確認・進捗・終了コードは [CLI](cli.md) のとおりで、本書の影響を受けない。
- 機械モードでも、判定、[モード契約](../SPEC.md#modes)、FATAL・STOP・`DELETE_FAILED` の境界、[削除0件の範囲](../SPEC.md#zero-deletions)、終了コードの意味、削除の方式・ハンドル構成・検査順序は変わらない。変わるのは出力の形式と出力先だけである。
- 機械出力は削除の許可証ではない。`analyze` の機械出力を `delete` に渡せるのは、[entries](cli.md#entries) による処理範囲の指定としてだけである。
- 既存の概念 (エントリ、状態名、SkipReason、合計・要約の件数、FATAL・STOP の原因) だけを公開する。進捗、候補サイズの合計、エントリごとの Target のパス、呼び出し側のグルーピングは出さない。

<a id="invocation"></a>
## 起動

```text
unextract analyze <archive.zip> --target <dir> [--fast] --jsonl
unextract delete  <archive.zip> --target <dir> [--fast] [--entries <file>] --yes|-y --jsonl [--log <file>]
```

- `--jsonl` は値を取らないフラグで、両操作で受け付ける。位置は自由で、重複は入力エラー ([引数](cli.md#arguments) の規則と同じ)。
- 機械モードかどうかは、引数の解析より前に、どれかの引数が `--jsonl` と完全一致するかで決める。引数エラーも機械出力で報告するためである。
- 機械モードの `delete` で `--yes`/`-y` がなければ入力エラー (`USAGE`、削除0件) とする。確認プロンプトを表示せず、標準入力を読まない。`--jsonl` から確認の省略を推論しない。機械モードでは利用者による中止 (終了コード 2) は起こらない。
- `--log <file>` は機械モードの `delete` だけで受け付ける ([実行ログ](#log))。`analyze` や `--jsonl` なしの実行での指定は入力エラー。値の渡し方と拒否条件 (別引数、値が無い・空・`-` で始まる、重複) は `--entries` と同じ。
- `--entries` の形式・照合・上限は変えない。

<a id="destinations"></a>
## 出力先と符号化

- 標準出力にはレコードだけを書く。1レコードを1行の JSON オブジェクトとし、LF で終える。改行で終わっていない最後の行はレコードとして扱わない。
- 出力全体を ASCII だけにする。JSON 文字列中の非 ASCII 文字と制御文字は `\uXXXX` でエスケープする (補助平面はサロゲート対)。ほかにどの文字をエスケープするかは規定しない (JSON として等価であればよい)。
- `name` を JSON として復号した文字列は FullName そのものであり、そのまま `--entries` の1行に使える。人間向けの[表示用エスケープ](cli.md#result-lines)と転記不可の印は適用しない。
- 標準エラー出力には何も書かず、[進捗](cli.md#streams)も出さない。人間向けの文言は `message` に表示用として入れる。唯一の例外は、レコードを書けなかった場合の最終手段としての標準エラー出力で、これは契約外とする。
- [Fast警告](cli.md#warning)の文は出さない。代わりに `run` の `mode` を必ず出す。
- 出力先 (標準出力または実行ログ) への書き込み・flushに失敗したことを検出したら、以後のエントリを処理しない。まだ書ける出力先には `internal_error` の `result` (`OUTPUT_FAILED`) を書く。ただし、終端の `result` の配送中・配送後の失敗は [resultと終了コード](#result) の例外規定に従う。標準出力の読み手が消えたことを CLI が検出できるとは保証しない ([未確認](../OPEN_ISSUES.md#observations))。検出できなくても、CLI はその実行で承認された処理範囲を、各エントリの再検証つきで処理し続けるだけである。
- 書き込みまたはflushに一度でも失敗した出力先は、まだ書ける出力先に含めず、以後何も書かない。未完の行へのLF補完、切り詰め、再試行、再利用もしない。全出力先が失敗した場合は最終手段の標準エラー出力だけを使う。

<a id="log"></a>
## 実行ログ (`--log`)

`delete` の結果を、呼び出し側やその出力経路に問題が起きた後でも確認できるようにするための記録である。監査、ジョブの再開、セッションの復元には使わない。

- 標準出力に書くレコードと同じものを、同じバイト列でログファイルにも書く。ログ専用の形式・レコード・フィールドはない。
- ログファイルは [Prepare](../SPEC.md#prepare) の手順1で新規作成する。既存のファイル (ZIP や entries ファイルを含む) は上書きせず入力エラー (`LOG_ALREADY_EXISTS`)。作成に失敗した場合も入力エラー (`LOG_CREATE_FAILED`) で、その `result` は標準出力にだけ書く。開けた先がディスク上のファイルでない場合 (`NUL`・`CON` などのデバイスやパイプ) は記録が残らないため、作成の失敗 (`LOG_CREATE_FAILED`) として扱う。
- 作成したログは、他者には読み取りだけを共有して実行終了まで保持する。作成後のレコードは、Prepare の入力エラー・FATAL の `result` を含めて全てログにも書く。
- 各レコードは、まずログに書いて OS に渡し (プロセス内のバッファに残さない)、その後で標準出力に書く。したがって、標準出力で受け取ったレコードはログにもある。例外は、ログ作成失敗の `result` と、実行途中のログの書き込み・flush失敗後に標準出力だけへ書く `OUTPUT_FAILED` の `result` である。ディスクへの書き出しの完了 (電源断への耐性) は保証しない。
- ログへの書き込みに失敗したら、標準出力と同じく以後のエントリを処理しない ([出力先と符号化](#destinations))。記録できないまま削除を続けない。
- CLI はログを削除・切り詰め・再利用しない。保存場所・ファイル名・保持期間は呼び出し側が決める。
- ログが target の内側にあり、ZIP に同じ名前のエントリがあっても、保持中のログとの共有違反で削除用に開けないため削除されない ([失敗の境界](filesystem.md#failure-boundary) の識別確認により `DELETE_FAILED` または STOP)。
- ログの内容は [完了境界](#boundary) の規則で読む。

<a id="records"></a>
## レコード

全てのレコードに `v` (protocol version、整数、初版は `1`) と `type` を付ける。該当しないフィールドは省略し、null は使わない。

| `type` | 書く時点 | フィールド |
|---|---|---|
| `run` | [Prepare](../SPEC.md#prepare) が成功した後、最初のエントリを処理する直前に1回だけ。書き込みが成功してから処理を始める | `operation` (`analyze` / `delete`)、`mode` (`strict` / `fast`)、`archive` (指定されたまま)、`target` (target の最終パス。`\\?\` を除いた、人間向けヘッダーと同じ値)、`entries_total` (ZIP の全エントリ数)、`selected` (処理対象の件数)、`entries_option` (bool、`--entries` の有無) |
| `entry` | 1エントリの処理が完結し、そのハンドルを閉じた後。書き込みが成功してから次のエントリへ進む | `index`、`name`、`directory` (bool)、`length`、`status`、`skip_reason`、`reason`、`possibly_deleted` |
| `result` | 終了時に1回 (`run` がない場合を含む) | `outcome`、`exit_code`、`counts`、`error` |

`entry` のフィールド:

- `index`: 1始まりの Central Directory の順の番号で、全エントリで通し。人間向け表示の `#n` と同じ。
- `name`: FullName ([出力先と符号化](#destinations))。
- `length`: ZIP の宣言展開サイズ。ディレクトリは 0。`MATCHED`・`SAME_SIZE`・`DELETED` では target の `EndOfFile` と一致することが分類の条件なので、対象の論理サイズに等しい。
- `status`: 既存の状態名だけを使う。`analyze` は `MATCHED`、`SAME_SIZE`、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DIRECTORY`。`delete` は `DELETED`、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DELETE_FAILED`、`STOPPED`。
- `skip_reason` (`SKIPPED_SPECIAL_FILE` のとき): [特殊ファイルと属性](filesystem.md#special-files) の理由に対応する `PARENT_REPARSE`、`DIRECTORY`、`REPARSE`、`HARDLINK`、`ADS`、`ARCHIVE_ITSELF`、`ATTRIBUTES`。
- `reason` (`DELETE_FAILED` と `STOPPED` のとき): `step`、`code`、`win32_error` (ある場合)、`message` を持つオブジェクト ([コード](#codes))。
- `possibly_deleted` (`STOPPED` のとき、bool): 削除の指示の後で成立を確認できなかった場合に true ([失敗の境界](filesystem.md#failure-boundary))。

`entry` を出す範囲は人間向けの結果行と同じにする。

- `analyze`: 判定した全エントリを ZIP の順に逐次出す (DIRECTORY を含む)。カテゴリー順の並べ替えは人間向け出力だけで行う。FATAL の原因エントリには `entry` を出さず、`result.error` で示す。
- `delete`: 処理したファイルエントリだけを処理した順に出す。DIRECTORY と処理対象外は件数だけ。STOP した対象は `STOPPED` の `entry` として出す。

<a id="result"></a>
## resultと終了コード

| `outcome` | 条件 | `exit_code` |
|---|---|---|
| `completed` | 処理対象を最後まで処理した | `analyze` は 0。`delete` は `DELETE_FAILED` が0件なら 0、1件以上なら 1 |
| `input_error` | 人間向けの出力が「入力エラー」になるもの (引数、entries、拒否位置、target、ログの作成、機械モードで `--yes` がない場合) | 1 |
| `fatal` | 人間向けの出力が「FATAL」になるもの (ZIP を開けない、ZIP の事前検証、ZIP 自身の個体、`analyze` のエントリ処理中) | 1 |
| `stopped` | `delete` の逐次処理中の STOP | 1 |
| `internal_error` | 想定外の例外、出力先への書き込みの失敗 | 1 |

- 終了状態の判定の第一の根拠は終了コード ([引数と終了状態](cli.md#arguments)) で、`result` はその詳細である。`result` がないのに終了コード 0 で終わることは仕様上起こらず、起きた場合は異常として扱う。
- 終端の `result` の配送中に出力失敗を検出した場合、既に書いた `result` は書き換えず、2個目も書かず、実プロセスの終了コードを1にする。配送後のログのclose失敗も同じ扱いとする。この場合、ログなどに書けた `result` の `outcome`・`exit_code` と実プロセスの終了コードの食い違いを許容し、終了コードを優先する。
- エントリ内で捕捉した想定外の例外は既存どおり `STOPPED` / `stopped`、`stage=entry`、`code=UNEXPECTED_EXCEPTION` とする (削除指示後は `possibly_deleted=true`)。処理器外へ伝わる例外は `internal_error` とし、`analyze` では `deletion_started` を省略する。
- `counts` には既存の合計・要約の項目を入れる。途中で終わった場合は確定している範囲だけを入れる。
  - `analyze`: `matched` (Strict)、`same_size` (Fast)、`modified`、`missing`、`skipped_special_file`、`directory`、`undetermined` (未判定)。
  - `delete`: `deleted`、`modified`、`missing`、`skipped_special_file`、`directory`、`delete_failed`、`not_selected` (処理対象外)、`unprocessed` (未処理)。
- `counts` のキーは、その段階の人間向け表示が出す件数項目と同じ集合とし、usage・ログ作成失敗・`delete` のPrepare失敗では省略する。`analyze` のPrepare FATAL (targetルート確認後) は判明済みの判定済み・未判定の範囲とし、未取得の件数を0にしない。`delete` の `internal_error` は処理済みの事実を数え、出力に失敗したエントリも処理済み、それより後の選択対象は `unprocessed` とする。`STOPPED` の配送失敗では、その対象の `possibly_deleted` を `result.error` に付ける。
- `error` (`completed` 以外) のフィールド:
  - `stage`: 閉じた集合。`usage` (引数)、`prepare` (Prepare の入力エラー・FATAL)、`entry` (エントリの処理中の FATAL・STOP)、`internal` (想定外の例外、出力の失敗)。
  - `step`: 開いた集合。`entry` の段階で分かる場合だけ付け、[失敗の境界](filesystem.md#failure-boundary) の段階を `resolve`、`open`、`verify`、`inspect`、`compare`、`final_check`、`dispose`、`confirm` で示す。
  - `code`: 開いた集合 ([コード](#codes))。
  - 付加情報 (該当する場合だけ): `entry_index`、`entry_name`、`line` (entries の行番号)、`win32_error`、`possibly_deleted`、`deletion_started` (`internal_error` で、逐次削除を開始していたか)、`message` (表示用)。

<a id="boundary"></a>
## 完了境界と異常時の解釈

標準出力で受け取ったレコードにも、[実行ログ](#log)の内容にも同じ規則を使う。

- `run` がなければ、削除は0件である。
- `entry` があるエントリは、その結果で確定している。
- `run` があり `result` がない場合 (クラッシュ、強制終了、出力の破損や途絶)、`delete` では、処理対象のファイルエントリのうち ZIP の順で最後の `entry` の次の1件 (`entry` が無ければ最初の1件) を「不明 (削除された可能性あり)」とし、それ以降を未処理 (触れていない) とする。`internal_error` で `deletion_started` が true の場合も同じ規則による。
- CLI は異常終了の後に何も書けない。保証するのは、書いたレコードの意味と、書けなかったことを検出したら先へ進まないこと ([出力先と符号化](#destinations)) だけである。

<a id="codes"></a>
## コード

名前は UPPER_SNAKE とし、C# の列挙名とは独立した仕様上の名前である。各コードの条件は担当正本が定め、本表は対応だけを示す。

| 区分 | コード | 条件の正本 |
|---|---|---|
| 引数 | `USAGE` (細分化しない。機械モードで `--yes` がない場合を含む) | [引数](cli.md#arguments)、[起動](#invocation) |
| 実行ログ | `LOG_ALREADY_EXISTS`、`LOG_CREATE_FAILED` | [実行ログ](#log) |
| entries | `ENTRIES_UNREADABLE`、`ENTRIES_TOO_LARGE`、`ENTRIES_UTF16`、`ENTRIES_UTF32`、`ENTRIES_TOO_MANY_LINES`、`ENTRIES_CR_IN_LINE`、`ENTRIES_EMPTY_LINE`、`ENTRIES_LINE_TOO_LONG`、`ENTRIES_INVALID_UTF8`、`ENTRIES_DUPLICATE_LINE`、`ENTRIES_NO_LINES`、`ENTRIES_NO_MATCH`、`ENTRIES_DIRECTORY` | [entries](cli.md#entries) |
| ZIP を開けない | `ARCHIVE_OPEN_FAILED`、`ARCHIVE_UNREADABLE` | [ZIP](zip.md)、[Prepare の失敗](../SPEC.md#prepare-failures) |
| 上限 | `TOO_MANY_ENTRIES`、`NAME_TOO_LONG`、`METADATA_TOO_LARGE`、`PATH_TOO_DEEP`、`ENTRY_TOO_LARGE`、`TOTAL_DECLARED_LENGTH_TOO_LARGE`、`INVALID_DECLARED_LENGTH`、`TOTAL_READ_LENGTH_TOO_LARGE` | [ZIP上限](zip.md#limits) |
| 名前とパス | `NAME_CONTAINS_REPLACEMENT_CHARACTER`、`ROOTED_PATH`、`DRIVE_SPECIFIER`、`COLON`、`CONTROL_CHARACTER`、`INVALID_CHARACTER`、`EMPTY_COMPONENT`、`DOT_COMPONENT`、`DOT_DOT_COMPONENT`、`TRAILING_DOT_OR_SPACE`、`RESERVED_NAME` | [ZIP名と構造](zip.md#names) |
| ZIP の構造・種別 | `DUPLICATE_ENTRY`、`CASE_INSENSITIVE_COLLISION`、`FILE_DIRECTORY_CONFLICT`、`FILE_USED_AS_PARENT`、`FILE_ENTRY_WITH_DIRECTORY_TYPE`、`DIRECTORY_ENTRY_WITH_FILE_TYPE`、`UNSUPPORTED_ENTRY_TYPE`、`DOS_DIRECTORY_ATTRIBUTE_ON_FILE_ENTRY`、`DOS_REPARSE_POINT_ATTRIBUTE`、`DIRECTORY_ENTRY_WITH_DATA` | [ZIP名と構造](zip.md#names) |
| target | `TARGET_NOT_FOUND`、`TARGET_CHECK_FAILED`、`TARGET_IS_REPARSE_POINT`、`TARGET_CHANGED_DURING_CHECK`、`TARGET_NOT_DIRECTORY`、`TARGET_NOT_NTFS`、`TARGET_IS_UNC_PATH`、`TARGET_IS_DRIVE_ROOT`、`TARGET_UNSUPPORTED_PATH_FORM`、`TARGET_IS_PROTECTED_LOCATION`、`PROTECTED_LOCATION_UNRESOLVED` | [targetルート](filesystem.md#target-root) |
| ZIP 自身と target 側の判定不能 | `ARCHIVE_IDENTITY_FAILED`、`ENUMERATION_OPEN_FAILED`、`ENUMERATION_HANDLE_MISMATCH`、`ENUMERATION_FAILED`、`UNEXPECTED_TARGET_TYPE`、`COMPARISON_OPEN_FAILED`、`COMPARISON_FILE_ID_MISMATCH`、`FINAL_PATH_MISMATCH`、`PARENT_FILE_ID_MISMATCH`、`TARGET_DELETE_PENDING`、`TARGET_INFO_FAILED`、`TARGET_READ_FAILED` | [解決順序](filesystem.md#resolution)、[特殊ファイルと属性](filesystem.md#special-files)、[analyzeのFATAL](../SPEC.md#analyze-failures) |
| 内容検証 | `CONTENT_ENCRYPTED`、`CONTENT_READ_FAILED`、`CONTENT_TOO_LONG`、`CONTENT_TOO_SHORT`、`CONTENT_CRC_MISMATCH` | [内容検証基準](zip.md#verification) |
| delete 固有 | `DELETE_OPEN_REFUSED` (`DELETE_FAILED` の理由。`win32_error` は 32 または 5)、`OPEN_FAILED`、`IDENTITY_CHECK_FAILED`、`IDENTITY_CHECK_MISMATCH`、`FINAL_CHECK_MISMATCH`、`DISPOSITION_FAILED`、`DELETION_UNCONFIRMED` | [失敗の境界](filesystem.md#failure-boundary)、[削除用openのエラー](filesystem.md#open-errors) |
| 内部 | `UNEXPECTED_EXCEPTION`、`OUTPUT_FAILED` | [出力先と符号化](#destinations)、[出力先と進捗](cli.md#streams) |

- `delete` の STOP には、上表の target 側・内容検証のコードも使う (`analyze` の FATAL と同じ原因)。
- `IDENTITY_CHECK_FAILED` の `win32_error` は識別確認自体の値とし、元のopenの32/5は `message` に残す。最終確認の情報取得失敗は `step=final_check`・`code=TARGET_INFO_FAILED` と元の `win32_error`、値の不一致は `FINAL_CHECK_MISMATCH` とし `win32_error` を付けない。
- コードは追加してよい。利用側は未知のコードを一般的なエラーとして扱う。
- 利用側が状態の分岐に使ってよいのは、`type`、`status`、`outcome`、`exit_code`、`possibly_deleted`、`deletion_started` だけである。`stage`、`step`、`code`、`skip_reason` は表示と診断に使い、`message` は表示だけに使う。

<a id="versioning"></a>
## 互換性と版

- 版 `1` の中では、フィールドとコードの追加を互換とする。利用側は未知のフィールドを無視する。
- `type`、`status`、`outcome`、`stage` の値の集合を変えるとき、既存フィールドの意味を変えるときは `v` を上げる。
- 利用側は、未知の `v` のレコードを受け取ったら、その実行を非互換として失敗扱いにする。
- 版を指定するフラグは設けない。CLI と利用側 (GUI) は同梱して配布する。
