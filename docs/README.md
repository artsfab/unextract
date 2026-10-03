# unextract MVP 設計文書

利用者向けの説明 (使い方、挙動、制限) はリポジトリ直下の [`README.md`](../README.md) にある。段階 F で、本書にあった利用上の制限の下書きをそちらに統合した。

| 文書 | 内容 |
|---|---|
| [`SPEC.md`](SPEC.md) | 仕様。唯一の基準 |
| [`PLAN.md`](PLAN.md) | 実装の境界、確認ゲート (G1〜G5)、実装順 (旧方式の段階 A〜F と新方式の段階 R0〜R7)、Win32 エラーと `DELETE_FAILED` / STOP の対応表、表示の文言 |
| [`PLAN_TESTS.md`](PLAN_TESTS.md) | テストの観点と実施場所 (Core / Win / CLI / E2E / PTY / 手動)、実測項目、旧テストとの対応 |
| [`PLAN_DECISIONS.md`](PLAN_DECISIONS.md) | 技術判断の理由 (DEC-1〜DEC-34。DEC-24〜DEC-34 は 2026-10-03 の実行モデル改訂) |
| [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) | 技術検証・PoC・実機確認の記録、ゲート状態、必須テスト対応表 (検証状況と検証記録) |
| [`MANUAL_TESTS.md`](MANUAL_TESTS.md) | 手動確認 (M 系) の具体的な手順とチェックリスト |
| [`PLAN_HISTORY.md`](PLAN_HISTORY.md) | 文書と実装の履歴 |

SPEC §13 の PoC と実装後の実機テストは実施済み (旧方式) で、削除機能のブロッカーは見つかっていない。確認できなかった事項 (SPEC §13 の「未確認の事項」と、2026-10-03 の改訂で生じた未実測の事項) は成立と見なさず、判定できなければ削除しない側に倒す。

2026-10-03 に実行モデルを `analyze` / `delete` に改訂した (SPEC 冒頭)。文書と製品コードは改訂済み (新方式は `PLAN.md` §3.2 の段階 R1〜R6 で実装。手動の M 系は未実施)。
