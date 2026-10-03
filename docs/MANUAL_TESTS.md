# unextract 手動テスト手順書 (M 系)

仕様の唯一の基準は [`SPEC.md`](SPEC.md)。本書は、自動テスト ([`PLAN_TESTS.md`](PLAN_TESTS.md)) では確認できない項目を、手順どおりに実施すれば誰でも同じ確認ができる形で定める。期待結果は原則として SPEC / PLAN_TESTS とリポジトリ直下の `README.md` に書かれた事実に従う。SPEC が具体的な文言や運用詳細を PLAN に委ねている項目については、確定済みの PLAN の記述に従う。SPEC・README に定めがなく実装の挙動にすぎない点は「観察」として分けて書き、合否の基準にしない。

2026-10-03 に実行モデルを改訂し (`analyze` / `delete`。SPEC 冒頭)、本書の手順を新しい CLI に合わせて書き直した。**新しい手順は、新方式の実装 (`PLAN.md` §3.2) の後に実施する。** 旧形式 (`unextract <archive.zip> --target <dir> [--dry-run]`) で 2026-10-02 に実施した記録は §6 に旧方式の記録として残す (旧手順の M07 は「確認待ち中の変更による停止」を確かめたもので、新方式では期待結果が変わった)。

> **安全上の注意 (必ず読む)**
>
> - **target には、`scripts/make_manual_fixtures.py` が `%TEMP%\unextract-manual\` の下に作った fixture だけを使う。実在のデータ (自分の文書・ダウンロード・作業フォルダーなど) を target にしない。**
> - **`delete` で `y` を入力すること、`delete --yes` を実行することは、fixture のファイルを実際に削除する。ごみ箱を使わない完全削除で、復旧できない。**
> - **コマンドを実行する前に、`--target` の後のパスが `%TEMP%\unextract-manual\<日時>\<シナリオ名>\target` であることを目で確認する。**

## 1. 目的と実施タイミング

- 目的: 対話的なコンソールが必要な挙動 (`delete` の `[y/N]` の確認入力、Ctrl+C、進捗と逐次結果の表示、コードページごとの名前の表示、確認待ち中の外部変更、`--fast` の警告) を実際の端末で確認する。あわせて、改訂で生じた未実測の事項 (SPEC §13) のうち手動で確かめられるものを実測する (M09〜M13)。
- 実施タイミング: 新方式の実装の後に1回。その後は、確認プロンプト・進捗表示・出力のエンコーディングに関わるコード (`src/Unextract.Cli` の `Program.cs`、`CliApplication.cs` の `ConsolePrompt`・`ProgressLine` など) を変更したときに行う。M09〜M13 の実測は、結果を `PLAN_VALIDATION.md` に記録したら、Windows の更新などで前提が変わったと考えられるときだけ再実施する。

## 2. 前提

- Windows 11。
- 通常の PowerShell (Windows PowerShell 5.1 または PowerShell 7) を、Windows Terminal またはコンソールホストで開く。**Git Bash、VS Code などのエディタ内のターミナル、出力をパイプ・リダイレクトする実行は使わない** (標準入力・標準エラー出力がリダイレクトされると、確認と進捗の挙動が変わるため。SPEC §2、§10.4)。ただし M13 は、リダイレクトの挙動そのものを確かめる項目なので、手順どおりにリダイレクトする。M09・M12 は Windows PowerShell 5.1 を使う (.NET Framework の `FileStream` の `FileSystemRights` 指定を使うため)。
- 管理者権限は不要 (管理者として開かない)。
- exe は、publish した単一ファイル版を推奨する (README の「配布ビルド」)。出力先はリポジトリの外にする。例:

  ```powershell
  dotnet publish src/Unextract.Cli -p:PublishProfile=win-x64 -o "$env:TEMP\unextract-publish"
  $exe = "$env:TEMP\unextract-publish\unextract.exe"
  ```

  ビルド出力の `src\Unextract.Cli\bin\<構成>\net10.0-windows\unextract.exe` を使ってもよい。どちらを使ったかを結果に書く。
- Python ランチャー `py` (fixture の作成に使う)。

## 3. fixture の作り方

リポジトリ直下で次を実行する。exe のパスは表示されるコマンドに埋め込むためだけに使い、スクリプトは exe を実行しない。

```powershell
py scripts\make_manual_fixtures.py "$exe"
```

- 表示が文字化けする、または `UnicodeEncodeError` で止まる場合は、先に `$env:PYTHONIOENCODING = 'utf-8'` を設定してから実行する。
- `%TEMP%\unextract-manual\<日時>\` の下に、次のシナリオのフォルダー (それぞれ `archive.zip` と `target\`。`handles` は `target\` だけ) が作られ、最後に各シナリオの項目 ID・期待・実行コマンドが表示される。

| シナリオ | 使う項目 | 内容 |
|---|---|---|
| `yn-n`、`yn-enter`、`yn-y`、`yn-ctrlc` | M01、M02、M03、M04 (`yn-n` は M08 でも使う) | ZIP: `same1.txt`、`same2.txt`、`docs/`、`docs/deep.txt`、`changed.txt`、`missing.txt`。target: `same1.txt`・`same2.txt`・`docs\deep.txt` が一致、`changed.txt` が同じサイズで内容違い、ZIP にない `unrelated.txt` |
| `progress` | M05、M10 | 10 個のフォルダーに分けた 500 ファイル。全て一致 |
| `ja` | M06、M13 | UTF-8 フラグ付きの日本語名 (`資料/報告書.txt`、`資料/写真一覧.csv`、`ファイル名.txt`) と `café░.txt`。target には `資料\報告書.txt` と `café░.txt` だけがある |
| `stop` | M07 | `f01.txt`〜`f10.txt`。全て一致 |
| `crc` | (参考) | E2E の X19 で自動化する。`bad.txt` の CRC-32 だけを書き換えた ZIP |
| `handles` | M09、M12 | `target\sub\held.txt` と、read-only 属性の `target\ro.txt`。unextract は実行しない |

- 項目ごとに別のシナリオを使う (1つのシナリオで削除を伴う項目を続けて行わない)。
- **やり直すときは、スクリプトをもう一度実行して新しい fixture を作る。** スクリプトは既存の fixture を削除・上書きしない (毎回新しい日時フォルダーを作る)。
- 不要になった fixture は、`%TEMP%\unextract-manual\` の下の日時フォルダーごと手で消してよい。リポジトリの規約に合わせて `Remove-Item -Recurse` は使わず、パスを確認してから次のように消す (`handles` の `ro.txt` は read-only のため、先に `attrib -r` で外す)。

  ```powershell
  attrib -r "$env:TEMP\unextract-manual\<日時>\handles\target\ro.txt"
  cmd /c rmdir /s /q "$env:TEMP\unextract-manual\<日時>"
  ```

## 4. 共通の手順

- 各項目の最初に、使うシナリオのフォルダーを変数に入れ、target の中身を記録しておく。

  ```powershell
  $s = "$env:TEMP\unextract-manual\<日時>\<シナリオ名>"
  Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName
  ```

- unextract は次の形で実行し、直後に終了コードを表示する。

  ```powershell
  & $exe analyze "$s\archive.zip" --target "$s\target"
  & $exe delete "$s\archive.zip" --target "$s\target"
  $LASTEXITCODE
  ```

- 実行後にもう一度 `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName` を実行し、実行前と比べる。

## 5. 項目

### M01: `delete` の `[y/N]` に `n`

- 目的: 確認で `y`/`Y` 以外を入力すると中止し、target のエントリに触れないこと (SPEC §2、§3.2)。
- シナリオ: `yn-n`
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\yn-n"`
  2. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
  3. `& $exe delete "$s\archive.zip" --target "$s\target"`
  4. `[y/N]` の確認が表示されたら `n` を入力して Enter
  5. `$LASTEXITCODE`
  6. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
