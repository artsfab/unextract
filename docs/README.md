# 文書の入口

役割: 変更する領域から担当正本とテスト節へ直行するための案内。初回は[CLAUDEの作業規則](../CLAUDE.md)と[SPECの共通契約](SPEC.md)を読み、以後は関係する節だけを選ぶ。

[読む経路](#routes) / [正本](#owners)

<a id="routes"></a>
## 変更別の読む経路

通常は基本経路の2〜3本文の該当節から始める。全リンク先の再帰的通読は不要。安全性・判断の前提を変える場合は条件付き追加も読む。

| 変更・目的 | 基本経路 | 条件付き追加 |
|---|---|---|
| entries・引数 | [CLI entries](spec/cli.md#entries) / [引数](spec/cli.md#arguments) → [TESTINGのL/K/P/X](TESTING.md#test-series) | Prepare順序は[SPEC](SPEC.md#prepare)。実端末encodingは[M13](MANUAL_TESTS.md#m13) / [保留](OPEN_ISSUES.md#procedure-review) |
| ZIP復号・CRC・上限 | [ZIP復号](spec/zip.md#decoding) / [内容](spec/zip.md#verification) / [上限](spec/zip.md#limits) → [C/Z/R](TESTING.md#test-series) | runtime更新・検査省略は[ZIP理由](RATIONALE.md#zip-runtime)。共通節を変えるときは[RAR](spec/rar.md)への影響も |
| RAR (受理範囲・DLL・名前・内容) | [RAR仕様](spec/rar.md)の該当節 → [RAR検証](TESTING.md#rar) | DLLの版・委譲・受理範囲を変えるときは[RAR理由](RATIONALE.md#rar)、未確認・リリース判断は[OPEN_ISSUES](OPEN_ISSUES.md#rar) |
| target・属性・hardlink・削除・エラー | [target](spec/filesystem.md#target-root) / [属性](spec/filesystem.md#special-files) / [hardlink](spec/filesystem.md#hardlink-parent-id) / [削除](spec/filesystem.md#delete-flow) / [エラー](spec/filesystem.md#open-errors) → [T/Sと重要回帰](TESTING.md#regressions) | 同一性/API/検査順序変更は[FS理由](RATIONALE.md#filesystem)と[未確認範囲](OPEN_ISSUES.md#observations)を必読 |
| 表示・警告 | [CLI出力](spec/cli.md#output) / [警告](spec/cli.md#warning) → [O/X](TESTING.md#test-series) | 視認性は[M05](MANUAL_TESTS.md#m05) / [M06](MANUAL_TESTS.md#m06) / [M08](MANUAL_TESTS.md#m08)。Entry表記は[entries](spec/cli.md#entries)も |
| 機械可読出力・実行ログ | [機械可読出力](spec/machine-output.md) → [TESTINGのJ](TESTING.md#regressions) | 出力経路・確認・終了状態・ログの順序を変えるときは[機械出力の理由](RATIONALE.md#machine-output)。人間向け表示は上の表示行 |
| Fast | [モード契約](SPEC.md#modes) → 変更領域の担当仕様 → [モード適用](TESTING.md#principles) | 安全検査は上のFS経路、分岐は[ARCHITECTURE](ARCHITECTURE.md#shared-path) |
| 配置・依存 | [ARCHITECTURE](ARCHITECTURE.md) → 対応仕様とコード | 採用理由の変更は[構造の理由](RATIONALE.md#structure) |
| GUI (検索・Target管理・解析・削除・結果表示・配布) | [GUI仕様](spec/gui.md)の該当節 → [GUI配置](ARCHITECTURE.md#gui) → [GUI検証](TESTING.md#gui) | 受信契約は[機械可読出力](spec/machine-output.md)。方式・画面構成・UI E2Eの方式を変えるときは[GUIの理由](RATIONALE.md#gui)。画面を変えたら[画面確認](TESTING.md#gui-review)、受入は[M14](MANUAL_TESTS.md#m14)。配布は[配布案内](../README.md#配布ビルド) |
| 検証実行 | [TESTING安全原則](TESTING.md#principles) / [実行案内](TESTING.md#e2e) → [ルートREADME](../README.md#ビルドとテスト) | 実端末は[MANUAL](MANUAL_TESTS.md)と[状態](OPEN_ISSUES.md#manual-status)、publishは[wrapper](../scripts/run-e2e-tests.ps1) / [CI](../.github/workflows/ci.yml) |
| 残課題・リリース判断 | [OPEN_ISSUES](OPEN_ISSUES.md) → [受入条件](TESTING.md#acceptance) | 必要な手順・限定付き根拠だけ |

<a id="owners"></a>
## 正本の担当

規範は[SPEC](SPEC.md)と[CLI](spec/cli.md)・[ZIP](spec/zip.md)・[RAR](spec/rar.md)・[FS](spec/filesystem.md)・[機械可読出力](spec/machine-output.md)、GUIを変えるときだけ[GUI](spec/gui.md)。配置は[ARCHITECTURE](ARCHITECTURE.md)、理由は[RATIONALE](RATIONALE.md)、確認方法は[TESTING](TESTING.md)・[手動手順](MANUAL_TESTS.md)、不確実性の現在値は[OPEN_ISSUES](OPEN_ISSUES.md)。ルートREADMEは利用者要約で仕様上の正本ではない。

文書の新設・分割・統合・廃止、責務変更、情報の配置・保存判断を行うときだけ[DOCUMENTATION](DOCUMENTATION.md)を読む。通常の仕様反映・誤字・既存リンク修正で毎回の通読は不要。
