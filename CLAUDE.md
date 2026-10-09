# CLAUDE.md

本書は作業の参照先とルールだけを示す。仕様・計画の内容はここに複製しないので、作業に関係する参照先を読む。強制力のある制限 (コミット・`dotnet run` の禁止など) は `.claude/settings.local.json` の権限設定が担う。

## 1. プロジェクト概要

- unextract は、アーカイブ (ZIP・RAR) 内のファイルに対応する target 内のファイルだけを削除する Windows 11 用 CLI。RAR は単一・非 Solid の RAR4/RAR5 だけで、UnRAR.dll で読む ([RAR仕様](docs/spec/rar.md))。モードは2つ ([モード契約](docs/SPEC.md#modes))。
  - Strict (既定): アーカイブの内容と全バイト一致したファイル (`MATCHED`) だけを削除する。全バイト一致が削除の根拠。
  - Fast (`--fast` による明示的な opt-in): パスとサイズの一致 (`SAME_SIZE`) を削除候補とし、内容は読まない・比較しない。内容の一致は保証しない。
- ファイルシステムの安全性 (パス検証、実名解決、reparse・hardlink・ADS、File ID・最終パスによる同一性、TOCTOU 対策、削除方式) は両モード共通。Fast を理由に弱めない。
- 「そのアーカイブから展開された」という来歴は証明しない。不明・判定不能なら削除しない (両モード)。
- 同梱の GUI (`unextract-gui.exe`、WPF) はアーカイブ検索と複数 Target の順次実行を担うラッパー。CLI を子プロセスとして起動して機械可読出力を使い、一致判定・削除の安全性を実装しない ([GUI仕様](docs/spec/gui.md))。

## 2. 文書の優先順位

- 規範本文は [SPEC](docs/SPEC.md) と [CLI](docs/spec/cli.md)・[ZIP](docs/spec/zip.md)・[RAR](docs/spec/rar.md)・[ファイル安全性](docs/spec/filesystem.md)・[機械可読出力](docs/spec/machine-output.md)、GUI を変えるときは [GUI](docs/spec/gui.md)。[文書入口](docs/README.md)から担当節とテストへ直行する。
- 実装の地図は [ARCHITECTURE](docs/ARCHITECTURE.md)、理由は [RATIONALE](docs/RATIONALE.md)、検証は [TESTING](docs/TESTING.md)、未確認・手動実施状態は [OPEN_ISSUES](docs/OPEN_ISSUES.md)。利用者向け要約はルートREADMEで、仕様の正本ではない。
- 食い違いや不足を見つけたら明示された規定・決定を調べ、製品判断が必要な意味変更は行わず報告する。
- 文書は担当正本を更新し、同じ規則を複製しない。文書の新設・分割・統合・廃止、責務変更、情報の配置・保存判断を行う場合は [文書メンテナンス原則](docs/DOCUMENTATION.md)を読む。通常の仕様反映・誤字・既存リンク修正だけなら毎回の通読は不要。

## 3. 絶対に守る安全規則

- 削除は、照合・検査 (Strict では全バイト比較も) と最終確認をした同じ削除用ハンドルへの `SetFileInformationByHandle(FileDispositionInfoEx)` の1か所だけ ([削除順序](docs/spec/filesystem.md#delete-flow))。`analyze` は削除用ハンドルを開かない。
- flags は 0x3 (`DELETE | POSIX_SEMANTICS`)。`IGNORE_READONLY_ATTRIBUTE` は使わない。
- パスベースの削除・改名 API (`File.Delete`、`DeleteFile`、`Directory.Delete`、`RemoveDirectory`、`MoveFile*` など) を `src/` に書かない。
  - 唯一の例外: GUI が自分で一意な名前で新規作成した一時 `--entries` ファイルを、CLI プロセスの終了後 (起動失敗の場合はその判断後) に GUI が `File.Delete` で削除する1か所。target 内のファイル、利用者のファイル、ディレクトリ、それ以外の一時ファイルには使わず、Core・Windows・Cli には書かない。
- 不明・判定不能は削除しない側 (FATAL または停止) に倒す。未知のエラーを推測で続行しない ([失敗の境界](docs/spec/filesystem.md#failure-boundary))。
- 独自の ZIP・RAR 構造パーサ、reflection、ntdll の未文書 API を使わない。ZIP の読み取りは `ZipArchive` のみ。RAR の読み取りは版と SHA-256 を固定した UnRAR.dll のみで、独自に読むのは RAR の先頭署名だけ ([形式の判定](docs/spec/rar.md#format))。
- UnRAR.dll には `RAR_TEST` と `RAR_SKIP` だけを使い、`RAR_EXTRACT` と展開先の指定を使わない (DLL にディスクへ書かせない)。DLL は照合したファイルを固定の読み込み元から絶対パスでロードし、DLL の検索順序に頼らない。DLL を利用できなければ RAR の実行だけを Prepare の FATAL (削除0件) にし、ZIP の実行では DLL を読み込まない ([版の固定](docs/spec/rar.md#pinning))。
- `Unextract.Core` は Win32 にも `Unextract.Windows` にも UnRAR.dll にも依存しない (UnRAR.dll の P/Invoke・照合・ロードは Core に置かない)。GUI は UnRAR.dll を読み込まない。
- Fast は既存の `Unextract.Core`・`Unextract.Windows`・`Unextract.Cli` の中のモード分岐として実装する。Fast 用の層、専用の実装クラス・抽象化、追加の状態管理、Fast 用の Win32 API やハンドル構成を設けない ([実装配置](docs/ARCHITECTURE.md#shared-path))。RAR でも同じで、名前・構造・上限・内容検証とファイル安全性は ZIP と同じ経路を通し、RAR を理由に target 側の安全性を弱めない。

## 4. 開発規則

- 警告ゼロ (`Directory.Build.props` の `TreatWarningsAsErrors`)。
- `NoWarn`、`#pragma warning disable`、`TreatWarningsAsErrors` の解除をしない。
- テストの Skip、期待値の緩和をしない。テストが失敗したら、まず実装を疑う。UnRAR.dll が無い・照合できない環境で RAR のテストを前提不成立や成功にしない。
- 不可視文字・双方向制御文字 (C0の改行・CR・タブを除く制御文字/DEL、C1制御、Unicode Cf、U+2028/U+2029。双方向制御のU+202A〜U+202E・U+2066〜U+2069などを含む) を `src/` `tests/` `docs/` `scripts/` `.github/` に実際の文字として入れない。必要なら `\u` エスケープで書く。

## 5. 作業規則

- コミット・プッシュ・`git checkout` はしない (人間が行う)。
- `LICENSE` は MIT。内容は変更しない。
- 実在のユーザーデータを target にしない。
- 削除してよいのは、テスト・検証が自作した一意な fixture と、承認を得た後始末だけ。
- テストが作った fixture は、共通の削除処理 (所有の確認、reparse point をたどらない) で、テストの終了後に成功・失敗を問わず削除する。`scripts/clean-test-fixtures.ps1` は、削除できなかった残りの回収に使う (既定は一覧のみ、`-Execute` で実行)。fixture は共通の補助 (`TestFixtures`) だけで作る ([fixture](docs/TESTING.md#fixtures))。
- `Remove-Item -Recurse` を使わない。
- UnRAR.dll をリポジトリ・製品の配布物に入れない (利用者が置く。[版の固定](docs/spec/rar.md#pinning))。WinRAR で作る RAR の実物はローカル検証専用で、リポジトリに収録せず CI でも実行しない。WinRAR (試用期間内を含め、そのライセンス条件の範囲内) で自作データから作り、第三者の RAR は使わない ([実物のRAR](docs/TESTING.md#rar-real))。

## 6. よく使うコマンド

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\get-unrar-dll.ps1
dotnet build unextract.sln
dotnet test unextract.sln
dotnet test tests/Unextract.E2E.Tests
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-e2e-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-gui-smoke-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-gui-ui-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\clean-test-fixtures.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-real-rar.ps1 -Exe <DLLを隣に置いたリポジトリ外のexe>
```

- 引き渡しの合否は `verify.ps1` (標準の検証) の全段の1回の実行で判定する ([標準の検証](docs/TESTING.md#verify))。文書・コメントだけの変更の例外は同節に従う。段は Release ビルド、solution のテスト、publish 版 E2E、GUI スモーク、UI E2E で、選択・省略しない。ほかのコマンドは修正中の確認 (失敗したテストだけの再実行など) に使ってよい。
- UI E2E を含むので、ロックされていない対話デスクトップが必要で、実行中は人がマウスとキーボードに触れない。リモートデスクトップの最小化、サービスセッション、他の UI テストとの同時実行では行わない。結果は `%TEMP%\unextract-verify\<checkout>\latest\summary.txt` を見る。`PASSED (not observed: N)` の未観測は成功と別に報告する。

- `get-unrar-dll.ps1` は RAR のテストが使う採用版の UnRAR.dll をリポジトリ外に用意する (一度だけ。DLL が無いと RAR のテストは失敗する)。
- `dotnet run` は使わない。ビルド済みの exe を呼ぶ。
- `dotnet publish` の出力先はリポジトリの外にする (`README.md` の「配布ビルド」)。
- CI の構成とオプションは `.github/workflows/ci.yml` を参照する。

## 7. 注意点

- ACL を変えるテスト (P03、S16、S34 など) は `AclChanges.Run` を使う。DENY の残存はテストの失敗として検出されるので、手動の `icacls` 確認は不要。
- Git Bash は `/q` などの引数を別のパスに変換する。必要なら `MSYS_NO_PATHCONV=1` を付けるか PowerShell を使う。
- 書き換えスクリプト (`py` など) の対象は、依頼で許可された範囲のファイルだけにする。
- 前提不成立 (管理者権限などが必要な項目。テスト出力の `前提不成立: ...`) は、成功でも失敗でもなく別に報告する。
- 未確認の事項 (ファイル symlink、クラウド placeholder、EFS) は成立と見なさず、非NTFS・USN機能のないFSは現行対象外とする ([観測の範囲](docs/OPEN_ISSUES.md#observations)・[受入条件](docs/TESTING.md#acceptance))。
- publish 版 E2E / PTY は `scripts/run-e2e-tests.ps1` を使う。一時 Release / `win-x64` publish、`UNEXTRACT_E2E_EXE` の設定、E2E project 全体の実行、環境変数の復元、自作 temp publish の cleanup を一括で行う。検証用の temp publish を手作業で残さない。外部 exe や既存 `bin/` / `obj/` は削除しない。
- E2E test 自身は publish しない。`UNEXTRACT_E2E_EXE` があればその exe を使い、未設定なら通常 build 出力を使う。通常の全体検証 (`dotnet test unextract.sln`) と publish 版の検証は役割が異なる。
- 手動確認 (実端末での進捗・警告の見え方 (M05・M08)、GUI の受入 (M14)、UnRAR.dll の導入と実物の RAR (M15)) の手順は [MANUAL_TESTS](docs/MANUAL_TESTS.md)、実施状態は [OPEN_ISSUES](docs/OPEN_ISSUES.md#manual-status)。確認入力・コードページ・中断などの機能は E2E と E2E PTY (X28〜X32) で自動化済み。

## 8. 報告の作法

- 事実と推定を区別して書く。
- 実行していないことを成功と書かない。未実施・前提不成立・失敗を区別する。
- 変更したファイルと、見つけた食い違い (変更せずに残したもの) を明示する。