- 期待結果:
  - 確認の前に、ヘッダー (Archive、Target、Mode、凡例) と `対象: 全 6 エントリ` が表示され、**結果行 (`MATCHED`・`DELETED` など) は表示されない** (確認は最初のエントリの処理の前。SPEC §3.2、§10.3)。確認文は、最大 5 件のファイルエントリを1件ずつ検証して削除すること、途中で停止した場合に削除済みのファイルが戻らないことを示す (`PLAN.md` §5.4)。
  - 中止し、削除0件。「中止しました。削除0件。」。終了コード 2 (SPEC §2)。
  - 手順 6 の一覧が手順 2 と同じ。

### M02: `delete` の `[y/N]` に空 Enter

- 目的: 空入力は中止になること (SPEC §2)。
- シナリオ: `yn-enter`
- 手順: M01 と同じ (シナリオ名を `yn-enter` にする)。手順 4 では何も入力せずに Enter だけを押す。
- 期待結果: M01 と同じ (中止、削除0件、終了コード 2、target は変わらない)。

### M03: `delete` の `[y/N]` に `y`

- 目的: `y` の明示入力でだけ逐次処理を始め、全バイト一致したファイルだけが削除され、結果が1件ずつ表示されること (SPEC §2、§8.3、§10.3)。
- シナリオ: `yn-y`
- 手順: M01 と同じ (シナリオ名を `yn-y` にする)。手順 4 で `y` を入力して Enter。**実行前に `--target` のパスを確認する。**
- 期待結果:
  - 処理した順に1行ずつ、`DELETED same1.txt -> ...`、`DELETED same2.txt -> ...`、`DELETED docs/deep.txt -> ...`、`MODIFIED changed.txt -> ...`、`MISSING missing.txt -> ...` の形の結果行が表示される (状態名の列は 20 桁、ZIP の順。`docs/` の DIRECTORY は行を出さない)。`unrelated.txt` は表示されない (SPEC §1)。
  - `same1.txt`、`same2.txt`、`docs\deep.txt` だけが削除される。`changed.txt`、`unrelated.txt`、`docs\` フォルダー、`archive.zip` は残る。
  - 要約 `要約: 削除済み 3、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1、DELETE_FAILED 0、未処理 0` が表示される。
  - 終了コード 0 (SPEC §2)。

### M04: `delete` の `[y/N]` で Ctrl+C

- 目的: 確認待ちでの中断で、何も削除されないこと (SPEC §10.4「Ctrl+C などの中断で未検証のファイルを削除しないことは守る」)。
- シナリオ: `yn-ctrlc`
- 手順: M01 と同じ (シナリオ名を `yn-ctrlc` にする)。手順 4 で何も入力せずに Ctrl+C を押す。
- 期待結果:
  - target の一覧が実行前と同じ (削除0件)。
  - 終了コードは SPEC に定めがない (Ctrl+C 専用の後処理は MVP に含めない)。**`$LASTEXITCODE` の値を事実として記録する** (合否の基準にしない)。
  - 観察: 表示されたメッセージ (あれば) と、終了後のプロンプトの状態を記録する。

### M05: 進捗と逐次結果の表示

- 目的: `analyze` の `Checking n / total` と `delete` の `Processing n / total` が標準エラー出力に表示され、標準エラー出力をリダイレクトすると表示されないこと。`delete` で結果行と進捗が混ざって読めなくならないこと (SPEC §10.4)。
- シナリオ: `progress` (500 エントリ)。M05 では削除を伴う実行を1回だけ行う。
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\progress"`
  2. `& $exe analyze "$s\archive.zip" --target "$s\target"`
  3. `$LASTEXITCODE`
  4. `& $exe analyze "$s\archive.zip" --target "$s\target" 2> $null`
  5. `$LASTEXITCODE`
  6. **`--target` のパスを確認してから** `& $exe delete "$s\archive.zip" --target "$s\target" --yes`
  7. `$LASTEXITCODE`
