# unextract

本書は利用者向け要約です。仕様の正本は [SPECと担当仕様](docs/SPEC.md)、変更別の入口は [docs/README](docs/README.md)です。

ZIP 内のファイルと照合して、target 内の対応するファイルだけを削除する Windows 11 用の CLI です。既定の Strict モードでは、**削除する直前に、ZIP 内のファイルと全バイト一致すると検証できたファイルだけ**を削除します。`--fast` を指定した Fast モードでは、**パスとサイズだけで判定し、内容は確認しません** (下記「Fast モード」)。

> **注意: `unextract delete` はファイルを実際に削除します。ごみ箱を使わない完全削除で、unextract に復旧手段はありません。** 必ず先に `unextract analyze` で結果を確認してください。

> 検証・手動確認の現在の状態は [OPEN_ISSUES](docs/OPEN_ISSUES.md)を参照してください。旧形式 (`unextract <archive.zip> --target <dir> [--dry-run]`) と `--dry-run` は入力エラーになります。

仕様上の基準は [SPEC](docs/SPEC.md) とその担当仕様 (CLI・ZIP・ファイル安全性) です。この README と食い違う場合は担当仕様を確認してください。文書の一覧と読む経路は [docs/README](docs/README.md) にあります。

## 位置づけ

- 操作は2つです。
  - **`analyze`**: ZIP 全体を検査し、全エントリを target 側で分類して表示します。**何も削除しません。**
  - **`delete`**: 対象のエントリを ZIP の順に1件ずつ、**その時点の target の状態で**検証し、条件を満たしたファイルをその場で削除します。
- 既定の **Strict** モードでは、ZIP から実際に読み出した内容と target 内のファイルの内容を比較し、全バイト一致を確認したファイルだけを削除します。`delete` は、比較に使ったのと同じハンドル (開いたファイル) をそのまま削除するので、比較したファイルと削除するファイルは同じです。
- `--fast` を指定した **Fast** モードでは、ZIP 内のパスと展開後のサイズが target 内のファイルと一致すれば削除の対象にします。内容は比較しません。明示的に選んだときだけ使われます。
- 「その ZIP から展開されたファイルかどうか」(来歴) は検証しません。たまたま同じ内容のファイルも、一致すれば削除の対象になります (Fast では、同じパス・同じサイズであれば内容が違っても対象になります)。
- Strict は、利用者が追加・変更したファイルを誤って削除しないことを最優先とします。Fast は、同じパス・同じサイズのまま内容を変更したファイルを削除し得るため、この点では最優先になりません。どちらのモードでも、不明・判定不能なものは削除しません。

## 動作環境

