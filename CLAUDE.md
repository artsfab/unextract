# CLAUDE.md

本書は作業の参照先とルールだけを示す。仕様・計画の内容はここに複製しないので、必ず参照先を読む。強制力のある制限 (コミット・`dotnet run` の禁止など) は `.claude/settings.local.json` の権限設定が担う。

## 1. プロジェクト概要

- unextract は、ZIP 内のファイルに対応する target 内のファイルだけを削除する Windows 11 用 CLI。モードは2つ (SPEC §15)。
  - Strict (既定): ZIP の内容と全バイト一致したファイル (`MATCHED`) だけを削除する。全バイト一致が削除の根拠。
  - Fast (`--fast` による明示的な opt-in): パスとサイズの一致 (`SAME_SIZE`) を削除候補とし、内容は読まない・比較しない。内容の一致は保証しない。
- ファイルシステムの安全性 (パス検証、実名解決、reparse・hardlink・ADS、File ID・最終パスによる同一性、TOCTOU 対策、削除方式) は両モード共通。Fast を理由に弱めない。
- 「その ZIP から展開された」という来歴は証明しない。不明・判定不能なら削除しない (両モード)。

## 2. 文書の優先順位

- `docs/SPEC.md` が唯一の仕様上の基準。
- `docs/PLAN.md`、`docs/PLAN_TESTS.md`、`docs/PLAN_DECISIONS.md`、`docs/PLAN_VALIDATION.md` は実現・検証の文書で、仕様を追加・変更しない。
- 文書間・文書と実装の食い違いや不足を見つけたら、勝手に変更せず報告する。
- 技術判断の根拠は `docs/PLAN_DECISIONS.md` (`DEC-n`)、テストは `docs/PLAN_TESTS.md` (`D01` などの英字+番号)、実測は `docs/PLAN_VALIDATION.md`。
- 決定は `DEC-n`、テストは英字+番号で区別する (例: `DEC-12` とテスト `D12` は別物)。
- 履歴は `docs/PLAN_HISTORY.md` に追記する。利用者向けの説明はリポジトリ直下の `README.md`。

## 3. 絶対に守る安全規則

- 削除は、再検証 (Strict では再比較も) した同じハンドルへの `SetFileInformationByHandle(FileDispositionInfoEx)` の1か所だけ (SPEC §8.3)。
- flags は 0x3 (`DELETE | POSIX_SEMANTICS`)。`IGNORE_READONLY_ATTRIBUTE` は使わない。
- パスベースの削除・改名 API (`File.Delete`、`DeleteFile`、`Directory.Delete`、`RemoveDirectory`、`MoveFile*` など) を `src/` に書かない。
- 不明・判定不能は削除しない側 (FATAL または停止) に倒す。未知のエラーを推測で続行しない (SPEC §8.4)。
- 独自 ZIP パーサ、reflection、ntdll の未文書 API を使わない。ZIP の読み取りは `ZipArchive` のみ。
- `Unextract.Core` は Win32 にも `Unextract.Windows` にも依存しない。
- Fast は既存の `Unextract.Core`・`Unextract.Windows`・`Unextract.Cli` の中のモード分岐として実装する。Fast 用の層、専用の実装クラス・抽象化、追加の状態管理、Fast 用の Win32 API やハンドル構成を設けない (`docs/PLAN.md` §1・§4、DEC-19、DEC-23)。

## 4. 開発規則

- 警告ゼロ (`Directory.Build.props` の `TreatWarningsAsErrors`)。
- `NoWarn`、`#pragma warning disable`、`TreatWarningsAsErrors` の解除をしない。
- テストの Skip、期待値の緩和をしない。テストが失敗したら、まず実装を疑う。
- 不可視文字・双方向制御文字 (U+202A〜U+202E、U+2066〜U+2069 など。一覧は `docs/PLAN.md` §5) を `src/` `tests/` `docs/` `scripts/` `.github/` に実際の文字として入れない。必要なら `\u` エスケープで書く。

## 5. 作業規則

- コミット・プッシュ・`git checkout` はしない (人間が行う)。
- `LICENSE` は MIT。内容は変更しない。
- 実在のユーザーデータを target にしない。
- 削除してよいのは、テスト・検証が自作した一意な fixture と、承認を得た後始末だけ。
- fixture は原則テストから削除せず、掃除は `scripts/clean-test-fixtures.ps1` で行う (既定は一覧のみ、`-Execute` で実行)。例外として M08 / O07 の PTY テストは、PTY / process tree の終了・Dispose 完了後に自分が作った GUID 付き fixture だけを自動 cleanup する。cleanup failure は黙殺せず、元の失敗情報・terminal output を保持する。
- `Remove-Item -Recurse` を使わない。

## 6. よく使うコマンド

```text
dotnet build unextract.sln
dotnet test unextract.sln
dotnet test tests/Unextract.E2E.Tests
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-e2e-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\clean-test-fixtures.ps1
```

- `dotnet run` は使わない。ビルド済みの exe を呼ぶ。
- `dotnet publish` の出力先はリポジトリの外にする (`README.md` の「配布ビルド」)。
- CI の構成とオプションは `.github/workflows/ci.yml` を参照する。

## 7. 注意点

- ACL を変えるテスト (P03、D11、D22 など)。終了後に DENY が残っていないことを `icacls` で確認する。
- Git Bash は `/q` などの引数を別のパスに変換する。必要なら `MSYS_NO_PATHCONV=1` を付けるか PowerShell を使う。
- 書き換えスクリプト (`py` など) の対象は、依頼で許可された範囲のファイルだけにする。
- 前提不成立 (管理者権限などが必要な項目。テスト出力の `前提不成立: ...`) は、成功でも失敗でもなく別に報告する。
- 未確認の事項 (ファイル symlink、クラウド placeholder、EFS、USN のない FS) は成立と見なさない (SPEC §13・§14)。
- publish 版 E2E / PTY は `scripts/run-e2e-tests.ps1` を使う。一時 Release / `win-x64` publish、`UNEXTRACT_E2E_EXE` の設定、E2E project 全体の実行、環境変数の復元、自作 temp publish の cleanup を一括で行う。検証用の temp publish を手作業で残さない。外部 exe や既存 `bin/` / `obj/` は削除しない。
- E2E test 自身は publish しない。`UNEXTRACT_E2E_EXE` があればその exe を使い、未設定なら通常 build 出力を使う。通常の全体検証 (`dotnet test unextract.sln`) と publish 版の検証は役割が異なる。
- 手動確認 (実端末の操作・進捗・コードページ・視認性) の手順と記録は `docs/MANUAL_TESTS.md`。M08 / O07 の機能部分は E2E PTY で自動化済み。

## 8. 報告の作法

- 事実と推定を区別して書く。
- 実行していないことを成功と書かない。未実施・前提不成立・失敗を区別する。
- 変更したファイルと、見つけた食い違い (変更せずに残したもの) を明示する。