- 期待結果:
  - 手順 2: `Checking n / total` の形の進捗が表示され、結果行 (`MATCHED` 500 行) と合計行が続く。終了コード 0。
  - 手順 4: 進捗は表示されない。結果行と合計行は表示される。終了コード 0。
  - 手順 6: `Processing n / total` の進捗と、`DELETED` の結果行 500 行が表示され、`要約: 削除済み 500、...、DELETE_FAILED 0、未処理 0` が表示される。**結果行が進捗の表示の途中に続いて崩れることがない。** 終了コード 0。
  - 観察 (合否の基準にしない): 進捗が1行の上書きで更新されるか。結果行の流れの中で進捗がどう見えるか。
- 確認できないこと: 500 件では表示が速く、途中の値が目で追えないことがある。その場合は「最後の値だけ見えた」などと事実を記録する。

### M06: 日本語名の表示 (コードページ 932 / 65001)

- 目的: 日本語名と CP437 由来の文字を含む名前が、コンソールのコードページによらず、Entry の列に変換されずに表示されること (SPEC §4.1、§10.1)。
- シナリオ: `ja` (`analyze` だけを使い、削除しない)
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\ja"`
  2. `chcp` を実行し、表示された元のコードページ (例: 932) を記録する。
  3. `chcp 932`
  4. `& $exe analyze "$s\archive.zip" --target "$s\target"`
  5. `$LASTEXITCODE` と `chcp` を実行して記録する。
  6. `chcp 65001`
  7. 手順 4・5 をもう一度行う。
  8. `chcp <手順 2 で記録した値>` で元のコードページに戻す。
