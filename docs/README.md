# unextract MVP 設計文書

利用者向けの説明 (使い方、挙動、制限) はリポジトリ直下の [`README.md`](../README.md) にある。段階 F で、本書にあった利用上の制限の下書きをそちらに統合した。

| 文書 | 内容 |
|---|---|
| [`SPEC.md`](SPEC.md) | 仕様。唯一の基準 |
| [`PLAN.md`](PLAN.md) | 実装の境界、確認ゲート (G1〜G4)、実装順、Win32 エラーと `DELETE_FAILED` / 停止の対応表 |
| [`PLAN_TESTS.md`](PLAN_TESTS.md) | テストの観点と実施場所 (Core / Win / 手動) |
| [`PLAN_DECISIONS.md`](PLAN_DECISIONS.md) | 技術判断の理由 (DEC-1〜DEC-18) |
| [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) | 技術検証・PoC・実機確認の記録、ゲート状態、必須テスト対応表、手動確認のチェックリスト |
| [`PLAN_HISTORY.md`](PLAN_HISTORY.md) | 文書と実装の履歴 |

`archive/` は旧版 (v3、v4) の文書で、現在の仕様の根拠や通過済みの検証としては扱わない。

SPEC §13 の PoC と実装後の実機テストは実施済みで、削除機能のブロッカーは見つかっていない。確認できなかった事項 (SPEC §13 の「未確認の事項」) は成立と見なさず、判定できなければ削除しない側に倒す。
