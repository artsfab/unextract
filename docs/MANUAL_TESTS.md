# unextract 手動テスト手順書 (M 系)

仕様の唯一の基準は [`SPEC.md`](SPEC.md)。本書は、自動テスト ([`PLAN_TESTS.md`](PLAN_TESTS.md)) では確認できない項目を、手順どおりに実施すれば誰でも同じ確認ができる形で定める。期待結果は原則として SPEC / PLAN_TESTS とリポジトリ直下の `README.md` に書かれた事実に従う。SPEC が具体的な文言や運用詳細を PLAN に委ねている項目については、確定済みの PLAN の記述に従う。SPEC・README に定めがなく実装の挙動にすぎない点は「観察」として分けて書き、合否の基準にしない。

> **安全上の注意 (必ず読む)**
>
> - **target には、`scripts/make_manual_fixtures.py` が `%TEMP%\unextract-manual\` の下に作った fixture だけを使う。実在のデータ (自分の文書・ダウンロード・作業フォルダーなど) を target にしない。**
> - **`y` の入力と `--yes` の実行は、fixture の削除候補 (`MATCHED`。`--fast` では `SAME_SIZE`) のファイルを実際に削除する。ごみ箱を使わない完全削除で、復旧できない。**
> - **コマンドを実行する前に、`--target` の後のパスが `%TEMP%\unextract-manual\<日時>\<シナリオ名>\target` であることを目で確認する。**

## 1. 目的と実施タイミング

- 目的: 対話的なコンソールが必要な挙動 (`[y/N]` の確認入力、Ctrl+C、進捗表示、コードページごとの名前の表示、確認待ち中の外部変更、`--fast` の `[y/N]` の直前の警告) を、実際の端末で確認する。
- 実施タイミング: 最初のリリースの前に1回。その後は、確認プロンプト・進捗表示・出力のエンコーディングに関わるコード (`src/Unextract.Cli` の `Program.cs`、`CliApplication.cs` の `ConsolePrompt`・`ProgressLine` など) を変更したときに行う。毎回のコード変更やリリースごとには不要。

## 2. 前提

- Windows 11。
- 通常の PowerShell (Windows PowerShell 5.1 または PowerShell 7) を、Windows Terminal またはコンソールホストで開く。**Git Bash、VS Code などのエディタ内のターミナル、出力をパイプ・リダイレクトする実行は使わない** (標準入力・標準エラー出力がリダイレクトされると、確認と進捗の挙動が変わるため。SPEC §2、§10)。
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
- `%TEMP%\unextract-manual\<日時>\` の下に、次のシナリオのフォルダー (それぞれ `archive.zip` と `target\`) が作られ、最後に各シナリオの項目 ID・期待・実行コマンドが表示される。

| シナリオ | 使う項目 | 内容 |
|---|---|---|
| `yn-n`、`yn-enter`、`yn-y`、`yn-ctrlc` | M01、M02、M03、M04 (`yn-n` は M08 でも使う) | ZIP: `same1.txt`、`same2.txt`、`docs/`、`docs/deep.txt`、`changed.txt`、`missing.txt`。target: `same1.txt`・`same2.txt`・`docs\deep.txt` が一致、`changed.txt` が同じサイズで内容違い、ZIP にない `unrelated.txt` |
| `progress` | M05 | 10 個のフォルダーに分けた 500 ファイル。全て一致 |
| `ja` | M06 | UTF-8 フラグ付きの日本語名 (`資料/報告書.txt`、`資料/写真一覧.csv`、`ファイル名.txt`) と `café░.txt`。target には `資料\報告書.txt` と `café░.txt` だけがある |
| `stop` | M07 | `f01.txt`〜`f10.txt`。全て一致 |
| `crc` | (参考) | E2E の X04 で自動化済み。`bad.txt` の CRC-32 だけを書き換えた ZIP |

- 項目ごとに別のシナリオを使う (1つのシナリオで削除を伴う項目を続けて行わない)。
- **やり直すときは、スクリプトをもう一度実行して新しい fixture を作る。** スクリプトは既存の fixture を削除・上書きしない (毎回新しい日時フォルダーを作る)。
- 不要になった fixture は、`%TEMP%\unextract-manual\` の下の日時フォルダーごと手で消してよい。リポジトリの規約に合わせて `Remove-Item -Recurse` は使わず、パスを確認してから次のように消す。

  ```powershell
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
  & $exe "$s\archive.zip" --target "$s\target"
  $LASTEXITCODE
  ```

- 実行後にもう一度 `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName` を実行し、実行前と比べる。

## 5. 項目

### M01: `[y/N]` に `n`

- 目的: 確認で `y`/`Y` 以外を入力すると中止すること (SPEC §2)。
- シナリオ: `yn-n`
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\yn-n"`
  2. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
  3. `& $exe "$s\archive.zip" --target "$s\target"`
  4. `[y/N]` の確認が表示されたら `n` を入力して Enter
  5. `$LASTEXITCODE`
  6. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