- 期待結果:
  - 手順 4・7 とも、`資料/報告書.txt`・`café░.txt` が `MATCHED`、`資料/写真一覧.csv`・`ファイル名.txt` が `MISSING` として、Entry と Target の名前がそのまま (文字化けせず、`\u{...}` のエスケープにならずに) 表示される。転記不可の印は付かない。終了コード 0。
  - CP437 由来の名前は `café░.txt` (c・a・f・é・░ の5文字 + `.txt`) と表示される。
  - 観察 (合否の基準にしない): 手順 5 の `chcp` の値 (unextract の実行後にコードページが実行前の値のままか)。
- フォントによる表示: 文字が `□` (豆腐) で表示されるのは、端末のフォントにその文字がないためで、unextract の出力の問題ではない。この場合は「フォント由来の □」と備考に別記し、文字化け (別の文字や `?` に置き換わる) と区別する。

### M07: 確認待ち中の外部変更 (現在状態での判定)

- 目的: `delete` が確認の後に各ファイルを**その時点の状態で**検証すること。確認待ちの間に変更されたファイルは、変更後の内容で判定され、削除されないこと (SPEC §2、§3.2、§8.3)。
- シナリオ: `stop`
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\stop"`
  2. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
  3. **`--target` のパスを確認してから** `& $exe delete "$s\archive.zip" --target "$s\target"`
  4. `[y/N]` の確認が表示されたら、まだ何も入力しない。
  5. 2つ目の PowerShell ウィンドウを開き、`Add-Content -Path "$env:TEMP\unextract-manual\<日時>\stop\target\f10.txt" -Value "changed"` を実行する。
  6. 1つ目のウィンドウに戻り、`y` を入力して Enter。
  7. `$LASTEXITCODE`
  8. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
- 期待結果:
  - `f01.txt`〜`f09.txt` は `DELETED`、`f10.txt` は `MODIFIED` (サイズが変わったため) と表示され、`f10.txt` だけが残る。
  - STOP にはならない (確認待ちの間はファイルを開いておらず、確認の後の検証が変更後の状態を見るため)。要約は削除済み 9、MODIFIED 1。
  - 終了コード 0。
- 旧方式との違い: 旧方式では同じ操作で `f10.txt` の削除直前再検証が不一致になり停止した (§6 の旧記録)。新方式では確認が比較より前にあるため、期待結果が変わった。

### M08: `--fast` の警告と確認プロンプト

- 目的: `--fast` の `analyze` のヘッダー、`delete` のヘッダー、`delete` の `[y/N]` の直前に Fast の警告が表示され、Strict では表示されないこと (SPEC §10.4、§15.6、`PLAN.md` §5.2)。
- 機能部分は E2E PTY (`PLAN_TESTS.md` X28) で自動確認する。以下の手動手順は、実端末のフォント・折り返し・視認性の確認用として残す。
- シナリオ: `yn-n` (どの実行も `n` で中止し、削除しない)。M01 の後に続けて使う場合は、M01 で target が変わっていないことを確かめてから使う。
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\yn-n"`
  2. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
  3. `& $exe analyze "$s\archive.zip" --target "$s\target" --fast`
  4. `& $exe delete "$s\archive.zip" --target "$s\target" --fast`
  5. `[y/N]` の確認が表示されたら、その直前の行を記録してから `n` を入力して Enter。`$LASTEXITCODE`
  6. `& $exe delete "$s\archive.zip" --target "$s\target"` (Strict)。`[y/N]` の直前の行を記録して `n`。`$LASTEXITCODE`
  7. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