- Windows 11
- target は NTFS ボリューム上の既存のフォルダーに限ります (NTFS 以外は入力エラー)。
- 次の target は拒否します (入力エラー): ドライブのルート、UNC パス (ネットワーク共有の共有ルートとその配下。解決後のパスが `\\?\UNC\` で始まるもの)、ユーザープロファイルのフォルダーそのもの、Windows フォルダー、Program Files、Program Files (x86)、ProgramData とその配下、target 自体が reparse point (junction など) のもの。

## 使い方

```text
unextract analyze <archive.zip> --target <dir> [--fast]
unextract delete  <archive.zip> --target <dir> [--fast] [--entries <file>] [--yes|-y]
```

| 引数 | 意味 |
|---|---|
| `analyze` / `delete` | 操作 (サブコマンド)。第1引数で必須です |
| `<archive.zip>` | 比較元の ZIP (1つだけ) |
| `--target <dir>` | 比較・削除の対象フォルダー (必須)。オプション名と値を別々の引数で渡します (`--target=dir` の形は不可) |
| `--fast` | その実行全体を Fast モードにします。指定しなければ Strict です |
| `--entries <file>` | (`delete` のみ) 処理するエントリを、ファイルに列挙した ZIP エントリ名に限定します (下記「`--entries`」) |
| `--yes`, `-y` | (`delete` のみ) 削除の確認 (`[y/N]`) だけを省略します。検証は省略しません |

不明なオプション、同じオプションの重複、`--target`・`--entries` の値が無い・空・`-` で始まる、ZIP の指定が無い・複数、`analyze` への `--entries`・`--yes` の指定は入力エラーです。

### 旧形式と `--dry-run` の廃止

以前の形式 `unextract <archive.zip> --target <dir> [--dry-run] [--fast] [--yes]` は廃止しました。サブコマンドの無い形と `--dry-run` は、**何も削除せずに入力エラー**になり、新しい使い方が表示されます。互換の別名はありません。

| 以前 | これから |
|---|---|
| `unextract a.zip --target D:\x --dry-run` | `unextract analyze a.zip --target D:\x` |
| `unextract a.zip --target D:\x` | `unextract analyze ...` で確認してから `unextract delete a.zip --target D:\x` |
| `unextract a.zip --target D:\x --yes` | `unextract delete a.zip --target D:\x --yes` |

`delete` の動き方は以前の通常実行と違います (全件を先に調べてから削除するのではなく、1件ずつ調べてその場で削除します)。下記「動作の流れ」を読んでから使ってください。

### 推奨する手順

1. `analyze` で、どのファイルが ZIP と一致し、どのファイルが変更されているかを確認します。Fast で削除する場合は、`analyze` にも `--fast` を付けます。

   ```text
   unextract analyze archive.zip --target D:\work\extracted
   ```

2. 問題がなければ `delete` を実行します。`[y/N]` で確認され、`y` または `Y` を入力したときだけ処理を始めます。

   ```text
   unextract delete archive.zip --target D:\work\extracted
   ```

3. 一部のファイルだけを削除したい場合は、`analyze` の結果の Entry を entries ファイルに書いて `--entries` で指定します。

   ```text
   unextract delete archive.zip --target D:\work\extracted --entries entries.txt
   ```

**`analyze` の結果は削除の許可証ではありません。** `delete` は `analyze` の結果を使わず、実行した時点の状態を改めて検証します。そのため、間にファイルが変わった場合や、ファイルを読めても削除の権限が無い場合などは、`analyze` と `delete` の結果が一致しないことがあります。

## 動作の流れ

### analyze

1. ZIP の全エントリの名前・構造・上限を検査します (target には触れません)。
2. ZIP の順に target 側の対応するファイルを調べて分類します。Strict では、サイズが一致する通常ファイルについてだけ、ZIP の内容を読んで全バイト比較します。Fast では内容を読まず、サイズが一致する通常ファイルを `SAME_SIZE` とします。
3. 全エントリの結果を1件1行で表示します。何も削除しません。

### delete

1. **事前の検査**: 引数、ZIP、entries ファイル、target フォルダー、ZIP の全エントリの名前・構造・上限を検査します。**ここで問題があれば、1件も削除せずに終了します。**
2. **確認**: `[y/N]` を表示します (Fast では、その直前に警告を表示します)。空入力、EOF、`y`/`Y` 以外の入力は中止です。標準入力が対話的でなく `--yes` もない場合も中止です。この時点では削除される件数はまだ分からないため、処理するファイルエントリの件数 (最大件数) が表示されます。削除されるファイルが結果として0件でも確認は表示されます。
3. **1件ずつの処理**: ZIP の順に、各ファイルについて次を行い、結果を1行表示してから次へ進みます。
   - 大小文字まで一致する実名で target 側のファイルを探し、フォルダー・reparse point・削除しない属性のものは開かずにスキップします。
   - ファイルを削除用に開き、同じファイルであること (File ID、親フォルダー、パス) と、特殊なファイルでないこと (hardlink、ADS など) を確かめます。
   - Strict では、同じハンドルから読んで ZIP の内容と全バイト比較します (1回だけ)。内容が違えば `MODIFIED` として残します。
   - 削除の直前に、開いた直後の状態 (サイズ、日時、属性、リンク数、ADS、親フォルダー、パスなど) から何も変わっていないことを確かめます。
   - 同じハンドルに削除を指示し、削除が成立したことを確かめます。
4. **途中で安全に判定できないものが見つかった場合**: 削除指示前ならそのファイルを残し、**以後の処理を止めます (STOP)**。指示後に成立を確認できない場合は、そのファイルが削除された可能性があります。**それまでに削除したファイルは元に戻りません。** 残りのエントリには触れません。

### 分類と結果

| 表示 | 意味 | `delete` での扱い |
|---|---|---|
| `MATCHED` | (Strict の `analyze`) 安全な通常ファイルで、ZIP の内容と全バイト一致 | 削除する (`DELETED`) |
| `SAME_SIZE` | (Fast の `analyze`) 安全な通常ファイルで、サイズが ZIP の展開後のサイズと一致。**内容の一致は意味しません** | 削除する (`DELETED`) |
| `MODIFIED` | Strict: サイズが違う、または同じサイズで内容が違う。Fast: サイズが違う | 削除しない |
| `MISSING` | 大小文字まで完全に一致する名前のファイルがない (8.3 の短い名前だけで一致するものを含む) | 削除しない |
| `SKIPPED_SPECIAL_FILE` | 特殊なファイルと判定できたもの (下記「削除しない対象」)。理由を `(hardlink)` などと表示します | 削除しない |
| `DIRECTORY` | ZIP のフォルダーのエントリ | 削除しない (`delete` では件数だけ表示) |
| `DELETED` | (`delete`) 検証して削除した | — |
| `DELETE_FAILED` | (`delete`) 他のプログラムが使用中、権限が無いなどで削除用に開けず、改めて調べると同じファイルに見えたもの。削除せずに残し、次へ進みます (Strict でも内容は確認していません) | — |
| `STOPPED` | (`delete`) 安全に判定できず、処理を止めたファイル。指示前は非削除、指示後に成立不明なら削除された可能性あり | — |
| FATAL | (`analyze`) 判定できなかったもの、内容を読む ZIP エントリの異常 (Strict) | — |

### 表示の形式

`analyze` と `delete` は、結果を1件1行で表示します。

```text
MATCHED               bin/a.dll -> D:\Work\A\bin\a.dll
MODIFIED              config/settings.json -> D:\Work\A\config\settings.json
MISSING               docs/readme.txt -> D:\Work\A\docs\readme.txt
SKIPPED_SPECIAL_FILE  cache/data.bin -> D:\Work\A\cache\data.bin (ADS)
```

- 左の列が状態、`->` の左が **Entry** (ZIP 内のエントリ名そのもの)、右が **Target** (target 内の対応する場所) です。
- **Entry はそのまま `--entries` に書けます。** `\` などの文字も変換していません。端末で誤解を招く特殊な文字を含む名前だけはエスケープして表示し、行末に「`--entries` へそのまま転記できません」という印を付けます。
- **Target は確認用**です。`MISSING` では、実在しない (そこにあるはずの) 場所を示します。`--entries` には Target ではなく Entry を書きます。
- `analyze` は、分類ごと (Strict: `MATCHED`、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DIRECTORY`) にまとめて表示し、最後に合計の件数を表示します。Strict では `SAME_SIZE` を、Fast では `MATCHED` を (0件としても) 表示しません。
- `delete` は、処理した順に1行ずつ表示し、最後に削除済み・`MODIFIED`・`MISSING`・`SKIPPED_SPECIAL_FILE`・`DIRECTORY`・`DELETE_FAILED`・処理対象外・未処理の件数を要約します。途中で止まった場合は、それまでに削除したファイルが戻らないことと、未処理の件数を明示します。