- 期待結果:
  - 確認の前に、各カテゴリーの全パスと件数が表示される (`MATCHED` 3: `same1.txt`、`same2.txt`、`docs/deep.txt`。`MODIFIED` 1: `changed.txt`。`MISSING` 1: `missing.txt`。`DIRECTORY` 1: `docs/`)。`unrelated.txt` は表示されない (SPEC §1、§10)。
  - 中止し、削除0件。終了コード 2 (利用者による中止。SPEC §2)。
  - 手順 6 の一覧が手順 2 と同じ。

### M02: `[y/N]` に空 Enter

- 目的: 空入力は中止になること (SPEC §2)。
- シナリオ: `yn-enter`
- 手順: M01 と同じ (シナリオ名を `yn-enter` にする)。手順 4 では何も入力せずに Enter だけを押す。
- 期待結果: M01 と同じ (中止、削除0件、終了コード 2、target は変わらない)。

### M03: `[y/N]` に `y`

- 目的: `y` の明示入力でだけ削除を始め、`MATCHED` だけが削除されること (SPEC §2、§6)。
- シナリオ: `yn-y`
- 手順: M01 と同じ (シナリオ名を `yn-y` にする)。手順 4 で `y` を入力して Enter。**実行前に `--target` のパスを確認する。**
- 期待結果:
  - `same1.txt`、`same2.txt`、`docs\deep.txt` だけが削除される。`changed.txt`、`unrelated.txt`、`docs\` フォルダー、`archive.zip` は残る (SPEC §1、§2)。
  - 削除済み 3、`DELETE_FAILED` 0、未処理 0 の要約が表示される (SPEC §10)。
  - 終了コード 0 (SPEC §2)。

### M04: `[y/N]` で Ctrl+C

- 目的: 確認待ちでの中断で、何も削除されないこと (SPEC §10「Ctrl+C などの中断で未検証のファイルを削除しないことは守る」)。
- シナリオ: `yn-ctrlc`
- 手順: M01 と同じ (シナリオ名を `yn-ctrlc` にする)。手順 4 で何も入力せずに Ctrl+C を押す。
- 期待結果:
  - target の一覧が実行前と同じ (削除0件)。
  - 終了コードは SPEC に定めがない (Ctrl+C 専用の後処理は MVP に含めない。SPEC §10)。**`$LASTEXITCODE` の値を事実として記録する** (合否の基準にしない)。
  - 観察: 表示されたメッセージ (あれば) と、終了後のプロンプトの状態を記録する。

### M05: 進捗表示

- 目的: 解析中・削除中の進捗が標準エラー出力に表示され、標準エラー出力をリダイレクトすると表示されないこと (SPEC §10)。
- シナリオ: `progress` (500 エントリ)。M05 では削除を伴う実行を1回だけ行う。
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\progress"`
  2. `& $exe "$s\archive.zip" --target "$s\target" --dry-run`
  3. `$LASTEXITCODE`
  4. `& $exe "$s\archive.zip" --target "$s\target" --dry-run 2> $null`
  5. `$LASTEXITCODE`
  6. **`--target` のパスを確認してから** `& $exe "$s\archive.zip" --target "$s\target" --yes`
  7. `$LASTEXITCODE`