- 期待結果:
  - 手順 3: ヘッダーの先頭行が Fast の警告 (`PLAN.md` §5.2 の文言)。結果は `SAME_SIZE` 4 (`same1.txt`、`same2.txt`、`docs/deep.txt`、`changed.txt`)、`MISSING` 1、`DIRECTORY` 1 で、`MATCHED` は表示されない。
  - 手順 4・5: ヘッダーの先頭行と、`[y/N]` の確認文の直前の行が、同じ Fast の警告。中止し、削除0件、終了コード 2。
  - 手順 6: 警告はどこにも表示されない。中止し、終了コード 2。
  - 手順 7 の一覧が手順 2 と同じ。

### M09: 比較用ハンドル相当と削除用ハンドル相当の同時オープン (実測)

- 目的: 同じファイルを読み取り (`FILE_SHARE_READ` のみ) で開いている間に、`DELETE` アクセス付きで開けるかを記録する (SPEC §13 の未実測事項。新方式は同時に開かないため、結果は設計に影響しない)。
- 注意: .NET の `FileStream` による近似であり、製品のハンドル構成 (フラグ) と完全には同じではない。製品と同じ構成での確認が必要になった場合は Win テストのヘルパーで行う。
- シナリオ: `handles` (Windows PowerShell 5.1 で実施)
- 手順:
  1. `$f = "$env:TEMP\unextract-manual\<日時>\handles\target\sub\held.txt"`
  2. `$a = New-Object System.IO.FileStream($f, [IO.FileMode]::Open, [Security.AccessControl.FileSystemRights]::Read, [IO.FileShare]::Read, 4096, [IO.FileOptions]::None)`
  3. `try { $b = New-Object System.IO.FileStream($f, [IO.FileMode]::Open, [Security.AccessControl.FileSystemRights]'Read, Delete', [IO.FileShare]::Read, 4096, [IO.FileOptions]::None); 'opened'; $b.Dispose() } catch { $_.Exception.InnerException.Message; $_.Exception.HResult }`
  4. `$a.Dispose()`
- 記録: 手順 3 で開けたか、開けなければエラーの内容と HRESULT。(推定は共有違反で失敗。推定と異なっても合否の基準にせず、結果を `PLAN_VALIDATION.md` に記録する。)

### M10: `delete` の途中の Ctrl+C (実測)

- 目的: 逐次処理の途中で中断したとき、表示された `DELETED` の行と、実際に削除されたファイルが一致すること、未検証のファイルが削除されないことを確かめる。削除の指示の後・クローズの前に中断した場合の扱い (推定では削除が成立する) は、再現できた場合だけ記録する (SPEC §13)。
- シナリオ: `progress`
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\progress"`
  2. **`--target` のパスを確認してから** `& $exe delete "$s\archive.zip" --target "$s\target" --yes | Tee-Object -FilePath "$env:TEMP\unextract-manual\<日時>\m10.txt"` を実行し、`DELETED` の行が流れている途中で Ctrl+C を押す。
  3. `$LASTEXITCODE`
  4. `(Get-ChildItem -Recurse -File "$s\target").Count`
  5. `(Select-String -Path "$env:TEMP\unextract-manual\<日時>\m10.txt" -Pattern '^DELETED ').Count`
- 期待結果:
  - target に残ったファイルは、`DELETED` と表示されていないファイルだけである (手順 4 の件数 + 手順 5 の件数 が 500、または差が1件以内。1件の差は表示の前後の瞬間に中断した場合で、どちらの向きかを記録する)。
  - 観察 (合否の基準にしない): 終了コード、中断時に表示されたもの。中断の瞬間が削除の指示とクローズの間だったかは外から判別できないため、差が出た場合は「判別できない」と記録する。
- 確認できないこと: 削除の指示の後・クローズの前の中断を意図して起こすことはできない。この組み合わせが観測できなければ未確認のまま残す。

### M11: 書き込み可能なメモリマップ (実測、手順未確立)