### 終了コード

| コード | `analyze` | `delete` |
|---|---|---|
| 0 | 全エントリの分類を完了した | 最後まで処理し、STOP も `DELETE_FAILED` もなかった (削除0件を含む) |
| 1 | 入力エラー、FATAL、内部エラー | 入力エラー、事前の検査の FATAL、STOP、内部エラー、または STOP はなくても `DELETE_FAILED` が1件以上ある |
| 2 | — | 利用者による中止 (確認で `y`/`Y` 以外を入力した、または非対話で `--yes` がない。削除0件) |

### 出力先

- **標準出力**: 結果の行、ヘッダー、合計・要約、FATAL 時の判定済み・未判定の件数。
- **標準エラー出力**: 入力エラー、FATAL の原因、STOP の原因、内部エラー、エラーで終わる理由、進捗表示 (`analyze` は `Checking n / total`、`delete` は `Processing n / total`)。標準エラー出力をリダイレクトしていると進捗は表示しません。

## `--entries`

`delete --entries <file>` は、処理するエントリを限定します。**安全性の確認を減らすものではありません。** 指定したエントリも、指定しない場合と同じように実行時の状態で検証してから削除します。指定しなかったエントリは調べず、削除もしません。

entries ファイルの形式:

- **1行に1つ、ZIP エントリ名** (`analyze` の表示の Entry) をそのまま書きます。Windows のパス (`D:\...`) ではありません。
- 大小文字と区切り (`/` と `\`) は ZIP のエントリ名と完全に一致させます (補正しません)。行の前後の空白も名前の一部として扱います。
- 文字コードは **UTF-8** です (先頭の BOM は1個だけ許します)。UTF-16 などは入力エラーです。
- 改行は LF でも CRLF でもかまいません。最終行の末尾の改行は無くてもかまいません。
- 空行、空のファイル、同じ行の重複、ZIP に無い名前、フォルダーのエントリ (`docs/` のように `/` で終わるもの) は入力エラーです。コメント、ワイルドカード、フォルダー指定による一括指定はありません。
- 上限: ファイル 128 MiB、1行 4,096 バイト、100,000 行。
- entries ファイルの検査は、確認と最初のファイルの処理より前に全て終わります。**entries ファイルに問題があれば、1件も削除しません。**

例:

```text
bin/a.dll
bin/b.dll
textures/logo.png
```

**PowerShell で `analyze` の結果をファイルに保存する場合の注意**: Windows PowerShell 5.1 の `>` は UTF-16 で保存するため、そのファイルから作った entries は入力エラーになります。次のどちらかで UTF-8 にしてください。

```powershell
# Windows PowerShell 5.1: UTF-8 (BOM 付き) で保存する
unextract analyze archive.zip --target D:\work\extracted | Out-File -Encoding utf8 analyze.txt
```

PowerShell 7 の `>` やコマンドプロンプト (`cmd`) の `>` は UTF-8 で保存されます。PowerShell を経由して保存した場合に、日本語などの ASCII 以外の名前が正しく保存されるかは、まだ実機で確認していません (`docs/MANUAL_TESTS.md` の M13)。保存したファイルから、削除したいエントリの行の Entry 部分 (状態名の後から ` -> ` の前まで) を entries ファイルに書きます。

## 重要な注意

- **削除はごみ箱を使わない完全削除です。復旧手段はありません。**
- `delete` は1件ずつ削除します。**途中で STOP した場合、それまでに削除したファイルは戻りません。指示後に成立を確認できないSTOPでは、対象が削除された可能性があります。** 削除前に全体を確認したい場合は、`analyze` を先に実行してください。
- 削除するのは検証済みの個別のファイルだけです。**フォルダーは削除しません** (ZIP にフォルダーとして含まれていても、ファイルの削除後に空になっても)。**ZIP 自身と、ZIP にない target 内のファイルも削除しません** (ZIP にないファイルは表示も集計もしません)。

## Fast モード (`--fast`)

Fast は、誤判定のリスクを利用者が受け入れたうえで、ZIP の内容物らしいファイルを高速に削除するためのモードです。`--fast` を指定したときだけ使われ、既定は Strict です。モードは1回の実行全体で固定されます。Fast で削除する前の確認には `analyze --fast` を使ってください (Strict の `analyze` の結果とは対応しません)。

- **判定**: ZIP 内の相対パスと展開後のサイズ (ZIP に記録されたサイズ) が、target 内の安全な通常ファイルの名前とサイズに一致すれば `SAME_SIZE` とし、`delete` で削除します。**target のファイルの内容も ZIP エントリの内容も読みません。**
- **保証しないこと**: Fast は次を確認しません。
  - ZIP 内のデータと target のファイルの内容が一致すること
  - ZIP のエントリが正常に展開できること (暗号化・破損・未対応の圧縮方式でないこと、ZIP の記録と実データが食い違っていないことを含む)
  - 削除するファイルの内容 (確認するのは、削除用に開いた時点のサイズ・日時・属性などと、それが削除の直前まで変わっていないことまでです)
- そのため、**同じパス・同じサイズで内容の違うファイル (利用者が変更したファイルを含む) も削除されることがあります。** 内容の一致が必要な場合は Strict を使ってください。
- **内容の検証による FATAL・STOP は Fast では起きません**: Strict で FATAL・STOP になる「内容を読む ZIP エントリの異常」(暗号化、破損、CRC-32 不一致など) は、Fast では発生しません。**これは Fast が内容を読まないためであり、ZIP やエントリが健全であると確認されたという意味ではありません。**
- **Strict と同じに行うこと**: パスの検査、大小文字まで一致する実名の確認、target の外へ出ないこと、reparse point・hardlink・ADS・属性による除外、File ID・親フォルダー・パスによる同じファイルであることの確認、削除直前の最終確認、判定できなければ削除しないこと、名前・構造・上限による中止、削除の方式、`DELETE_FAILED` と STOP の境界、終了コードは Strict と同じです。ZIP にない target 内のファイルは、Fast でも表示・削除しません。
- **警告**: Fast では、パスとサイズだけで判定しており、内容の一致と ZIP からの正常な展開は確認していないことを示す警告を、`analyze` の結果の先頭、`delete` の先頭、`delete` の `[y/N]` の直前に表示します (`--yes` では確認が無いため、`delete` の先頭だけ)。Strict では表示しません。
- **性能**: Fast は ZIP の展開と target のファイルの読み取りを行わないため、内容の I/O はかかりません。

## 中止・停止する条件

### 1件も削除せずに終了する場合 (終了コード 1)

`analyze` と `delete` に共通です。`delete` では確認より前に検査します。

- **宣言展開量の合計が 64 GiB を超える ZIP** (1エントリでは 16 GiB 超)。**全エントリが `MISSING` でも、`--entries` で一部だけを指定しても中止します**。target を調べる前に、target の状態と無関係に受け付けるかどうかを決めるためです。
- **パス・構造の不正**: `..`、絶対パス・ドライブ指定、Windows で使えない名前や予約名 (`CON`、`NUL.txt` など)、同じ名前・大小文字だけ違う名前の重複、ファイルとフォルダーの同名衝突、ZIP 内の symlink などの特殊エントリ、名前の復号に失敗した文字 (U+FFFD) を含む名前。
- その他の上限 (エントリ数 100,000、名前 1,024 文字、深さ 128、名前などのメタデータ総量 128 MiB) の超過。
- 引数・target・entries ファイルの入力エラー、ZIP を開けない場合。

### analyze が FATAL で中止する場合

- **内容を読む ZIP エントリの異常 (Strict のみ)**: 暗号化、破損、未対応の圧縮方式、読み取り中の例外、CRC-32 不一致、読み出したサイズが宣言サイズより多い・少ない。**内容を読むのは、target に同じ名前で同じサイズの通常ファイルがあるエントリだけ**です。`MISSING`、サイズ違いの `MODIFIED`、`SKIPPED_SPECIAL_FILE` のエントリは内容を読まないため、それらに破損や暗号化があっても中止しません。
- **比較中の共有違反**: 比較するファイルを他のプログラムが書き込み用に開いている (エディタで編集中など) と開けず、**そのファイル1つで `analyze` が中止されます**。ファイルを閉じてから再実行してください。
- target 側で、存在・種類・属性を判定できない場合 (API の失敗など)。

FATAL のときは、判定済みのエントリの結果、原因のエントリと原因、未判定のエントリの件数を表示します。`analyze` は何も削除しません。

### delete が途中で STOP する場合

上の analyze の FATAL の条件 (比較中の共有違反を除く) は、`delete` ではそのファイルの処理の時点で STOP になります。加えて次の場合も STOP します。削除指示前のSTOPならそのファイルを残し、以後の処理を行いません。**それまでに削除したファイルは戻りません。指示後に成立を確認できないSTOPでは、対象が削除された可能性があります。**

- 削除用に開いた直後や削除の直前の確認で、違うファイルに見える・親フォルダーやパスが変わった・属性・ADS・hardlink が変わった、などの不一致があった場合。
- ファイルやフォルダーが消えた、原因のわからないエラー。
- **削除の指示そのものが失敗した場合** (最終確認の後に別のプログラムが read-only 属性を付けた場合など)。原因を区別できないためです。削除の成立を確認できなかった場合は、そのファイルを「削除された可能性あり」と表示します。

`DELETE_FAILED` と STOP の違い:

- `DELETE_FAILED`: 他のプログラムが使用中 (共有違反)、権限が無いなどで削除用に開けず、改めて調べると同じファイルに見える場合です。そのファイルを削除せずに残して、次のファイルへ進みます。最後まで処理しても、`DELETE_FAILED` が1件以上あればエラー (1) で終わります。
- STOP: 違うファイルに見える、調べられない、などの場合です。以後の処理を止めます。指示前ならそのファイルを残し、指示後に成立を確認できなければ削除された可能性を表示します。

その他の注意:

- **`delete` の実行中に target を操作しないでください。** 処理中のファイルの属性・ADS・hardlink の追加、親フォルダーの ACL の変更 (子のファイルの変更日時 (ChangeTime) が変わる) などは STOP の原因になります。処理に時間がかかる場合、途中で target が変わると STOP が増えることがあります (誤って削除することはありません)。
- **ファイル自体の ACL で削除を拒否していても、親フォルダーの権限 (中の項目の削除の許可) によって削除される場合があります。** これは Windows の通常の権限の挙動で、unextract は個々のファイルの ACL を確認しません。
- 削除したファイルの名前は、unextract がそのファイルを閉じた時点で消えます。検索インデクサやバックアップソフトが削除を妨げない形で読み取り中でも、名前は残りません。
- Ctrl+C などで中断しても、検証していないファイルは削除しません。中断した時点までに削除したファイルは戻りません (`delete` の表示に出た `DELETED` の行が記録になります)。

## 削除しない対象 (`SKIPPED_SPECIAL_FILE`)

- 既定以外のデータストリーム (ADS) を持つファイル (ダウンロード時に付く `Zone.Identifier` を含む)
- hardlink (リンク数が2以上)
- reparse point (symlink、junction、OneDrive などのクラウド placeholder)。フォルダーが reparse point の場合、その下はたどらず削除しません
- read-only、system、temporary、offline などの属性を持つファイル、および unextract が知らない属性を持つファイル
- ZIP 自身 (`analyze` の分類。`delete` では、実行中に保持している ZIP への削除用オープンが共有違反になり、識別確認が一致すれば `DELETE_FAILED`、終了コード 1。ZIP は残り、後続のエントリは処理を続けます)
- ZIP ではファイルなのに target ではフォルダーのもの

次の属性**だけ**では除外しません: hidden、archive、not-content-indexed、NTFS 圧縮、sparse、EFS 暗号化。

## 制限と未確認の事項

- **名前の文字コード**: ZIP のエントリ名は、UTF-8 フラグ付きなら UTF-8、フラグなしなら CP437 として読みます。CP932 (Shift_JIS) などの推測はしません。そのため、**UTF-8 フラグなしで日本語名を格納した ZIP (古い日本語環境の ZIP ツールで作ったものなど) は、名前が CP437 として読まれて target の名前と一致せず、`MISSING` になることがあります** (削除はされません)。
- **ZIP の読み取り**: .NET の `ZipArchive` を使い、独自の ZIP 構造パーサは作りません。独自パーサは攻撃面と実装量を増やし、Strict の削除の安全性の根拠 (読み出したデータと target の全バイト一致) にも必要ないためです。unextract は ZIP の完全な健全性を証明しません。内容を読まないエントリの破損は検出しません (Fast ではどのエントリの内容も読みません)。
- **CRC-32**: `ZipArchive` が CRC を検証しないため、Strict では内容を読むエントリについて unextract が検証します。偶発的な破損・切り詰め・ZIP の記録と実データの食い違いを見つけるための補助検査であり、ZIP の作成者が書き換えられる値なので、**悪意をもって作られた ZIP への防御ではありません**。Strict の削除の根拠は全バイト一致です。
- **性能 (Strict)**: `delete` は、削除するファイルについて ZIP の展開と target の読み取りを1回行います。`analyze` の後に `delete` を実行すると、そのファイルの I/O は合計で約2倍になります。巨大なファイルでは削除用に開いている時間が長くなり、その間そのファイルは他のプログラムから書き込めません。
- **[既知の限界](docs/spec/filesystem.md#limitations)**:
  - `analyze` から `delete` までの間の変更は、`delete` が実行時の状態で判定します (Strict では内容が違えば `MODIFIED`)。
  - 削除用に開いている間、他のプログラムは書き込み・改名・削除のために開けません。
  - 削除用に開いている間でも、属性の変更・ADS の作成・hardlink の追加は防げません。削除の直前に再確認して STOP しますが、再確認から削除の指示までのごく短い間に起きた場合は防げません (ADS はファイルと一緒に削除され、追加された hardlink の名前とデータは残り、read-only の付与は削除の失敗として STOP します)。
  - 読み取りはできても削除の権限が無いファイルは、`analyze` では分類されますが、`delete` では `DELETE_FAILED` などになります。
  - `ZipArchive` が ZIP を開くときのメモリ使用量 (Central Directory 全体の読み込み) は上限で制限できません。メモリ不足の場合は削除前に異常終了します。
- **未確認の事項**: 実施状態・限定付き観測・次の確認は [OPEN_ISSUES](docs/OPEN_ISSUES.md) に集約しています。未確認を成立とは見なさず、判定できなければ削除しない側へ倒します。非NTFS・USN機能のないFSは対象外です。
- JSON 出力、ログファイル、詳細な終了コード、上限を変更するオプションはありません。

## 開発者向け

### ビルドとテスト

- .NET SDK 10.0.401 (`global.json` で固定、`rollForward: latestPatch`)。警告はエラーとして扱います。

```text
dotnet build -c Release
dotnet test -c Release --no-build --logger "console;verbosity=detailed"
```

- `tests/Unextract.Windows.Tests` は実 NTFS 上で、テストが作った fixture の中のファイルを実際に削除します。fixture は `tests/<プロジェクト>/bin/<構成>/<TFM>/fixtures/` に作られ、**テストからは削除しません** (下記の掃除手順を使います)。ACL を変えるテストは終了時に元に戻します。
- 一部のテストは、環境が前提を満たさないと失敗にせず、テスト出力に `前提不成立: ...` と書いてその項目の確認を行わずに終わります。`--logger "console;verbosity=detailed"` を付けると出力に残ります。該当するのは次のとおりです。
  - 8.3 の短い名前が生成されないボリューム (T06 と列挙のテスト)
  - `fsutil file setCaseSensitiveInfo` でフォルダー単位の大文字小文字の区別を有効にできない環境 (T15。権限や Windows の機能の構成によります)
  - `compact /c` または `fsutil sparse setflag` で属性を設定できない環境 (T08)
- 検証の案内は [TESTING](docs/TESTING.md)、未確認・手動実施状態は [OPEN_ISSUES](docs/OPEN_ISSUES.md) にあります。

### E2E テストと手動テスト

`tests/Unextract.E2E.Tests` は、ビルド済みの `unextract.exe` を別プロセスとして起動し、終了コード・標準出力・標準エラー出力と target の結果を確かめます ([TESTING](docs/TESTING.md#e2e) の X 系)。確認プロンプトの機能部分は Windows PTY で確認します。通常 build を含む全体検証 (`dotnet test unextract.sln`) に含まれます。配布形態の publish 版 E2E / PTY 検証には、次の wrapper を使います。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-e2e-tests.ps1
```

wrapper は OS temp 直下の `unextract-e2e-publish-<GUID>` に Release / `win-x64` で一時 publish し、`UNEXTRACT_E2E_EXE` を設定して E2E project 全体を実行します。終了時に環境変数を復元し、自分が作った一時 publish だけを削除します。外部 exe と既存 `bin/` / `obj/` は削除しません。E2E test 自身は publish せず、`UNEXTRACT_E2E_EXE` が未設定なら通常 build 出力を使います。PTY fixture はテストが自動 cleanup し、その他の fixture は既存の掃除手順を使います。

Ctrl+C、進捗表示、コードページやフォント・折り返し・視認性など実端末での確認手順は [`docs/MANUAL_TESTS.md`](docs/MANUAL_TESTS.md) にあります。

### 配布ビルド

`src/Unextract.Cli` は単一ファイルの自己完結型 (win-x64、トリミングなし、出力名 `unextract.exe`) として publish できます。バージョンは `src/Unextract.Cli/Unextract.Cli.csproj` の `Version` の1か所です。

```text
dotnet publish src/Unextract.Cli -c Release -p:PublishProfile=win-x64 -o <リポジトリ外の出力先>
```

### テスト fixture の掃除

`scripts/clean-test-fixtures.ps1` で、テストが残した `tests/*/bin/*/*/fixtures` を掃除します。

```powershell
# 一覧だけ (何も変更しない。既定)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\clean-test-fixtures.ps1
# 実行
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\clean-test-fixtures.ps1 -Execute
```

`-Execute` のときは、(1) `P03_*` の fixture に残った自分の SID の DENY を `icacls /remove:d` で戻し、(2) junction を `rmdir` (`/s` なし) で1つずつ外してリンク先が残ることを確かめ、(3) 残りを `cmd /c rmdir /s /q` で削除します。1つでも失敗したらその時点で止まります。

### CI

`.github/workflows/ci.yml` は push と pull request で、`windows-latest` 上で Release のビルド (警告ゼロ) とテストを行います。続けて、単一ファイルの exe を `dotnet publish` で作り、その exe を `UNEXTRACT_E2E_EXE` に指定して E2E テストを実行します。

## ライセンス

MIT License。詳細は [`LICENSE`](LICENSE) を参照してください。
