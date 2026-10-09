# CLI仕様

役割: 引数、entries、確認、表示文言・出力先・終了状態の正本。入力や利用者表示を変更するときに該当節を読む。

[引数](#arguments) / [entries](#entries) / [確認](#confirmation) / [出力](#output) / [警告](#warning) / [入力エラー](#input-errors)

<a id="arguments"></a>
## 引数と終了状態

```text
unextract analyze <archive> --target <dir> [--fast] [--jsonl]
unextract delete  <archive> --target <dir> [--fast] [--entries <file>] [--yes|-y] [--jsonl] [--log <file>]
```

- `<archive>` は ZIP または RAR のアーカイブ1つ ([対象](../SPEC.md#scope))。拡張子が `.rar` (大小文字を区別しない) なら RAR、それ以外は ZIP として扱う ([RARの形式の判定](rar.md#format))。
- 第1引数はサブコマンドで、`analyze` または `delete` (小文字の完全一致) だけを受け付ける。サブコマンドが無い (旧形式 `unextract <archive> --target <dir>` を含む)、または不明なサブコマンドは入力エラーとし、削除処理を開始せず、両サブコマンドの使い方を表示する。
- `--dry-run` は廃止した。どの位置にあっても入力エラーとし、削除処理を開始せず、結果の確認には `analyze` を使うよう案内する。互換動作 (別名) は設けない。
- `analyze` が受け付けるオプションは `--target`、`--fast`、`--jsonl` だけ、`delete` は `--target`、`--fast`、`--entries`、`--yes`/`-y`、`--jsonl`、`--log` だけである。それ以外 (`analyze` に `--entries`・`--yes`・`-y`・`--log` を指定した場合を含む) は入力エラーとする。
- `--jsonl` は機械可読出力を選ぶ ([機械可読出力](machine-output.md))。機械モードの `delete` では `--yes`/`-y` が必須で、`--log <file>` (実行ログ) は機械モードの `delete` だけで受け付ける ([起動](machine-output.md#invocation))。`--jsonl` なしの実行には本書の規定がそのまま適用される。
- `--target`、`--entries`、`--log` はオプション名と値を別々の引数で渡す形だけとし、`--target=dir` の形、値が無い・空、値が `-` で始まる、同じオプションの重複 (`--yes` と `-y` の併用を含む)、アーカイブの指定が無い・複数は、いずれも入力エラーとする。
- `--target` は必須。既存ディレクトリ・NTFS・拒否位置・reparse・最終パスの検証は[FSのtargetルート](filesystem.md#target-root)による。
- `--fast` を指定すると、その実行全体が Fast モードになる ([モード契約](../SPEC.md#modes))。指定しなければ Strict である。
- `--entries <file>` は `delete` の処理対象を、ファイルに列挙した ZIP エントリに限定する ([entries](#entries))。安全性の判断を変えず、処理範囲を狭めるだけである。指定しなければ、全エントリが処理対象になる。
- `delete` は、[確認](#confirmation) の時点で `[y/N]` を表示し、`y`/`Y` の明示入力だけで逐次処理を開始する。空入力、EOF、その他の入力は中止。標準入力が非対話で `--yes` がない場合も中止。`--yes` は確認のみを省略し、検証は省略しない。
- 削除は通常の完全削除であり、ごみ箱は使わない。削除するのは検証済みの個別ファイルだけ。再帰削除はしない。
- **`analyze` の結果は削除の許可証ではない。** `delete` は `analyze` の結果を参照せず、必ず実行時点の状態を検証する。そのため、`analyze` と `delete` の分類が一致することは保証しない (間の変更、削除権限の有無 ([ハンドル構成](filesystem.md#handles))、共有違反の扱い ([失敗の境界](filesystem.md#failure-boundary))、[特殊ファイルと属性](filesystem.md#special-files) の事前判定による差があり得る)。
- 終了状態は「成功」「利用者による中止」「エラー」の簡易区分とし、細分化した終了コード体系は設けない。プロセスの終了コードは成功 0、エラー 1、利用者による中止 2 とする。
  - `analyze`: 全エントリの分類を完走すれば成功。入力エラー、FATAL、内部エラーはエラー。中止は無い。
  - `delete` の成功: 処理対象の逐次処理を最後まで行い、STOP がなく `DELETE_FAILED` が0件の場合。削除0件 (全て `MODIFIED`・`MISSING` など) も成功とする。
  - `delete` の利用者による中止: 確認で `y`/`Y` 以外が入力された、または非対話で `--yes` がない場合 (削除0件)。
  - `delete` のエラー: 入力エラー、Prepare の FATAL、内部エラー、STOP ([失敗の境界](filesystem.md#failure-boundary)) に加え、**STOP はなかったが `DELETE_FAILED` が1件以上ある場合**。いずれの場合も終了時の要約に件数を表示する ([表示](#output))。

<a id="entries"></a>
## entriesの形式と照合

entries ファイルは、`delete` が処理を試みてよい ZIP エントリの集合を指定するだけのファイルである。File ID、日時、ハッシュ、分類結果などは持たず、`analyze` の時点で安全だったことを証明するものではない。

- 1行に1つ、ZIP エントリの FullName (`ZipArchive` が [名前の復号](zip.md#decoding) の規則で復号した名前そのもの。RAR では [RARの名前](rar.md#names) の FullName で、区切りは `\`) を書く。Windows のパスとして解釈しない。
- 文字コードは UTF-8。不正なバイト列は入力エラー。先頭の UTF-8 BOM (EF BB BF) は1個だけ許して除く。UTF-16 / UTF-32 の BOM で始まるファイルは入力エラーとし、UTF-16 と判定できる場合は UTF-8 が必要であることが分かるメッセージにする。
- 改行は LF と CRLF を受け付け、混在してもよい。各行末の CR を1個だけ除く。それ以外の位置の CR は入力エラー (CR はエントリ名に現れない)。最終行の末尾の改行は任意で、最後の改行の後の空文字列は行として数えない。
- 空行は入力エラー。行が1つも無いファイル (空ファイル、BOM だけのファイル) も入力エラー。
- 行を trim しない。コメント構文、glob、ワイルドカード、ディレクトリ接頭辞による指定は無い。
- 各行を、[Prepare](../SPEC.md#prepare) の手順6を通過したエントリの FullName と `StringComparison.Ordinal` で照合する。大小文字と区切り (`/`、`\`) を補正しない。一致するエントリが無い行は入力エラー。大小文字だけ、または区切りだけが異なるエントリがあれば、その FullName をヒントとして表示してよい。
- 同じ内容の行が複数あれば入力エラー。ディレクトリエントリ (名前が区切りで終わるもの) を指定した行は入力エラー。
- 上限は以下のとおり。上限以下を許可し、超過は入力エラー。ファイルサイズ超過時は全体を読まない。

  | 対象 | 上限・単位 | 検査時点 |
  |---|---|---|
  | ファイル全体 | 128 MiB (134,217,728バイト) | Prepareの読み込み時 |
  | 1行 | 4,096バイト (UTF-8、改行除外) | Prepareの形式検査 |
  | 行数 | 100,000 | Prepareの形式検査 |

  全体を検証してから削除するため読み込み量を制限する。正当な行はZIPエントリと1対1で重複せず、エントリ総数の上限を超えない。
- 最初のエラーで中止し、行番号と、安全に表示できる形の行内容と理由を表示する。
- entries の読み込み・形式の検査・照合は全て、確認 ([確認](#confirmation)) と最初の target エントリの処理より前に完了する。entries 入力のどの不正でも削除は0件である。

`--entries` で選ばれなかったエントリは、target 側で解決も分類もしない ([実名確認](filesystem.md#real-names) の列挙で探す名前にも含めない)。

<a id="confirmation"></a>
## 確認のタイミング

Prepare が全て成功した後、最初の target エントリの処理を始める前に、1回だけ確認する (`--yes` では省略)。確認待ちの間、個々の target ファイルのハンドルは開いていない。この時点では削除されるファイルの件数は分からないため、処理対象のファイルエントリの件数 (最大件数) を示す。削除されるファイルが結果として0件になる場合も確認は表示される。Fast では確認の直前に [Fast警告](#warning) の警告を表示する。中止した場合、target のエントリには触れない。

<a id="output"></a>
## 表示と進捗

<a id="result-lines"></a>
### 結果行と表示用エスケープ

`analyze` と `delete` の個別結果は、1件1行で次の形式とする。

```text
<状態名を 20 桁に左詰め>  <Entry> -> <Target>
```

- 状態名の列だけを固定幅 (最長の `SKIPPED_SPECIAL_FILE` に合わせた 20 桁) とし、空白2個の後に Entry、` -> ` の後に Target を置く。Entry と Target の列はそろえない。
- **Entry** は ZIP エントリの FullName を変換せずに表示する (`\` を `\\` にしない)。表示された Entry は、そのまま `--entries` の1行として使える。`>` はエントリ名にも Windows パスにも現れないため、` -> ` の区切りは一意である。ただし、端末上で危険な文字・誤認しやすい文字 (書式文字 (双方向制御文字などを含む Unicode の Cf)、C1 制御文字 U+0080〜U+009F、行区切り・段落区切り U+2028・U+2029) を含む名前は、その文字をエスケープ表記で表示し、**`--entries` へそのまま転記できない**ことが分かる印 ` [表示用にエスケープ済み: --entries へそのまま転記できません]` を行末に付ける。危険な文字は `\u{XXXX}` (補助平面もコードポイント単位) で表示する。FATAL・STOPの原因用SafeDisplay.Escapeでは表記を一意にするため `\` と `"` もエスケープし、結果行の表示用エスケープとは区別する。[ZIP名と構造](zip.md#names) の検査を通った名前には C0 制御文字・U+FFFD は含まれない。
- **Target** は、人間が確認するための target 内の対応する場所で、target の最終パス (`\\?\` を除いた形) に `\` と ZIP の成分を連結したものである。`MISSING` では実在しない期待位置を示す。Target の表示を `delete` の入力として使わない。表示の際は Entry と同じ文字だけをエスケープし、`\` は区切りとしてそのまま表示する。
- `analyze` と `delete` の結果表示の前に、Target の意味 (target 内の対応する場所であり、`MISSING` の場合は実在しない期待位置を示すこと) をヘッダーまたは凡例で一度示す。
- `SKIPPED_SPECIAL_FILE` の行には、判定した理由を行末に付ける (`SkipReason` → 表示): 親 reparse → `(親が reparse)`、ディレクトリ → `(ディレクトリ)`、reparse → `(reparse)`、hardlink → `(hardlink)`、ADS → `(ADS)`、ZIP 自身 → `(ZIP 自身)`、属性 → `(属性)`。
- `DELETE_FAILED` の行は ` : <理由>` を続ける。オープンできなかった場合の理由は「削除用に開けません (<Win32 の説明>)。識別確認の時点では同じファイルに見えるため、削除せずに残しました。内容は確認していません」。
- 名前を安全に表示できない部分はエスケープ表記で示す。FATAL・STOP などの原因の表示では、エントリ番号も併記する。
- 利用者向けの文言は日本語を基本とする。状態名・分類名は英大文字の名前 (`MATCHED` など) を使う。具体的な文言は以下の各節で定める。

<a id="analyze-output"></a>
### analyzeの表示

- ヘッダー (Fast の警告 (Fast のみ)、ZIP、target の最終パス、モード、Target の凡例)。
- 結果行を分類のカテゴリー順 (Strict: `MATCHED`、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DIRECTORY`。Fast: `SAME_SIZE`、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DIRECTORY`)、カテゴリー内は ZIP の順に表示する。結果が0件のカテゴリーは行を出さない。
- 正常に完走した場合、合計行に、そのモードで意味のある全カテゴリーの件数を0件も含めて表示する。Strict では `SAME_SIZE` を、Fast では `MATCHED` を (件数0としても) 表示しない。
- FATAL の場合、判定済みのエントリの結果行と件数、FATAL の原因エントリが1件あること、未判定の件数 (パスは列挙しない) を表示し、原因エントリと原因を表示して、`analyze` は削除を行わないこと (削除0件) を明示する。
- 最後に、`analyze` は削除しないこと、削除は `delete` で行い、`delete` は実行時の状態を改めて検証することを示す。


具体的な表示:

- 見出し行 `Status                Entry -> Target` の後に結果行。
- 完走: `合計: N エントリ (MATCHED a、MODIFIED b、MISSING c、SKIPPED_SPECIAL_FILE d、DIRECTORY e)` (Fast は `SAME_SIZE` を先頭に)。続けて `analyze は削除しません。削除は unextract delete で行います (delete は実行時の状態を改めて検証します)。`
- FATAL: stdout に `判定済み: N エントリ (...)`、`FATAL: 1 エントリ (#n)`、`未判定: M エントリ`。ZIP 事前検証の FATAL では結果行と見出しを出さず `判定済み: 0 エントリ` と `未判定: M エントリ`。アーカイブを開く・列挙する段階 (Prepare の手順2) の FATAL (RAR のボリューム (`ARCHIVE_MULTI_VOLUME`。[RARの列挙](rar.md#listing)) と列挙中の上限超過 ([RAR上限](rar.md#limits)) を含む) では、ヘッダー・見出し・件数を出さない。stderr に `FATAL: エントリ #n "<名前>": <原因> (<詳細>)` と `解析を中止しました。analyze は削除を行いません (削除0件)。`

<a id="delete-output"></a>
### deleteの表示

- ヘッダー (Fast の警告 (Fast のみ)、ZIP、target の最終パス、モード、Target の凡例、処理対象の件数 (`--entries` の指定の有無を含む))。全件の事前一覧は表示しない (その役割は `analyze`)。
- 処理したファイルエントリごとに、処理した順に1行ずつ結果を表示する。状態名は `DELETED`、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DELETE_FAILED`、`STOPPED`。`DIRECTORY` と処理対象外のエントリには行を出さず、件数だけを要約に出す。
- STOP した場合は、`STOPPED` の行を出し、停止の原因となったエントリと理由を表示する。停止の原因が削除の成立確認 ([削除順序](filesystem.md#delete-flow) の手順9) の場合などは、その対象を「削除された可能性あり」と表示する。
- 終了時に、削除済み、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DIRECTORY`、`DELETE_FAILED`、処理対象外、未処理の件数を要約する。STOP した場合は、STOP より前に削除したファイルは元に戻らないこと、STOP の対象を削除していないこと (または削除された可能性があること)、未処理のエントリには触れていないことを明示する。`DELETE_FAILED` が1件以上あれば、STOP がなくてもエラーで終了すること ([引数と終了状態](#arguments)) を件数とともに表示する。
- 確認プロンプト ([確認](#confirmation)) は、逐次処理で削除が1件ずつ行われること、途中で停止した場合それまでに削除したファイルは戻らないことを示す。


具体的な表示:

- ヘッダーの後に `対象: 全 N エントリ` または `対象: N エントリ中 K エントリ (--entries)`。
- 確認プロンプト (Fast は直前の行に [Fast警告](#warning)):

  ```text
  最大 K 件のファイルエントリを1件ずつ検証し、条件を満たしたものをその場で完全に削除します。
  途中で停止した場合、それまでに削除したファイルは元に戻りません。
  続行しますか? [y/N]
  ```

  中止: `中止しました。削除0件。` 非対話で `--yes` なし: `標準入力が対話的でなく --yes も無いため、確認できません。` の後に `中止しました。削除0件。`。
- 要約: `要約: 削除済み a、MODIFIED b、MISSING c、SKIPPED_SPECIAL_FILE d、DIRECTORY e、DELETE_FAILED f、処理対象外 h、未処理 g`。処理対象外は `--entries` で選択されなかった ZIP エントリの件数、未処理は STOP により処理を完結しなかった選択対象 (STOP の対象を除く) の件数で、ZIP では STOP の対象より後ろの選択対象である (RAR で STOP の対象より前に未処理が生じる場合は [RARの読む範囲と順序](rar.md#session))。対象外の target は解決しない。
- STOP した場合 (stdout): `途中で停止しました。それまでに削除した a 件は元に戻りません。<Entry> は削除していません。未処理の g 件には触れていません。` (成立確認の失敗などでは「削除していません」を「削除された可能性があります」にする)。stderr: `停止: エントリ #n "<名前>": <理由>[ (削除された可能性あり)]` と `以後の処理を停止しました (削除済み a、DELETE_FAILED f、未処理 g)。`
- STOP なしで `DELETE_FAILED` がある場合 (stderr): `DELETE_FAILED が f 件あるため、エラーとして終了します (削除済み a)。`
- Prepare の失敗: stderr に `入力エラー: ...` または `FATAL: ...` と `削除開始前に中止しました。削除0件。`

<a id="interruption"></a>
### 中断時の表示と実削除

逐次処理の途中で終わった場合 (強制終了、Ctrl+C など)、出力先が正常に書けていた範囲の、受け取った完全な結果行 (改行で終わる行) について次が成り立つ。

- 完全な結果行として報告した `DELETED` は削除済みである。
- 最後に報告した結果の次に処理していた1件は、結果行が完結する前に削除されていることがある。これは最終確認 ([削除順序](filesystem.md#delete-flow)) まで通った候補 (Strict では全バイト一致) に限る。それより後のエントリには触れていない。
- 最終確認を通っていないファイルは削除しない。

結果行はそのファイルの削除用ハンドルを閉じた後に書き、書けなければ後続を処理しないことによる。読み手の消失、途中で捨てた出力、電源断には当てはめない。出力の失敗と「削除された可能性あり」の扱いは上の規定のとおり。`--jsonl` の同じ規則は[完了境界](machine-output.md#boundary)にある。

<a id="streams"></a>
### 出力先と進捗

出力先: 結果行、ヘッダー、凡例、合計・要約、FATAL 時の判定済み・未判定の件数は標準出力に書く。確認プロンプトは確認の入出力と同じ経路で表示する。入力エラー、FATAL の原因、STOP の原因、内部エラー (想定外の例外) と、エラーで終わる理由は標準エラー出力に書く。

内部エラーの文言は `内部エラー: 想定外の例外が発生しました (<例外型名>: <表示用エスケープ済みメッセージ>)` とする。

進捗は標準エラー出力に `Checking n / total` (`analyze`)、`Processing n / total` (`delete`) を CR で1行上書きする。最初・100 件ごと・最後だけ書く。標準エラー出力がリダイレクトされているときは表示しない。`delete` では結果行を標準出力に書く前に、進捗の行を消してから書く (同じ端末で行が混ざらないようにする)。

本節の出力先と進捗の規定は `--jsonl` なしの実行に適用する。機械可読出力と実行ログ (`--log`) は[機械可読出力](machine-output.md#destinations)による。

詳細な終了コード、Ctrl+C 専用の後処理は設けない。Ctrl+C などの中断で未検証のファイルを削除しないことは守る ([中断時の表示と実削除](#interruption))。

<a id="warning"></a>
## ヘッダー・凡例・Fast警告

<a id="archive-wording"></a>
**形式名の読み替え**: 本書の利用者向けの文言で、処理中のアーカイブを指す「ZIP」(Fast警告の「ZIP から」、SkipReason の `(ZIP 自身)`、entries の「ZIP に一致するエントリがありません」など) は、RAR の実行では「RAR」と表示する。引数エラーでも、入力パスから [形式の判定](rar.md#format) に従って RAR と確定できる場合に限り、そのアーカイブを指す「ZIP」を「RAR」に読み替える。形式が未確定 (アーカイブの未指定・空・複数指定など) なら既存の文言を維持する。引数の受理条件・エラーの優先順位と ZIP の実行の文言は変えない。状態名・コード・機械可読出力の値 (同じ説明を使うメッセージを除く) は形式によって変えない。

```text
Archive: <ZIP のパス (指定されたまま、表示用エスケープ)>
Target:  <target の最終パス (\\?\ を除く)>
Mode:    Strict | Fast
凡例: Target は target 内の対応する場所です。MISSING の場合は実在しない期待位置を示します。Target は確認用で、--entries には Entry を書きます。
```

Fast の実行では、次の警告を (1) `analyze` の結果表示のヘッダーの最初の行、(2) `delete` のヘッダーの最初の行、(3) `delete` の `[y/N]` の直前に表示する。結果表示に至る Fast の `analyze` では、FATAL の場合も含めてヘッダーの警告を必ず表示する。`--yes` では確認プロンプトがないため、`delete` のヘッダーの警告だけが表示される。ヘッダーの警告は標準出力に書く。`--jsonl` では警告文を出さず、代わりに `run` レコードの `mode` を必ず出す ([機械可読出力](machine-output.md#destinations))。

```text
警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。
```

<a id="input-errors"></a>
## 入力エラー・FATALの案内

- 使い方:

  ```text
  使い方: unextract analyze <archive> --target <dir> [--fast] [--jsonl]
          unextract delete <archive> --target <dir> [--fast] [--entries <file>] [--yes|-y] [--jsonl] [--log <file>]
  ```

- サブコマンドなし・旧形式: `入力エラー: サブコマンド (analyze または delete) を指定してください。旧形式 (unextract <archive> --target <dir>) は廃止しました。` + 使い方。
- `--dry-run`: `入力エラー: --dry-run は廃止しました。削除せずに結果を確認するには unextract analyze を使ってください。` + 使い方。
- entries: `入力エラー: --entries の <n> 行目: <理由>` (例: 「ZIP に一致するエントリがありません ("Bin/a.dll")。大小文字だけが違うエントリ "bin/a.dll" があります」「UTF-8 として読めません」「UTF-16 で保存されています。UTF-8 で保存してください」「空行です」「3 行目と同じです」「ディレクトリエントリは指定できません」)。
- RAR の DLL が利用できない ([版の固定](rar.md#pinning)、Prepare の FATAL): FATAL の原因を `UnRAR.dll を使用できないため、RAR を処理できません (<理由>)。<読み込み元の絶対パス> に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。` とする。<理由> は `見つかりません`、`版が一致しません (SHA-256)`、`読み込めません (<Win32 の説明>)`、`版が一致しません (RARGetDllVersion=<値>)` のいずれか。続く行は他の Prepare の FATAL と同じ (`削除開始前に中止しました。削除0件。` など)。