- 目的: 他のプロセスがファイルを書き込み可能にマップした後でファイルハンドルだけを閉じた場合に、削除用ハンドルを開いている間に内容が書き換わり得るかを確かめる (SPEC §12、§13)。
- 状態: **手順未確立**。PowerShell だけで再現する手順を決められていない。再現方法 (Win テストのヘルパーを含む) が決まるまで、未実施・未確認として残す。成立とも不成立とも見なさない。

### M12: 祖先ディレクトリの改名と read-only の DELETE オープン (実測)

- 目的: (a) 配下のファイルを `DELETE` アクセス付き・`FILE_SHARE_READ` のみで開いている間に、祖先ディレクトリを改名できるか、(b) read-only 属性のファイルを `DELETE` アクセス付きで開けるかを記録する (SPEC §13 の未実測事項)。製品と同じ構成での実測は Win テスト S26・S37 で行い、本項は近似による手動の確認である。
- シナリオ: `handles` (Windows PowerShell 5.1 で実施)
- 手順 (a):
  1. `$d = "$env:TEMP\unextract-manual\<日時>\handles\target"`
  2. `$h = New-Object System.IO.FileStream("$d\sub\held.txt", [IO.FileMode]::Open, [Security.AccessControl.FileSystemRights]'Read, Delete', [IO.FileShare]::Read, 4096, [IO.FileOptions]::None)`
  3. 2つ目の PowerShell ウィンドウで `Rename-Item -LiteralPath "$env:TEMP\unextract-manual\<日時>\handles\target\sub" -NewName sub2` を実行し、成功したかエラーかを記録する。
  4. 1つ目のウィンドウで `$h.Dispose()`
- 手順 (b):
  1. `try { $r = New-Object System.IO.FileStream("$d\ro.txt", [IO.FileMode]::Open, [Security.AccessControl.FileSystemRights]'Read, Delete', [IO.FileShare]::Read, 4096, [IO.FileOptions]::None); 'opened'; $r.Dispose() } catch { $_.Exception.InnerException.Message; $_.Exception.HResult }`
- 記録: (a) 改名が成功したか (失敗ならエラーの内容)。(b) 開けたか (開けなければエラーの内容と HRESULT)。どちらも推定を合否の基準にせず、結果を `PLAN_VALIDATION.md` に記録する。製品の安全性はどちらの結果でも削除しない側に倒れる (改名できた場合は最終確認の最終パス不一致で STOP、read-only は事前判定またはハンドル上の判定で SKIPPED、開けなければ DELETE_FAILED)。
- 列挙の属性とハンドルの属性の一致 (SPEC §13) は Win テスト S38 で実測する (手動の手順は設けない)。

### M13: PowerShell 5.1 のリダイレクトと `--entries` (実測)

- 目的: `analyze` の出力を PowerShell で保存して entries を作ったときの文字コードと、`delete --entries` の扱い (UTF-16 なら入力エラーと案内、UTF-8 なら受理) を確かめる (SPEC §3.3、README の「`--entries`」、PLAN_TESTS L18)。
- シナリオ: `ja` (Windows PowerShell 5.1。削除を伴う実行は手順 6 の1回だけ)
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\ja"`
  2. `& $exe analyze "$s\archive.zip" --target "$s\target" > "$s\redirect.txt"`
  3. `Format-Hex -Path "$s\redirect.txt" | Select-Object -First 2` で先頭のバイトを記録する (FF FE で始まれば UTF-16LE の BOM)。
  4. `& $exe analyze "$s\archive.zip" --target "$s\target" | Out-File -Encoding utf8 "$s\utf8.txt"` と `Format-Hex -Path "$s\utf8.txt" | Select-Object -First 2`
  5. `redirect.txt` から `資料/報告書.txt` の行の Entry 部分だけを残した `entries-redirect.txt` を、文字コードを変えずに作る (例: `Get-Content` を使わず、メモ帳で開いて不要な行を消し、保存時の文字コードを変えない)。同じく `utf8.txt` から `entries-utf8.txt` を作る (`資料/報告書.txt` と `café░.txt` の2行)。
  6. `& $exe delete "$s\archive.zip" --target "$s\target" --entries "$s\entries-redirect.txt" --yes` と `$LASTEXITCODE`。続けて **`--target` のパスを確認してから** `& $exe delete "$s\archive.zip" --target "$s\target" --entries "$s\entries-utf8.txt" --yes` と `$LASTEXITCODE`