- 期待結果:
  - 手順 2: `Checking n / total` の形の進捗が表示され、結果一覧 (`MATCHED` 500) が続く。終了コード 0。
  - 手順 4: 進捗は表示されない (標準エラー出力がリダイレクトされているため。SPEC §10)。結果一覧は表示される。終了コード 0。
  - 手順 6: `Checking n / total` と `Deleting n / total` の進捗が表示され、削除済み 500、`DELETE_FAILED` 0、未処理 0 の要約が表示される。終了コード 0。
  - 観察 (合否の基準にしない): 進捗が1行の上書きで更新されるか、改行が増えていくか。進捗の行と結果一覧が混ざって読みにくくないか。
- 確認できないこと: 500 件では進捗の更新が速く、途中の値が目で追えないことがある。その場合は「最後の値だけ見えた」などと事実を記録する。

### M06: 日本語名の表示 (コードページ 932 / 65001)

- 目的: 日本語名と CP437 由来の文字を含む名前が、コンソールのコードページによらず表示されること (SPEC §4.1、§10)。
- シナリオ: `ja` (`--dry-run` だけを使い、削除しない)
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\ja"`
  2. `chcp` を実行し、表示された元のコードページ (例: 932) を記録する。
  3. `chcp 932`
  4. `& $exe "$s\archive.zip" --target "$s\target" --dry-run`
  5. `$LASTEXITCODE` と `chcp` を実行して記録する。
  6. `chcp 65001`
  7. 手順 4・5 をもう一度行う。
  8. `chcp <手順 2 で記録した値>` で元のコードページに戻す。
- 期待結果:
  - 手順 4・7 とも、`資料/報告書.txt`・`café░.txt` が `MATCHED`、`資料/写真一覧.csv`・`ファイル名.txt` が `MISSING` として、名前がそのまま (文字化けせず、`\u{...}` のエスケープにならずに) 表示される。終了コード 0。
  - CP437 由来の名前は `café░.txt` (c・a・f・é・░ の5文字 + `.txt`) と表示される。`é` の前に ASCII の `e` が余っていない (`cafeé░.txt` ではない) ことを見る。
  - 観察 (合否の基準にしない): 手順 5 の `chcp` の値 (unextract の実行後にコードページが実行前の値のままか)。
- フォントによる表示: 文字が `□` (豆腐) で表示されるのは、端末のフォントにその文字がないためで、unextract の出力の問題ではない。この場合は「フォント由来の □」と備考に別記し、文字化け (別の文字や `?` に置き換わる) と区別する。区別がつかなければ、見えたままを事実として記録する。

### M07: 確認待ち中の外部変更による停止

- 目的: 確認待ちの間に `MATCHED` のファイルが変更されると、削除直前再検証で検出してそのファイルを削除せず、以後の削除を停止すること。それまでに削除したファイルは戻らないこと (SPEC §8.3、§8.4、§10、§12)。
- シナリオ: `stop`
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\stop"`
  2. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
  3. **`--target` のパスを確認してから** `& $exe "$s\archive.zip" --target "$s\target"`
  4. `[y/N]` の確認が表示されたら、まだ何も入力しない。
  5. 2つ目の PowerShell ウィンドウを開き、`Add-Content -Path "$env:TEMP\unextract-manual\<日時>\stop\target\f10.txt" -Value "changed"` を実行する。
  6. 1つ目のウィンドウに戻り、`y` を入力して Enter。
  7. `$LASTEXITCODE`
  8. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
