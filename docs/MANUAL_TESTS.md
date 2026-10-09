# 手動テスト手順

役割: 手動に残した M05・M08・M14・M15 のfixture・実端末操作・観測方法 (M05・M08は端末での見え方、M14はGUI、M15はRARのDLLの導入と実物のRAR)。番号は振り直さず、自動化・削除した項目は欠番にする ([手動に残す理由](#manual-purpose))。該当項目を実施するときに読む。実施状態は[OPEN_ISSUES](OPEN_ISSUES.md#manual-status)だけに記録する。

[前提](#prerequisites) / [fixture](#fixtures) / [項目](#cases) / [記録](#recording) / [手動に残す理由](#manual-purpose)

> **安全上の注意 (必ず読む)**
>
> - **target には、`scripts/make_manual_fixtures.py` が `%TEMP%\unextract-manual\` の下に作った fixture だけを使う。実在のデータ (自分の文書・ダウンロード・作業フォルダーなど) を target にしない。**
> - **`delete` で `y` を入力すること、`delete --yes` を実行することは、fixture のファイルを実際に削除する。ごみ箱を使わない完全削除で、復旧できない。**
> - **コマンドを実行する前に、`--target` の後のパスが `%TEMP%\unextract-manual\<日時>\<シナリオ名>\target` であることを目で確認する。**

## 1. 目的と実施タイミング

- 目的: 自動テストでは判定できない、実際の端末での見え方 (進捗と逐次結果の表示 (M05)、`--fast` の警告の視認性 (M08)) と、GUI の使い勝手 (M14)、利用者による UnRAR.dll の導入と WinRAR の実物 (M15) を人間が確認する。
- 実施タイミング: 初回に確認する。その後は、進捗表示・警告の表示に関わるコード (`src/Unextract.Cli` の `CliApplication.cs` の `ProgressLine` など、[Fast警告](spec/cli.md#warning)) を変更したときに M05・M08 を行う。M14・M15 の実施タイミングは各項目に書く。

<a id="prerequisites"></a>
## 2. 前提

- Windows 11。
- 通常の PowerShell (Windows PowerShell 5.1 または PowerShell 7) を、Windows Terminal またはコンソールホストで開く。**Git Bash、VS Code などのエディタ内のターミナル、出力をパイプ・リダイレクトする実行は使わない** (標準入力・標準エラー出力がリダイレクトされると、確認と進捗の挙動が変わるため。[引数と終了状態](spec/cli.md#arguments)、[出力先と進捗](spec/cli.md#streams))。M05 の手順4だけは、手順どおりに標準エラー出力をリダイレクトする。
- 管理者権限は不要 (管理者として開かない)。
- exe は、publish した単一ファイル版を推奨する (README の「配布ビルド」)。出力先はリポジトリの外にする。例:

  ```powershell
  dotnet publish src/Unextract.Cli -p:PublishProfile=win-x64 -o "$env:TEMP\unextract-publish"
  $exe = "$env:TEMP\unextract-publish\unextract.exe"
  ```

  ビルド出力の `src\Unextract.Cli\bin\<構成>\net10.0-windows\unextract.exe` を使ってもよい。どちらを使ったかを結果に書く。
- Python ランチャー `py` (fixture の作成に使う)。

<a id="fixtures"></a>
## 3. fixture の作り方

リポジトリ直下で次を実行する。exe のパスは表示されるコマンドに埋め込むためだけに使い、スクリプトは exe を実行しない。

```powershell
py scripts\make_manual_fixtures.py "$exe"
```

- 表示が文字化けする、または `UnicodeEncodeError` で止まる場合は、先に `$env:PYTHONIOENCODING = 'utf-8'` を設定してから実行する。
- `%TEMP%\unextract-manual\<日時>\` の下に、次のシナリオのフォルダー (それぞれ `archive.zip` と `target\`) が作られ、最後に各シナリオの項目 ID・期待・実行コマンドが表示される。

| シナリオ | 使う項目 | 内容 |
|---|---|---|
| `yn-n`、`yn-y` | M08 (`yn-n`)、M14 (`yn-y` は Strict の削除、`yn-n` は Fast) | ZIP: `same1.txt`、`same2.txt`、`docs/`、`docs/deep.txt`、`changed.txt`、`missing.txt`。target: `same1.txt`・`same2.txt`・`docs\deep.txt` が一致、`changed.txt` が同じサイズで内容違い、ZIP にない `unrelated.txt` |
| `progress` | M05 | 10 個のフォルダーに分けた 500 ファイル。全て一致 |

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
  & $exe analyze "$s\archive.zip" --target "$s\target"
  & $exe delete "$s\archive.zip" --target "$s\target"
  $LASTEXITCODE
  ```

- 実行後にもう一度 `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName` を実行し、実行前と比べる。

<a id="cases"></a>
## 5. 項目

<a id="m05"></a>
### M05: 進捗と逐次結果の表示

- 目的: `analyze` の `Checking n / total` と `delete` の `Processing n / total` が標準エラー出力に表示され、標準エラー出力をリダイレクトすると表示されないこと。`delete` で結果行と進捗が混ざって読めなくならないこと ([出力先と進捗](spec/cli.md#streams))。
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
  - 手順 6: `Processing n / total` の進捗と、`DELETED` の結果行 500 行が表示され、`要約: 削除済み 500、...、DELETE_FAILED 0、処理対象外 0、未処理 0` が表示される。**結果行が進捗の表示の途中に続いて崩れることがない。** 終了コード 0。
  - 観察 (合否の基準にしない): 進捗が1行の上書きで更新されるか。結果行の流れの中で進捗がどう見えるか。
- 確認できないこと: 500 件では表示が速く、途中の値が目で追えないことがある。その場合は「最後の値だけ見えた」などと事実を記録する。

<a id="m08"></a>
### M08: `--fast` の警告と確認プロンプト

- 目的: `--fast` の `analyze` のヘッダー、`delete` のヘッダー、`delete` の `[y/N]` の直前に Fast の警告が表示され、Strict では表示されないこと ([出力先と進捗](spec/cli.md#streams)、[Fast警告](spec/cli.md#warning))。
- 機能部分 (警告の位置と回数、`n` での中止) は E2E PTY ([TESTING](TESTING.md#e2e) X28) で自動確認する。以下の手動手順は、実端末のフォント・折り返し・視認性の確認用として残す。
- シナリオ: `yn-n` (どの実行も `n` で中止し、削除しない)。
- 手順:
  1. `$s = "$env:TEMP\unextract-manual\<日時>\yn-n"`
  2. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
  3. `& $exe analyze "$s\archive.zip" --target "$s\target" --fast`
  4. `& $exe delete "$s\archive.zip" --target "$s\target" --fast`
  5. `[y/N]` の確認が表示されたら、その直前の行を記録してから `n` を入力して Enter。`$LASTEXITCODE`
  6. `& $exe delete "$s\archive.zip" --target "$s\target"` (Strict)。`[y/N]` の直前の行を記録して `n`。`$LASTEXITCODE`
  7. `Get-ChildItem -Recurse -File "$s\target" | ForEach-Object FullName`
- 期待結果:
  - 手順 3: ヘッダーの先頭行が Fast の警告 ([Fast警告](spec/cli.md#warning) の文言)。結果は `SAME_SIZE` 4 (`same1.txt`、`same2.txt`、`docs/deep.txt`、`changed.txt`)、`MISSING` 1、`DIRECTORY` 1 で、`MATCHED` は表示されない。
  - 手順 4・5: ヘッダーの先頭行と、`[y/N]` の確認文の直前の行が、同じ Fast の警告。中止し、削除0件、終了コード 2。
  - 手順 6: 警告はどこにも表示されない。中止し、終了コード 2。
  - 手順 7 の一覧が手順 2 と同じ。

<a id="m14"></a>
### M14: GUI の受入確認 (実端末)

- 目的: 実装者による[画面確認](TESTING.md#gui-review)と UI E2E の後に、人間が実際の画面で GUI の使い勝手を最終確認する ([利用品質](spec/gui.md#quality))。UI E2E と論理寸法の写真では代替できない、実際の表示拡大率・モニター間の移動・エクスプローラーと OS のフォルダー選択ダイアログ・全体の操作感を扱う。問題の洗い出しを人間に委ねる工程ではない。
- 実施タイミング: GUI の画面・文言・寸法・操作、または配布処理を変えたとき、影響する手順を行う。
- 前提: 本項目は CLI の端末操作ではないので §2 の端末の前提は適用しない。配布物は `scripts\publish-gui.ps1 -OutputDirectory <リポジトリ外の新規出力先>` で作る (README の「配布ビルド」)。拡大率の違う2台のモニターがあれば手順2の移動も行う。
- シナリオ: このために §3 のスクリプトを新しく実行し (exe には配布物の `cli\unextract.exe` を渡してよい)、その日時フォルダーの `yn-y` (Strict の削除) と `yn-n` (Fast) を使う。検索ディレクトリには各シナリオのフォルダー (`archive.zip` と `target\` を含む) を選び、Target はカスタムの `{{archive.dir}}\target` で追加する。実在のデータを使わない。
- 手順:
  1. 配布物の `unextract-gui.exe` を起動し、構成の説明を読まずに基本の流れを通す: `yn-y` を検索 → Target 追加 → Strict 解析 → 結果の確認 (分類・絞り込み) → 選択 → 削除確認 (**Target の実パスが fixture であることを目で確認してから承認**) → 完了結果の確認。続けて `yn-n` で Fast に切り替え、警告・モード変更の確認・削除確認での Fast の再表示を確かめる (削除するかは任意。削除すると同じサイズで内容が違う `changed.txt` も消える)。全体として実用に耐える使い勝手かを判断する。
  2. 表示拡大率 100% / 150% / 200% (設定の「ディスプレイ」) のそれぞれと、拡大率の違うモニター間のウィンドウ移動で、文字欠け・重なり・ボタンの切れが無いこと。
  3. Tab・Shift+Tab でフォーカス枠が見えること、Fast 警告と削除確認の警告が十分に目立つこと、スクロールの操作感。
  4. 「実行ログの保存フォルダーを開く」でエクスプローラーが実際に開くこと。「フォルダーを選択」で OS の選択ダイアログからフォルダーを実際に選べ、検索欄に反映されること。
- 期待結果: 各手順で指摘が無いこと。`yn-y` では一致した `same1.txt`・`same2.txt`・`docs\deep.txt` だけが消え、`changed.txt`・`unrelated.txt` が残り、`%LOCALAPPDATA%\unextract\logs\` に命名どおりのログができる。指摘が出たら画面確認へ戻って直し、影響する手順を再確認する。
- 記録: 手順ごとに実施・未実施・前提不成立・失敗を分けて [OPEN_ISSUES](OPEN_ISSUES.md#manual-status) に記録する。合格は実データ・実 GPU での性能、実 CLI 出力を受信している間の長時間の体感、既存の安全性の未確認事項を保証しない。

<a id="m15"></a>
### M15: UnRAR.dll の導入と WinRAR で作った RAR (実端末)

- 目的: 利用者向けの [UnRAR.dll の導入手順](../README.md#rar-dll) がそのとおりに実行でき、DLL の有無で RAR だけが変わること、WinRAR で作って WinRAR で展開した実際のフォルダーで RAR の analyze/delete が期待どおりになることを人間が確かめる。自動試験の RAR は生成器 (Stored) で、WinRAR の実物の受理・拒否・内容検証はローカル検証 ([実物のRAR](TESTING.md#rar-real)) で確かめるので、ここでは利用者の導入手順と実際の操作を確かめる。
- 実施タイミング: README の導入手順、DLL の版、RAR の配置・読み込み元を変えたとき。
- 前提: WinRAR (試用期間内を含め、そのライセンス条件の範囲内。RAR4 は対応する旧版) を使う。作った RAR はローカル検証専用で、リポジトリに収録しない ([実物のRAR](TESTING.md#rar-real))。WinRAR が使えない環境では行わず「前提不成立」と記録する。CLI は `dotnet publish` (README の「配布ビルド」) のリポジトリ外の出力、GUI は `publish-gui.ps1` の配布物を使う。fixture は自作データだけで作り、実在のデータを使わない。
- 手順:
  1. DLL を置かずに、自作フォルダー (例: `src\a.txt`、`src\sub\日本語.txt`、`src\b.bin` (数 MB)) を WinRAR で RAR5 (既定の圧縮) にした `archive.rar` と、同じフォルダーを WinRAR で `target\` に展開したものに対して `unextract analyze archive.rar --target target` を実行する。FATAL の説明に「見つかりません」・読み込み元の絶対パス (exe と同じフォルダーの `UnRAR64.dll`)・必要な版・ZIP に影響しないことが出て、終了コード 1 であること。同じ exe で ZIP の analyze が成功すること。
  2. README の手順どおりに `unrardll-723.exe` から `x64\UnRAR64.dll` を取り出し、SHA-256 を確かめて exe と同じフォルダーに置く。手順1の analyze が全件 `MATCHED`・`DIRECTORY` になること。`target\a.txt` の内容を変えて `MODIFIED` になること。
  3. 手順2の後、`delete` で一致したファイルだけが消え、変えたファイル・フォルダーが残ること (**target の実パスが fixture であることを目で確かめてから承認**)。
  4. GUI の配布物の `cli\` に同じ DLL を置き、手順1と同じ作り方の新しい fixture を GUI で検索 (`.rar` が見つかること)・解析・削除する。`cli\UnRAR64.dll` を外すと、解析の失敗の詳細に CLI の説明が出ること。
  5. (RAR4) 対応する旧版の WinRAR で RAR4 を作り、手順2と同じ analyze をする。
- 期待結果: 各手順の記載どおり。DLL を置く前後で ZIP の動作が変わらない。
- 記録: 手順ごとに実施・未実施・前提不成立・失敗を分けて [OPEN_ISSUES](OPEN_ISSUES.md#manual-status) に記録する。使った WinRAR の版とライセンスの状態も残す。


<a id="recording"></a>
## 結果の記録方法

実施状態は[OPEN_ISSUES](OPEN_ISSUES.md#manual-status)の対応行を更新する。結果の表をここへ複製せず、長い出力・完了履歴を積まない。項目 (M15は手順ごと)、実施日、製品version、exeがpublishか通常buildか、OS build、PowerShell版と端末、短い出力/観測、終了コード、判定、前提と次の確認を記す。versionは `(Get-Item $exe).VersionInfo.ProductVersion`、OSは `[Environment]::OSVersion.Version` で確認する。

観測範囲が変わった場合だけ、[限定付き観測](OPEN_ISSUES.md#observations)の該当行も更新する。

判定は合/不合/保留/未実施/記録 (期待を確定しない実測)を区別する。保留はfixture/手順の誤りで製品を判定できない状態。期待と異なる事実はその場で直さず記録し、製品の想定外なら以後を止めて報告する。fixture/手順にだけ原因がある保留なら無関係項目は続行できる。前提不成立を合格と書かない。
<a id="manual-purpose"></a>
## 自動化と手動に残す理由

対話的なコンソールが必要な機能は、exe を別プロセスで起動する E2E と、実端末 (ConPTY) の E2E PTY ([TESTINGのE2E](TESTING.md#e2e)) で毎回自動で確かめる。欠番の項目の置き換え先は次のとおり。

| 旧項目 | 置き換え先 |
|---|---|
| M01 `[y/N]` に `n`、M02 空 Enter、M03 `y` | X28 (`n`、Strict/Fast)、X29 (Enter・`y`、Strict) |
| M04 確認待ちの Ctrl+C | Core の P10 (回答が null (EOF) なら中止)。確認待ちの Ctrl+C は、プロセスの終了か回答 null の中止になり、どちらでも削除は始まらない |
| M06 コードページ 932 / 65001 の名前の表示 | X30 (名前が変換されずに表示され、実行後にコードページが戻る) |
| M07 確認待ち中の外部変更 | 実 NTFS の S03 (確認の回答の直前に追記したファイルが MODIFIED で残る) |
| M10 `delete` の途中の中断 | Core の結果行の順序と失敗注入、J07・J13、最終確認のテスト (S25 など)、X32 (強制終了。[中断時の表示と実削除](spec/cli.md#interruption)) |
| M12 (a) 祖先の改名・(b) read-only の DELETE オープン | 製品と同じハンドル構成の S26 (親・祖父母・その上)・S37 |
| M13 / L18 PowerShell 5.1 の保存形式と `--entries` | X31 (実測して記録し、安全側の性質を判定) |
| M09 同時オープン、M11 書き込み可能なマップ | 受入から外した。M09 は製品が同時に開かないので設計に影響しない。M11 は[既知の限界](spec/filesystem.md#limitations)の記述を残し、未確認のまま扱う |

手動に残すのは、自動テストでは判定できないものだけである。M05: ConPTY が出すのは端末が描き直した差分で、「進捗の行と結果行が崩れずに読める」の判定には端末のモデルが要る (機能 (stderr への出力、リダイレクト時に出さないこと、結果行の前に進捗の行を消すこと) は X24 と CLI のテスト)。M08: 実端末のフォント・折り返し・視認性。M14: 実際の表示拡大率・モニター間の移動・エクスプローラーと OS ダイアログでの実操作・全体の使い勝手 (UI E2E は OS の表示拡大率を変えず、写真の評価も論理寸法で行う)。M15: 利用者による DLL の導入手順と、WinRAR で作って展開した実際のフォルダーでの動作 (自動試験は生成器 (Stored) の RAR、WinRAR の実物はスクリプトによる[ローカル検証](TESTING.md#rar-real))。