- 期待結果:
  - 手順 3・4 の先頭のバイトを事実として記録する (推定: リダイレクトは UTF-16LE の BOM、`Out-File -Encoding utf8` は UTF-8 の BOM)。
  - `entries-redirect.txt` が UTF-16 なら、手順 6 の1つ目は入力エラー (UTF-8 で保存するよう案内)、削除0件、終了コード 1。
  - `entries-utf8.txt` では、日本語名と `café░.txt` が文字化けせずに保存されていれば受理され、2件が `DELETED` になり終了コード 0。文字化けしていれば ZIP に一致するエントリが無い入力エラー (削除0件) になる。どちらだったかを記録する (PowerShell がネイティブコマンドの出力をどの文字コードで読むかは未確認であるため)。

## 6. 結果の記録

- 下の表に、項目ごとに1行記入する。複数回実施したときは行を追加する。
- 「実際の出力」は要点だけを短く書く。長い出力は貼らない。
- 判定は「合」「不合」「保留」「未実施」「記録」(実測項目で、期待値を定めず結果を記録したもの) のいずれか。
  - 保留: 実施したが、fixture や手順書の誤りで製品の挙動を判定できなかった。原因と再実施の予定を備考に書く。
- **期待と違った点は、直さずにそのまま事実として書く。** 想定外の結果が出たら、その項目の記録だけをして以後の項目を止め、Issue または報告にする (実装・文書をその場で直さない)。
- fixture や手順書の誤りで保留した場合は、その原因と無関係な項目は続行してよい。製品の想定外の挙動が出た場合は、従来どおり記録して停止する。
- 実測項目 (M09〜M13) の結果は `PLAN_VALIDATION.md` にも記録する。
- バージョンは `(Get-Item $exe).VersionInfo.ProductVersion`、OS ビルドは `[Environment]::OSVersion.Version` で確認できる。

### 6.1 新方式 (`analyze` / `delete`) の記録

| 項目 | 実施日 | unextract のバージョン | exe (publish 版 / ビルド出力) | OS ビルド | 端末 (PowerShell の版と Windows Terminal / コンソールホスト) | 実際の出力 (短く) | 終了コード | 判定 | 備考 |
|---|---|---|---|---|---|---|---|---|---|
| M01 | | | | | | | | 未実施 | |
| M02 | | | | | | | | 未実施 | |
| M03 | | | | | | | | 未実施 | |
| M04 | | | | | | | | 未実施 | |
| M05 | | | | | | | | 未実施 | |
| M06 (932) | | | | | | | | 未実施 | |
| M06 (65001) | | | | | | | | 未実施 | |
| M07 | | | | | | | | 未実施 | |
| M08 | | | | | | | | 未実施 | |
| M09 | | | | | | | | 未実施 | 実測 |
| M10 | | | | | | | | 未実施 | 実測 |
| M11 | | | | | | | | 未実施 | 手順未確立 |
| M12 (a) | | | | | | | | 未実施 | 実測 |
| M12 (b) | | | | | | | | 未実施 | 実測 |
| M13 | | | | | | | | 未実施 | 実測 |

### 6.2 旧方式の記録 (2026-10-02、旧形式の CLI。変更しない)

以下は旧形式 (`unextract <archive.zip> --target <dir> [--dry-run] [--fast] [--yes]`) と当時の手順書で実施した記録である。手順と期待結果は当時のもの (M01〜M03 は全件の結果表示の後の確認、M05 は `--dry-run` と `Deleting n / total`、M06 は `--dry-run`、M07 は確認待ち中の変更による削除直前再検証の停止)。

