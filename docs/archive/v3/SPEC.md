# unextract MVP 仕様 v3

## 1. 目的

ZIPファイルの内容と現在も完全に一致するtarget内のファイルだけを、安全側に倒して削除するOSS CLIツール。

ファイルが「過去にそのZIPから展開されたものか」は判定しない。判定するのは「現在の内容がZIP内のエントリと検証可能な形で一致するか」のみ。

最優先事項: ユーザー自身が作成・変更したファイルを誤って削除しないこと。

> unextract does not determine whether a file "probably came from" an archive.
> It removes a file only when its current contents can be verified against the archive.

## 2. 設計原則

- 「ZIPと現在のファイルが同一であると検証できた場合だけ」削除する
- 少しでも不確実なら削除しない(Fail Safe)
- ZIPのヘッダー情報(CRC-32、サイズ)は自己申告値であり、同一性の根拠にしない
- ZIP内のパスは信用しない
- 再帰削除APIは使用しない(コード規約)
- ZIP全体・エントリ全体をメモリに展開しない(ストリーミング必須)

## 3. MVPの対象

- Windows 11 / CLI / ZIP形式のみ / 単一ZIP
- 実装言語: C#(.NET 現行LTS、`global.json` で固定)
- OSSとして公開可能な構成(LICENSE、README、CI)

## 4. CLI

```
unextract <archive.zip> --target <dir> [options]
```

| オプション | 説明 |
|---|---|
| `--target <dir>` | 必須。展開先ディレクトリ |
| `--dry-run` | 解析結果の表示のみ。ファイルシステムを一切変更しない |
| `--yes`, `-y` | 確認プロンプトを省略 |
| `--delete-permanently` | ごみ箱を使わず完全削除する(明示指定時のみ) |

挙動:

- 通常実行でも、削除前に必ず dry-run と同じ解析を行い、結果を表示する
- 確認プロンプトは `[y/N]`、デフォルトは中止
- stdin がTTYでなく `--yes` もない場合は、何も削除せず終了コード 2 で中止する
- `--target` の拒否条件(解決後の実パスで判定):
  - ドライブルート(`C:\` など)
  - `target == %USERPROFILE%`(ユーザープロファイルディレクトリそのもの)。配下の通常ディレクトリ(`Downloads` など)は許可する
  - Windowsディレクトリ、`Program Files`、`Program Files (x86)`、`ProgramData` それ自体
  - UNCパスのルート
  - 【方針案・未確定】上記システムディレクトリの配下も拒否する(安全側)

## 5. 分類カテゴリ

ZIP内の各エントリは、次のいずれか1つに分類される。

| カテゴリ | 意味 | 削除 |
|---|---|---|
| `MATCHED` | ZIPの内容とバイト単位で一致 | する |
| `MODIFIED` | 存在するが内容が異なる | しない |
| `MISSING` | targetに存在しない | - |
| `UNSAFE_PATH` | パス検証で拒否 | しない |
| `SKIPPED_SPECIAL_FILE` | symlink / junction / reparse point / ハードリンク / 通常ファイル以外 | しない |
| `ERROR` | 判断不能(読み取り不能、権限なし、暗号化、CRC不整合、リソース上限超過、パス解決不能など) | しない |
| `AMBIGUOUS` | 重複・衝突など曖昧なエントリ | しない |

注: `Unrelated files`(target内でZIPに無いファイル)はMVPでは集計しない。

## 6. 同一性判定

ファイルごとに次の順で判定する。0バイトファイルも例外にしない。

1. `stat` でディスク上のサイズを取得し、ZIPエントリのサイズと比較する。不一致なら `MODIFIED`
2. サイズ一致なら、ZIPエントリをストリーム展開しながら、ディスク上のファイルとバイト比較する。差異があれば即 `MODIFIED`
3. 展開量が「ZIPエントリ宣言サイズ」または「ディスク上のファイルサイズ」を超えた時点で打ち切り、`ERROR` とする
4. 展開完了後、展開バイト数が宣言サイズと一致し、CRC-32もZIPエントリの値と一致することを確認する。不一致ならZIP破損として `ERROR`
5. 全バイト一致、かつ手順4も成功した場合のみ `MATCHED`

0バイトエントリも、展開が正常終了し、出力が0 bytesで、CRC-32が正常であることを確認して初めて `MATCHED` とする。

CRC-32は「ZIP自体の整合性確認」と「早期不一致判定」にのみ使い、同一性の根拠としない。

### 6.1 リソース制限

- 展開は必ずストリーミングで行い、固定サイズのチャンクで読んで比較する。ZIP全体・エントリ全体をメモリに展開してはならない
- 打ち切り条件として「ディスク上のファイルサイズを超えた瞬間」を必須とする
- 内部ハードリミットとして、エントリ総数と1エントリあたりの展開量の絶対上限を定数で持つ。超過したエントリ、またはアーカイブは `ERROR`(値は実装PLANで実測して決定。初期案: エントリ総数 100,000、1エントリ 16 GiB)
- MVPでは `--max-file-size` のようなユーザー向けオプションは設けない

## 7. パス検証(最重要)

### 7.1 正規化前の拒否ルール

次のいずれかに該当するエントリは `UNSAFE_PATH` とし、ファイルシステムにアクセスしない。

- `..` 成分を含む(正規化すればtarget内に収まるものも拒否)
- 絶対パス、先頭が `/` または `\`
- ドライブレター(`C:\...`、`C:foo` のドライブ相対形式を含む)
- UNCパス(`\\server\share`)、`\\?\`、`\\.\`
- コロンを含む成分(NTFS代替データストリーム)
- NUL文字、制御文字
- Windows予約名(`CON`、`PRN`、`AUX`、`NUL`、`COM1-9`、`LPT1-9`。拡張子付きも含む)
- 成分の末尾がドットまたはスペース
- `\` と `/` の区切りはどちらも成分区切りとして扱う

### 7.2 結合後の検証

- targetディレクトリは事前に実パスへ解決しておく
- `target + エントリパス` を結合した結果が、パス成分単位の比較でtarget配下であることを確認する
- 文字列の `startswith` は使わない(`C:\extracted2` が `C:\extracted` で始まるため)
- 比較は大文字小文字を区別しない

### 7.3 reparse point 検査

- target自体、targetから対象ファイルまでの全中間ディレクトリ、対象ファイル自身のすべてについて `FILE_ATTRIBUTE_REPARSE_POINT` を検査する
- 1つでも該当すれば `SKIPPED_SPECIAL_FILE`
- ZIPエントリ側のsymlink属性(external attributes のUnixモード)がある場合も `SKIPPED_SPECIAL_FILE`
- 対象が通常ファイル以外(デバイス等)の場合も `SKIPPED_SPECIAL_FILE`
- ハードリンク(リンク数 > 1)は `SKIPPED_SPECIAL_FILE` として削除しない
- 【方針案・未確定】OneDriveなどのクラウドプレースホルダ(reparse point)も、同様にスキップする(安全側)

## 8. 曖昧なエントリ

次は `AMBIGUOUS` とし、関連するすべてのエントリを削除しない。

- 同一パスの重複エントリ
- 大文字小文字違いのみのパス重複
- ファイルエントリとディレクトリエントリのパス衝突

## 9. ファイル名の文字コード

- UTF-8フラグ付きエントリはそのままUTF-8として扱う
- UTF-8フラグなしのエントリはCP437として解釈する
- 文字化けにより対応ファイルが見つからなければ `MISSING` になる(安全側)
- CP932対応はMVP対象外。READMEの制限事項に明記する
- 実装ライブラリの実際の挙動が上記と一致するかは、実装PLANで実機確認する

## 10. 削除処理

### 10.1 ファイル

- 解析結果の表示 → 確認 → 削除の順
- 各ファイルの削除直前に、サイズ確認とバイト比較を再実行する。再検証で不一致・失敗なら削除せず `MODIFIED` または `ERROR` に再分類する
- デフォルトはごみ箱へ送る
- ごみ箱APIが完全削除にフォールバックする可能性がある場合(ネットワークドライブ、USBメモリ、容量超過など)は、削除せず `ERROR` とする。完全削除は `--delete-permanently` 指定時のみ

**TOCTOUについて**

- 再検証によってリスクは縮小するが、比較後にファイルを閉じ、パスをごみ箱APIへ渡すまでの間の置換は完全には防げない
- 実装PLANでの調査項目:
  - 比較時に取得したファイルID(`FILE_ID_INFO` またはボリュームシリアル+ファイルインデックス)を、削除直前に再取得して同一か確認できるか
  - `IFileOperation` に、ShellItem経由で対象を渡せるか(パス文字列を経由せずに済むか)
  - 比較中は共有モードで開いたままにして、置換・削除を抑止できるか

**実装PLANでの調査項目(ごみ箱)**

- `IFileOperation` の呼び出し(CsWin32、STAスレッド、COM初期化)
- 完全削除フォールバックを検出または抑止するフラグ(`FOF_ALLOWUNDO`、`FOFX_RECYCLEONDELETE`、`FOF_WANTNUKEWARNING` 等)
- ファイル単位の削除結果の取得(`IFileOperationProgressSink`)
- 読み取り専用属性、ロック中ファイル、長いパス(260文字超)の扱い

### 10.2 ディレクトリ

- 対象は「今回の実行で削除したファイルの祖先ディレクトリ」のみ(targetは含まない)
- ZIPの明示的な空ディレクトリエントリ、および事前から存在した空ディレクトリはMVPでは対象外
- 子から親の順に処理し、各ディレクトリが空であることを確認してから削除する
- 削除は非再帰のrmdirのみ(`RemoveDirectory` 相当)
- ZIPに無いファイルが1つでも残るディレクトリは削除しない
- reparse point のディレクトリは削除しない
- targetは削除しない

## 11. Dry Run

- ファイルシステムを一切変更しない
- 全カテゴリのファイル一覧に加え、「削除予定ディレクトリ」も表示する(ファイル削除後に空になるかをシミュレートして判定)
- 実行前後でtarget全体のスナップショットが一致すること

## 12. ZIPの扱い

- 読み取り専用で開く(書き換え・移動・削除をしない)
- 暗号化エントリは `ERROR`
- ZIP自体が破損・読み取り不能の場合は、何も削除せず終了コード 3

## 13. 出力例

```
Archive: archive.zip
Target : C:\Downloads\extracted

Matched:   18
Modified:   2
Missing:    3
Unsafe:     0
Skipped:    0   (special files)
Error:      0
Ambiguous:  0

Files to remove:
  src/main.py
  ...

Directories to remove (if empty after deletion):
  src/
  images/

Modified files (KEEP):
  config.json
  notes.txt

Proceed with removing 18 files? [y/N]
```

終了サマリー:

```
Unextract complete.

Removed:           18
Modified / kept:    2
Missing:            3
Unsafe / skipped:   0
Error:              0

Archive was not modified.
```

## 14. 終了コード

| コード | 意味 |
|---|---|
| 0 | 正常終了(エラー・スキップなし) |
| 1 | 正常終了だが、一部が `UNSAFE_PATH` / `SKIPPED_SPECIAL_FILE` / `ERROR` / `AMBIGUOUS` |
| 2 | 中止(ユーザー拒否、非対話で `--yes` なし) |
| 3 | 致命的エラー(ZIP破損、target不正など) |

## 15. MVPで「やらない」こと

- Explorer右クリックメニュー、GUI、常駐監視
- 展開履歴の記録、manifest DB
- ZIP以外(7z / RAR / tar.gz)、複数ZIP一括処理
- 展開先の自動推測
- ファイル名だけを根拠とした削除
- 変更されたファイルの強制削除
- ZIP自体の削除
- CP932ファイル名の自動判定
- target内の無関係ファイルの走査・集計
- ユーザー向けのリソース上限オプション(`--max-file-size` など)

## 16. READMEに明記する内容

**ツールの位置づけ**

- 避ける表現: 「ZIPから展開されたファイルを特定して削除する」
- 使う表現: 「ZIP内のファイルと現在も同一であることが検証できた、target内のファイルだけを削除する」
- manifestを持たないため、「展開した事実」は検証できない。検証できるのは「内容の同一性」だけである

**制限事項**

- 内容が同一であれば、ZIP由来でなくても削除対象になる(別経路で置いた同一ファイル、0バイトの `.gitkeep` など)
- 同じファイルを含む別のZIPから展開されたファイルも削除対象になる
- UTF-8フラグなしの日本語ファイル名ZIPは、Missingと判定されることがある
- ハードリンク、symlink、junction(およびクラウドプレースホルダ)は削除しない
- ごみ箱が使えない場所では、デフォルトでは削除しない
- 再検証によってリスクは縮小するが、パスベースのごみ箱APIを使用する限り、検証から削除までの競合窓を完全には排除できない。unextract実行中は、targetディレクトリを他のプロセスで変更しないこと

## 17. テスト要件

### 判定
1. 完全一致 → `MATCHED`
2. 1byte変更(サイズ同一) → `MODIFIED`
3. 同名でサイズ違い → `MODIFIED`
4. ZIPにあるがtargetに無い → `MISSING`
5. targetにユーザー作成ファイルがある → 削除しない
6. 0バイトファイル(正常な0バイトエントリ → `MATCHED`、CRC不正などの壊れた0バイトエントリ → `ERROR`)
7. 暗号化ZIP、破損ZIP、CRC不整合エントリ → 削除しない
8. 宣言サイズを超えて展開されるエントリ → `ERROR`
9. 宣言サイズが巨大なエントリ → ディスク上のファイルサイズ超過で停止し、メモリが増加しない
10. エントリ総数の上限超過 → `ERROR`

### ディレクトリ
11. 空になったZIP由来ディレクトリ → 削除
12. ユーザーファイルが残ったディレクトリ → 削除しない
13. 事前から存在した空ディレクトリ・明示ディレクトリエントリ → 削除しない
14. targetが削除されない

### パス安全性
15. `../` を含む → target外にアクセスしない
16. `a/../b`(正規化するとtarget内) → `UNSAFE_PATH`
17. 絶対パス、`C:foo`、UNC、`\\?\`、ADS、予約名、末尾ドット → `UNSAFE_PATH`
18. `C:\extracted2` のような兄弟ディレクトリの接頭辞一致 → target外として扱う
19. 親ディレクトリがjunction(`mklink /J` で作成) → `SKIPPED_SPECIAL_FILE`
20. symlink / reparse point / ハードリンク → スキップ(symlinkは権限が必要なためCIではjunction中心)

### 曖昧エントリ
21. 同一パス重複、大文字小文字違い重複、ファイル/ディレクトリ衝突 → `AMBIGUOUS`

### 動作
22. `--dry-run`: 実行前後でtarget全体(パス、サイズ、ハッシュ、更新日時)が一致
23. 処理後も元ZIPが残り、ZIPのハッシュと更新日時が不変
24. 検証後・削除前にファイルを書き換える → 再検証で削除されない
25. 検証後・削除前にファイルを別ファイルへ置換する → ファイルID確認で削除されない(実現可能性は実装PLANで調査)
26. 読み取り不能・ロック中ファイル → 削除されない
27. ごみ箱が使えない場所 → デフォルトでは削除されず、`--delete-permanently` 指定時のみ削除
28. 非TTYで `--yes` なし → 何も削除されず終了コード 2
29. `--target` がドライブルート、`%USERPROFILE%` 自体、システムディレクトリ → 拒否
30. `--target` が `%USERPROFILE%` 配下の通常ディレクトリ → 許可

## 18. 実装方針

- 構成: `Unextract.Core`(判定ロジック、Win32非依存)/ `Unextract.Windows`(reparse、hardlink、ファイルID、ごみ箱)/ `Unextract.Cli`
- 判定ロジックは `IFileSystemProbe` / `IDeleter` の抽象越しにし、実際には何も消えない偽Deleterで「削除しない」ことを大量に検証できるようにする
- Win32呼び出しは CsWin32 を使う
- フェーズ: Phase 0(調査スパイク)→ Phase 1(Core + dry-run)→ Phase 2(Windows実装)→ Phase 3(ごみ箱削除、再検証、ディレクトリ削除)→ Phase 4(CLI仕上げ、README、CI)

## 19. MVP完成条件

```
unextract example.zip --target ./example --dry-run
  ↓ 削除対象と残す対象を確認
unextract example.zip --target ./example
```

ZIPとバイト単位で一致する展開済みファイルだけがごみ箱へ送られ、変更済みファイル・ユーザー作成ファイル・ZIP本体は残る。全テストが通ること。