- 期待結果:
  - `f10.txt` は削除されず残る。停止の原因として `f10.txt` のパスと理由が表示される (標準エラー出力。SPEC §10)。
  - 削除済み・`DELETE_FAILED`・未処理の件数が表示される (SPEC §10)。
  - 停止の前に削除されたファイルは戻らない (SPEC §8.4)。実装は `MATCHED` を ZIP の順に処理するため、`f01.txt`〜`f09.txt` が削除済み、`f10.txt` だけが残る想定である (想定と違えば事実を記録する)。
  - 終了コード 1 (削除フェーズの停止はエラー。SPEC §2)。

### M08: `--fast` の確認プロンプトの直前の警告

- 目的: `--fast` の対話的な通常実行で、`[y/N]` の直前に Fast の警告が表示され、Strict では表示されないこと (SPEC §10、§15.6、`PLAN.md` §4 の「Fast モード」、`PLAN_TESTS.md` O07)。
- 機能部分は E2E PTY (`PtyConfirmationTests.M08_O07_InteractiveWarningAndCancelWithN`) で自動確認済み。`yn-n` 相当の fixture を一時生成し、Fast / Strict の実際の `[y/N]` 表示後に `n` を送信、終了コード 2・target 非削除を確認する。Fast は PLAN 指定の警告がヘッダーと確認直前の計2回出て、最後の警告から確認文・`[y/N]` までに別出力が無いこと、Strict は警告が無いことを確認する。fixture は PTY / process tree の終了・Dispose 完了後に自動 cleanup する。実績は `PLAN_VALIDATION.md` の「Fast / PTY / publish E2E の最終確認」。
- 以下の手動手順は実端末のフォント・折り返し・視認性の確認用として残す。機能確認の自動化とは分けて記録する。
- シナリオ: `yn-n` (どちらの実行も `n` で中止し、削除しない)。M01 の後に続けて使う場合は、M01 で target が変わっていないことを確かめてから使う。スクリプトの表示 (項目 ID・期待・実行コマンド) には M08 は出ないため、コマンドは下の手順のものを使う。
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\yn-n"`
  2. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
  3. `& $exe "$s\archive.zip" --target "$s\target" --fast`
  4. `[y/N]` の確認が表示されたら、その直前の行を記録してから `n` を入力して Enter
  5. `$LASTEXITCODE`
  6. `& $exe "$s\archive.zip" --target "$s\target"` (Strict)
  7. `[y/N]` の確認が表示されたら、その直前の行を記録してから `n` を入力して Enter
  8. `$LASTEXITCODE`
  9. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
- 期待結果:
  - 手順 3: 解析結果の一覧の先頭行に Fast の警告 (`PLAN.md` §4 の「Fast モード」の文言) が表示される。一覧は `SAME_SIZE` 4 (`same1.txt`、`same2.txt`、`docs/deep.txt`、`changed.txt`)、`MISSING` 1、`DIRECTORY` 1 で、`MATCHED` は表示されない (`changed.txt` は同じサイズで内容違いのため、Fast では `SAME_SIZE`。SPEC §15.3)。
  - 手順 4: `[y/N]` の確認の直前の行が、同じ Fast の警告の文言である。警告は確認プロンプトに続けて同じ画面に表示される。
  - 手順 5・8: 中止し、削除0件。終了コード 2 (SPEC §2)。
  - 手順 6・7: Fast の警告は、一覧の先頭にも `[y/N]` の直前にも表示されない。一覧は M01 と同じ (`MATCHED` 3、`MODIFIED` 1、`MISSING` 1、`DIRECTORY` 1。`SAME_SIZE` は表示されない)。
  - 手順 9 の一覧が手順 2 と同じ。