| 項目 | 実施日 | unextract のバージョン | exe (publish 版 / ビルド出力) | OS ビルド | 端末 (PowerShell の版と Windows Terminal / コンソールホスト) | 実際の出力 (短く) | 終了コード | 判定 | 備考 |
|---|---|---|---|---|---|---|---|---|---|
| M01 | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | `n` で中止。削除0件。target 変更なし。 | 2 | 合 | MATCHED 3、MODIFIED 1、MISSING 1、DIRECTORY 1。`unrelated.txt` は結果に含まれず。 |
| M02 | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | 空 Enter で中止。削除0件。target 変更なし。 | 2 | 合 | target の5ファイルがすべて残存。 |
| M03 | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | MATCHED 3件を削除。DELETE_FAILED 0、未処理0。 | 0 | 合 | `same1.txt`、`same2.txt`、`docs/deep.txt` を削除。`changed.txt`、`unrelated.txt` は残存。 |
| M04 | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | 確認待ちで Ctrl+C。削除0件。target 変更なし。 | -1073741510 | 合 | メッセージなしで PowerShell プロンプトへ復帰。終了コードは観察値。 |
| M05 | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | MATCHED 500。通常時は進捗表示、stderr リダイレクト時は進捗非表示。削除済み500、DELETE_FAILED 0、未処理0。 | 0 | 合 | 削除時に `Deleting 500 / 500` を確認。 |
| M06 (932) | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | MATCHED 2、MISSING 2。日本語名は正常表示。 | 0 | 保留 | fixture 生成スクリプトの名前の誤記 (`cafeé░.txt`)。製品の出力は fixture と一致し文字化けなし。スクリプト修正後に再実施。 |
| M06 (932、再実施) | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | MATCHED 2、MISSING 2。`資料/報告書.txt`、`café░.txt` を正常表示。 | 0 | 合 | fixture 修正後に再生成して実施。実行後のコードページ 932 を確認。 |
| M06 (65001) | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | MATCHED 2、MISSING 2。`資料/報告書.txt`、`café░.txt` を正常表示。 | 0 | 合 | fixture 修正後に再生成して実施。実行後のコードページ 65001 を確認。 |
| M07 | 2026-10-02 | `0.1.0+004ee0fb64904af4040471ae6995dcbc25392817` | publish 版 | `10.0.26300.0` | Windows PowerShell 5.1.26100.9444 | `f10.txt` の同一性再検証で不一致となり停止。削除済み9、DELETE_FAILED 0、未処理0。 | 1 | 合 | 確認待ち中に別 PowerShell から `f10.txt` を変更。`f01.txt`〜`f09.txt` は削除済みで、`f10.txt` のみ残存。削除済みファイルのロールバックなし。 |
| M08 (実端末の視覚確認) | | | | | | | | 未実施 | 機能部分は Fast / Strict とも E2E PTY で自動確認済み (旧方式。新方式では X28)。ここはフォント・折り返し・視認性だけを記録する。 |

## 7. 自動化する項目と、手動に残した理由

exe を別プロセスとして起動する E2E (`tests/Unextract.E2E.Tests`、[`PLAN_TESTS.md`](PLAN_TESTS.md) の §11) で、次を自動で確認する (新方式の実装後)。

| 確認内容 | 自動テスト |
|---|---|
| `analyze` で target・ZIP が変わらず、全エントリの結果行と合計を表示 | X16 |
| `delete --yes` で MATCHED に相当するファイルだけを削除 | X17 |
| 標準入力が非対話で `--yes` なしは中止 (終了コード 2) | X18、X25 |
| `analyze` の FATAL と、`delete` の途中の STOP (それ以前の削除は残る) | X19 |
| 引数のエラー (旧形式、`--dry-run` を含む) と入力のエラー | X20、X22 |
| `--entries` の正常と入力エラー | X21 |
| 日本語名・CP437 名の出力と、Entry の転記による `--entries` | X23 |
| stdout と stderr の分離、stderr のリダイレクト時に進捗が出ないこと | X24 |
| Fast の `analyze`・`delete` | X26 |
| 終了コード 0 / 1 / 2 | X27 |
| 実際の `[y/N]` → `n` → 終了コード 2・非削除、Fast の警告の順序 | X28 (PTY) |
| 逐次処理中の競合 (プロセス内のフックで注入) | `Unextract.Windows.Tests` の S21〜S29 |

手動に残した理由: X 系の E2E はリダイレクト実行で非対話であり、PTY は機能の確認に限る。実端末での操作 (M01〜M03、M07)、Ctrl+C (M04、M10)、進捗と逐次結果の見え方 (M05)、コードページとフォントによる表示 (M06)、警告の視認性 (M08)、PowerShell の挙動 (M13) は手動で確認する。M09・M11・M12 は、製品と同じ構成での実測を Win テスト (S26、S37、S38) で行い、手動は近似による補助の確認である。