- 確認範囲の分担: 警告が確認プロンプトと同じ出力経路 (`IConfirmationPrompt.Ask` に渡す文字列) で出ていることは O07 の Core テスト、実際の表示順序と Strict の警告なしは O07 の PTY テストで確認する。PTY は stdout / stderr が同一端末に流れるため、ヘッダー警告の stdout 所属は O06 が担当する。`--fast --yes`、非対話 (`--yes` なし)、偽プロンプトの経路も既存 Core テストが担当する。手動では実端末上の警告と確認プロンプトの読みやすさ・折り返しを見る。

## 6. 結果の記録

- 下の表に、項目ごとに1行記入する。複数回実施したときは行を追加する。
- 「実際の出力」は要点だけを短く書く (例: 「中止しました。削除0件。」、停止の行の要旨)。長い出力は貼らない。
- 判定は「合」「不合」「保留」「未実施」のいずれか。
  - 保留: 実施したが、fixture や手順書の誤りで製品の挙動を判定できなかった。原因と再実施の予定を備考に書く。
- **期待と違った点は、直さずにそのまま事実として書く。** 想定外の結果が出たら、その項目の記録だけをして以後の項目を止め、Issue または報告にする (実装・文書をその場で直さない)。
- fixture や手順書の誤りで保留した場合は、その原因と無関係な項目は続行してよい。製品の想定外の挙動が出た場合は、従来どおり記録して停止する。
- バージョンは `(Get-Item $exe).VersionInfo.ProductVersion`、OS ビルドは `[Environment]::OSVersion.Version` で確認できる。

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
| M08 (実端末の視覚確認) | | | | | | | | 未実施 | 機能部分は Fast / Strict とも E2E PTY で自動確認済み。ここはフォント・折り返し・視認性だけを記録する。 |

## 7. 自動化済みの項目と、手動に残した理由

exe を別プロセスとして起動する E2E (`tests/Unextract.E2E.Tests`、[`PLAN_TESTS.md`](PLAN_TESTS.md) の「E2E (X 系)」) で、次を自動で確認している。

| 確認内容 | 自動テスト |
|---|---|
| `--dry-run` で target・ZIP が変わらず、全カテゴリーの全パスと件数を表示 | X01 |
| `--yes` で `MATCHED` だけを削除 | X02 |
| 標準入力が非対話 (空・`n`・`y` をパイプで渡す) で `--yes` なしは中止 (終了コード 2) | X03 |
| 削除開始前の FATAL で削除0件 | X04 |
| 引数・入力のエラー | X05、X06 |
| 削除候補0件・空 ZIP はプロンプトなしで成功 | X07 |
| 日本語名 (UTF-8 フラグ付き) の出力 (UTF-8 で受けた場合) と削除 | X08 |
| UTF-8 フラグなしの CP437 名の照合 (publish 版の単一ファイル exe を含む) | X09 |
| stdout と stderr の分離、stderr のリダイレクト時に進捗が出ないこと | X10 |
| `--dry-run` と `--yes` の解析結果の一致 | X11 |
| 終了コード 0 / 1 / 2 | X12 |
| 確認待ち中の外部変更による停止 (プロセス内のフックで注入) | `Unextract.Windows.Tests` の D04 など |
| `--fast` の `--dry-run`・`--yes`・事前 FATAL・削除候補0件での分類と、解析結果の一覧の先頭の警告 | X13、X14、X15 |
| 実際の `[y/N]` → `n` → 終了コード 2・非削除、Fast の警告2回と確認直前の順序、Strict の警告なし | M08 / O07 (E2E PTY) |

手動に残した理由: X 系の E2E はリダイレクト実行で非対話だが、M08 / O07 の機能部分は Windows PTY で自動化済み。実端末での操作 (M01〜M03、M07)、Ctrl+C (M04)、進捗の見え方 (M05)、コードページとフォントによる表示 (M06)、M08 のフォント・折り返し・視認性は手動で確認する。PTY の `y` ケースは追加していない。M07 の検出ロジック自体は D04 で自動化しているが、実際の確認待ちの間に別のウィンドウから変更する流れは手動で確認する。
