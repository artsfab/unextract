# unextract PLAN — 監査証跡(完了済みの調査・レビュー・改訂)

- 本書は `docs/PLAN.md` の分冊である。文書構成と節の所在は `PLAN.md`「文書構成と参照ガイド」
- 本書は、現在の設計を理解するために毎回読む文書ではない。過去の判断根拠・調査証跡を追跡するときに読む。現在有効な結論は `PLAN_DECISIONS.md`、現在の計画は `PLAN.md`
- 収録: §1.1〜§1.7(再監査結果、第2改訂で反映したレビュー指摘)、§2(各改訂の作業条件。§2.2 を除く)、§15(自己点検の記録)、P0-3 調査記録(旧 `docs/P0-3_results.md` の全文)、文書分割の記録
- 改訂の概要(第1〜第4改訂)は `PLAN.md` 冒頭にある

---

## 1. 再監査結果

旧PLANの全節を SPEC v4 §1〜§19、テスト #1〜#54 と突合した結果。「扱い」の列は本版での処理を示す。

### 1.1 文書全体・凡例

| # | 旧PLANの節 | 旧前提・矛盾・不足 | SPEC v4 | 扱い |
|---|---|---|---|---|
| A-01 | 表題・対象 | 「SPEC v3 対応」 | 全体 | v4 に書き換え |
| A-02 | 凡例 | 「確認済み(実機)」「確認済み(一次資料)」が、過去観測と今回の確認を区別しない | TASK の事実区分 | §0.2 の4区分に置換。過去観測は [過去実機] として観測条件つきで扱う |
| A-03 | §1 スパイク環境 | OS ビルド、C: のファイルシステム種別、権限(管理者か否か)の一部が明記されていない | — | §2.2 で「記録あり/記録なし」を分けて再掲。記録のない条件に依存する結論は一般保証にしない |
| A-04 | 全体 | 一次資料の参照が `release/10.0` ブランチ | TASK(タグで確認) | タグでの再確認を Phase 0 タスク化(P0-3)。旧観測は [未確認] 扱い |
| A-05 | §2 C 冒頭、§4.2 | 自前リーダーが既定路線として固定されている | §6.2、§18 | 方式は §6.2 の4能力を満たすかで選ぶ。Phase 1 のゲート(G1)にする(§10) |

### 1.2 ごみ箱・削除(旧 §2 A、§4.5、§5、§6)

| # | 旧PLANの節 | 旧前提・矛盾・不足 | SPEC v4 | 扱い |
|---|---|---|---|---|
| B-01 | A-2 結論「三重の防御」、§4.5-5、R-4 | 事後検出(PostDeleteItem の `psiNewlyCreated==NULL`)を防御の一部として数えている | §10.1「事後検出は保証の代替にならない」、§2 | 事後検出は**内部安全性エラーの検出手段**としてのみ残し、保証には数えない。保証条件が確立できない間は削除しない。Recycle ゲート(G3)を新設 |
| B-02 | A-2、Q-8 | `DRIVE_FIXED` 判定+PreDeleteItem の 0x80 検査で「ごみ箱が使える」とみなす | §10.1(削除開始前の保証) | 保証条件としては未確立。0x80 がボリューム側のフォールバックでも消えるかは未観測。§3.1、G3 に移す |
| B-03 | §4.5-5-5、Q-7 | 異常検出時は「ERROR、終了コード 1」 | §12.6(内部安全性エラー、終了コード 3、reason 分離) | 内部安全性エラーとして終了コード 3。reason を3種に分離(§4.8) |
| B-04 | A-4 | `PerformOperations` の戻り値と `GetAnyOperationsAborted` の扱いが「Sink で判定する」のみ。Post 欠落・結果取得失敗・関連項目の追加への対応がない | §10.1、§12.6 | Shell 結果の状態機械を設計(§4.7)。「削除済み」は積極的確認時のみ |
| B-05 | A-5、§4.5-5-4、付録 | `--delete-permanently` で `IGNORE_READONLY_ATTRIBUTE` を使い読み取り専用も削除 | §10.1(読み取り専用は ERROR、属性無視の手段は採らない) | 撤回。`IGNORE_READONLY_ATTRIBUTE` は使わない。読み取り専用は両モードで ERROR |
| B-06 | Q-9 | 推奨「一致すれば読み取り専用でも削除」 | §10.1、§15、#49 | **撤回** |
| B-07 | §4.5-6 | ディレクトリ削除で、削除したファイルの祖先個体との同一性の再確認、名前付きデータストリームの確認がない | §10.2、§7.4 | ディレクトリ個体の寿命設計(§4.6)に置換 |
| B-08 | §4.5-6 | 削除マーク(disposition)と実際の消滅の違いを考慮していない | §10.2(子から親、空の確認) | §4.6 に反映 |
| B-09 | §6 README 案 1 段落目 | SPEC §16 の文言を「大幅に縮小でき、事後検出もできる」に置き換える提案(旧 X-5) | §16 | 撤回。README は §16 に従う。補足文案は §9.2 に、§16 と矛盾しない範囲でのみ置く |
| B-10 | §3 共通準備 | 手動確認の前提が「Phase 3 完了後の `--diag-recycle`」。Recycle ゲートより後に置かれている | §10.1、G3 | 診断手段は G3 の判断材料なので、Recycle 実装着手より前に用意する(P0-5) |
| B-11 | — | Ctrl+C・キャンセルの検討がない | (SPEC 要件なし) | 設計検討として §4.10 に追加。SPEC 要件として扱わない |

### 1.3 パス・解決・reparse(旧 §2 B、§4.4、§4.5-1/3)

| # | 旧PLANの節 | 旧前提・矛盾・不足 | SPEC v4 | 扱い |
|---|---|---|---|---|
| C-01 | §4.4 `ITargetRoot.OpenFile(components)` | 成分列を一括で渡す契約。単一成分ずつ、検証済み親個体を起点にすることが型として保証されない | §7.2 | 単一成分の解決契約に再設計(§4.3)。多成分を受け取る API を抽象から除く |
| C-02 | §4.5-3-5、§4.5-5-1 | 分類後にハンドルを閉じ、削除時に「3-1 と同様に」開き直す。開き直しの起点が明示されていない | §7.2(検査・再検証・削除のすべて) | 削除フェーズも保持中の target 個体から単一成分ずつ解決する(§4.4、§4.6) |
| C-03 | §4.5-1、Q-4 | target 自体が reparse の扱いを「全件 SKIPPED」と推奨 | §4(終了コード 3)、#47 | 撤回。SPEC どおり終了コード 3 |
| C-04 | Q-4、Q-13 | 「target の祖先の reparse は不問、実パス解決だけ」 | §4、§7.2(実ディレクトリを個体として保持、固定できなければ 3)、#48 | 初期化順序(§4.2)に置換 |
| C-05 | Q-1、B-8 | 「配下も拒否するか」が判断事項 | §4(配下も拒否、安全ポリシー) | SPEC で決定済み。判断事項から削除 |
| C-06 | B-8 | 拒否判定の位置づけ(ポリシーか包含の根拠か)が未記述 | §4、§7.2 | 拒否リストはポリシー判定であり、包含の根拠ではないと明記(§4.2) |
| C-07 | B-7、X-4 | 大文字小文字の用途別書き分けを SPEC への提案としている | §7.2(v4 で書き分け済み) | SPEC で決定済み。提案を削除 |
| C-08 | B-2 | ディレクトリ保持による固定の観測が、ファイル単位の処理中に限られる | §10.2 | ディレクトリ個体の寿命設計に組み込む(§4.6)。Shell の移動との両立は [未確認] |
| C-09 | B-5 | ファイル ID が時間をまたいで一意であるかのような書き方(「削除まで不変」) | §10.2 | ID は時間をまたぐ一意性を保証しない前提に修正。同一性の主根拠はハンドルの連続保持とする |
| C-10 | — | 名前付きデータストリームの検査がない | §7.4、#39、#40 | 追加(§4.5)。内部 reason `NamedDataStream` |

### 1.4 クラウド(旧 B-3、Q-2、R-5、README 案)

| # | 旧PLANの節 | 旧前提・矛盾・不足 | SPEC v4 | 扱い |
|---|---|---|---|---|
| D-01 | Q-2 | スキップするかが判断事項 | §7.3(識別された placeholder は SKIPPED) | SPEC で決定済み。削除 |
| D-02 | B-3、§4.5-1、P2-4 | 公開モード設定の失敗時の扱いが「ERROR か続行か決定する」 | §7.3(初期化失敗は削除開始前に終了コード 3)、#51 | 終了コード 3 に確定(SPEC 要件) |
| D-03 | — | 個別対象の状態取得失敗の扱いがない | §7.3(当該対象 ERROR)、#52 | 追加 |
| D-04 | R-5 | 「漏れてもハイドレーション後の比較で一致時だけ削除されるので誤削除にならない」 | §7.3(識別前に内容を読まない) | **撤回**。識別前の読み取り自体が禁止 |
| D-05 | §6 README 案 3 段落目 | 「同期状態にかかわらず削除対象にならない」 | §7.3(識別を確認した範囲に限る)、§16 | **撤回**。§16 の文言に合わせる |

### 1.5 ZIP(旧 §2 C、D、§4.2、§4.3)

| # | 旧PLANの節 | 旧前提・矛盾・不足 | SPEC v4 | 扱い |
|---|---|---|---|---|
| E-01 | §4.2 | ローカルヘッダーとセントラルの不一致を ERROR(entry-local) | §12.2、#36 | archive-fatal に変更 |
| E-02 | §4.2 | 宣言件数超過を「アーカイブ全体を上限超過」としつつ終了コードが未定(Q-5) | §6.1、§12.2、#10 | archive-fatal、終了コード 3(SPEC で決定済み)。Q-5 削除 |
| E-03 | §4.2、D | メタデータ総量、名前長、パスの深さの上限がない | §6.1 | 追加(§4.9)。値は測定まで確定しない |
| E-04 | D「10万件でヒープ約 35 MB」「1件あたり約 350 B」 | 名前長が記録されていない測定。上限の根拠にならない | §6.1(測定条件を明記) | 根拠から除外。再測定を P1-10 とする |
| E-05 | C-7、§4.2 | 公開 `DeflateStream` を使えば終端を確認できる前提 | §6、§6.2-1、#34 | 入力 EOF が正常終端を保証しない経路がある(TASK 記載の旧調査)。終端確認の方法を設計に含め、Phase 1 ゲートで確認(§3.4、§4.1) |
| E-06 | — | archive-fatal / unsupported / entry-local / 内部安全性エラーの分類がない | §12 | 分類表を作成(§5) |
| E-07 | — | 削除開始前に全エントリの構造検証を完了する設計がない(Local Header を削除時に初めて読む余地がある) | §6.2-4、§12.2 | 分類フェーズで全エントリの Local Header・Data Descriptor を検証(§4.1) |
| E-08 | — | SFX、余剰データ、複数ディスク、CD 暗号化の扱いがない | §12.5、#37、#46 | unsupported として分類表に追加。APPNOTE 上可能な形式と区別 |
| E-09 | X-2 | `ZipArchive` を使う場合「SPEC 側を緩める必要がある」と SPEC 変更を提案 | §6.2、TASK(緩和を決めない) | 撤回。方式が能力を満たさなければ採用しない |
| E-10 | X-1、X-3 | SPEC の文言への注記を提案 | §9、§6 | 提案を削除。実装上の注意として §3.4 に残す |
| E-11 | Q-11 | 不正 UTF-8 を判断事項としている | §7.1、#54 | SPEC で決定済み。削除 |
| E-12 | §4.3 | 空成分・`.` 成分の拒否が「推奨」 | §7.1、#45 | SPEC 要件として扱う |
| E-13 | §4.2 | Deflate64 等の未対応方式は ERROR | §12.5(Stored / Deflate 以外は entry-local ERROR) | 一致。維持(理由: SPEC と整合) |
| E-14 | C-5、Q-10 | ZIP 側の symlink 以外の属性(Unix 種別、Windows reparse ビット)を SKIPPED にする推奨 | §7.3(symlink のみ明記) | SPEC にない追加判断のため、§8 の要確認事項へ移す |
| E-15 | — | 正常 ZIP の `ZipArchive` 差分テストの位置づけ(独立した基準にならない部分)がない | TASK | テスト計画に追加(§11.3) |

### 1.6 リソース・配布・テスト・フェーズ

| # | 旧PLANの節 | 旧前提・矛盾・不足 | SPEC v4 | 扱い |
|---|---|---|---|---|
| F-01 | Q-6、P2-1「AOT アナライザの警告 0」 | NativeAOT の準備を完了条件に含めている | TASK(NativeAOT を採用条件にしない) | 完了条件から除外。配布は framework-dependent または self-contained single-file |
| F-02 | §7 | Phase の開始条件(gate)がない | §18 | 各 Phase に gate を設定(§10) |
| F-03 | §8 | テストが #1〜#30 のみ | §17(#1〜#54) | 全54件の対応表(§11.1) |
| F-04 | §8 #7 | 「破損 ZIP は終了コード 3」と entry-local を混在 | #7、#32、#33 | #7 は entry-local、構造破損は #32 に分離 |
| F-05 | §8 #10 | 「ERROR」「Q-5 で決める」 | #10 | archive-fatal、終了コード 3 |
| F-06 | §8 #29 | 「Q-1 の結果で配下のケースを追加」 | §4、#29 | 配下のケースを必須にする |
| F-07 | Q-12 | スナップショット項目の提案 | #22(パス、サイズ、ハッシュ、更新日時) | SPEC の項目を必須とし、属性の追加比較は PLAN の追加テストとして扱う |
| F-08 | §9 | 版数を「調査時」の固定値として記載 | — | 過去観測の版数として明記し、採用時に再選定 |

### 1.7 第2改訂で反映したレビュー指摘(本書の第1改訂に対するもの)

| # | 指摘 | 対象 | 扱い |
|---|---|---|---|
| RV-01 | 読み取り専用の比較省略は `MODIFIED` を `ERROR` にすり替え、§10.1・#49 と食い違う | §4.4 手順3・5、§4.5.2 | 比較を省略しない。`MATCHED` かつ読み取り専用のときだけ `ERROR`。追加テスト X-17 |
| RV-02 | archive-fatal と unsupported の境界(F3/U3、U2/F15)が未定義。Stored のサイズ矛盾の行がない | §5、§4.1.2 | §5.1.1 の判別規則(案)、F19(暫定候補・未決定)を追加。人間の確認事項として S-15、S-16。追加テスト X-18、X-19 |
| RV-03 | ごみ箱送りの最終段は Shell のパス再解決であり、§7.2 の充足を主張できない | §4.3.1、§4.7.2、§7.1、K-02 | 充足を主張しない旨を明記。S-17 として人間に戻す |
| RV-04 | 結果不明を `PermanentDeletionOccurred` と断定している | §4.7.3、§4.8、#53 | `UnknownShellOutcome` を独立 reason に。S-05 更新、追加テスト X-20 |
| RV-05 | G3 の判断材料が遅い | §10 | P0-7 に前倒し。V-04 の影響範囲に §10.2、#7/#11/#33 を追加 |
| RV-06 | G1 の位置が実際の依存順と不一致。G0 と P0-4〜P0-6 の関係が不明 | §10.1 | §10.1.1 に依存グラフとゲート通過前の可否を固定 |
| RV-07 | 旧区分名「一次資料*」は今回確認済みに見える。K-10 の根拠が R-B3 より広い | §0.2、全体、K-10 | 区分名を「旧PLAN記載の一次資料要約(今回未再読)」に変更。K-10 を R-B3 の範囲に縮小 |
| RV-08 | Z-A の終端確認が循環的 | §4.1.1 | 「入力 EOF 到達は終端未確認として `ERROR`」を主方針に。実用上の成立性を G1 で評価、不成立なら Z-B |
| RV-09 | §15 が「検索した」と記載 | §15 | 実際の確認方法に修正 |
| RV-10 | 分類時 ID と再オープン時 ID の関係 | §4.4 手順4 | ID は置換検出の補助条件であり同一個体の証明ではないと明記 |


### 1.8 第4改訂で反映した事項(Codex 独立レビュー、P0-3、P0-3b)

→ `PLAN_DECISIONS.md` §1.8 へ移動した(CR-01〜CR-18)。

---

## 2. スパイク環境と今回の追加調査

### 2.1 第1改訂の作業条件(記録)

- 第1改訂では**シェルを使用していない**。追加の実機調査、ビルド、実行、外部取得は行っていない
- 読み込んだのは `docs/SPEC.md`(v4)と旧 `docs/PLAN.md` のみ。過去のレビュー・検証結果・決定メモは読んでいない
- 一次資料(Microsoft Learn、dotnet/runtime、APPNOTE、CsWin32)は第1改訂の作業では再読していない。旧PLANの引用は [旧一次資料要約] として扱った(第4改訂で P0-3 の結果により更新。§2.5)
- 実機確認が必要な事項は [未確認](要実機)として残した

### 2.2 旧スパイク環境(過去の観測条件。今回再検証していない)

→ `PLAN_DECISIONS.md` §2.2 へ移動した。

### 2.3 第2改訂(レビュー反映)の作業条件

- 人間の指示により、レビュー指摘 1〜8 と軽微2件を PLAN 本文へ反映した。SPEC、ソース、テスト、CI 設定は変更していない
- 追加の実機調査、ビルド、実行、外部取得は行っていない。一次資料は再読していない。作業環境のシェルは、PLAN ファイルの編集と、番号参照・#1〜#54 の網羅の機械確認にのみ使った
- 第2改訂で新たに「確認済み」とした事実はない

### 2.4 第3改訂(人間の決定の反映)の作業条件

- 人間が決定した H-01〜H-12 と S 項目の扱いを、PLAN 本文に転記した。SPEC、ソース、テスト、CI 設定は変更していない
- P0-3 以降の調査、外部取得、スパイク、実機確認は行っていない。この環境は Linux であり、Windows 実機確認の代替にはならない
- H-08 の版情報(.NET 10 が現行 LTS、ランタイム 10.0.12、SDK 10.0.401)は、人間の方針決定の前提であり、本書では一次資料で確認していない([未確認]。R-Z14)(第4改訂で P0-3 の結果により更新。§3.4)

### 2.5 P0-3 と第4改訂の作業条件

- P0-3 は 2026-10-01 に実施された(別作業)。結果は `docs/P0-3_results.md`(現 `PLAN_HISTORY.md`「P0-3 調査記録」) に記録されている。外部取得は TASK の5系統のみで、ビルド・実行・Windows 実機確認は含まない。検索結果に混じった二次資料は根拠にしていない(同ファイルの注記)
- 第4改訂では、`TASK.md`、`docs/SPEC.md`、`docs/PLAN.md`、`docs/P0-3_results.md`(現 `PLAN_HISTORY.md`「P0-3 調査記録」)、および人間から渡された P0-3b 判断と独立レビューの採用指摘を入力とした。一次資料の再取得、実機調査、ビルド、実行は行っていない
- シェルは使用していない(実行しようとしたが権限設定により拒否されたため、回避していない)。改訂後の文書内整合の機械確認(§15)は、ファイル検索ツール(正規表現による検索)で行った。これは文書内の整合の確認であり、一次資料・実機の確認ではない
- 第4改訂で「確認済み」とした事実は、P0-3 の記録に「◎」(または「○」の確認済み範囲)として記載されたものに限る

---

## 15. 自己点検の記録

**第1改訂**: 更新後の本書を通読し、v3 前提の語・記述(「SPEC v3」「確認済み」、`IGNORE_READONLY_ATTRIBUTE` の使用、「同期状態にかかわらず」、旧 Q 番号、「三重の防御」、「約 35 MB」、ブランチソースの根拠化、NativeAOT の採用条件化)の残存を確認した。該当箇所はすべて §1 の再監査表・§2・§3 の区分注記・§9.2 の撤回の記載の中にあり、現行の設計・案として残っているものはない。参照番号の誤り(NativeAOT の手動確認 ID)を1件修正した。(第1改訂時はシェルを使用していないため、確認は通読による。)

**第2改訂**: レビュー指摘の反映後、スクリプトで次を機械確認した。①§11.1 が #1〜#54 を欠落・重複なく含む、②S-、X-、M-、R- の参照に未定義 ID がない、③旧区分名「一次資料*」(角括弧つきの旧記号)が残っていない、④v3 前提の語が §1 の再監査表・撤回記載以外に残っていない。これは文書内の整合の確認であり、一次資料・実機の確認ではない。

**第3改訂**: 人間の決定の転記後、スクリプトで次を機械確認した。文書内の整合の確認であり、一次資料・実機の確認ではない。

- §11.1 が #1〜#54 を欠落・重複なく含む。S-、X-、M-、R-、K-、V-、O-、RV-、P0- の参照に未定義 ID がない(`V-` の正規表現が `RV-08` 等に一致した誤検出は除外)
- §8 の S-01〜S-17 の17行すべてに決定状況が付いている。§14 に H-01〜H-12 と S-01〜S-17 のすべてが現れる
- 決定の転記を個別に確認した: H-10 = B(「G2 の阻害要因としない」の記述なし)、S-16 は未決定(「案: archive-fatal」の確定表現なし。F19 は暫定候補・未決定)、S-08 の「観測できなければ Z-B」の記述なし、S-05 の「継続可否は判断待ち」の記述なし
- G0 は「P0-3 の実施に G0 の成立は不要。(a)〜(d) が終わった時点で成立」となっている。Phase 3 の開始条件は G3 成立
- H-08 の版情報は [未確認(要一次資料)](R-Z14)。H-12 は「暫定第一候補・採用決定ではない」。H-09 は「事実検証を含まない」。外部取得は5系統、容量は 5 GiB
- 旧区分名「一次資料*」(角括弧つき)の残存なし

この確認は、決定の内容が PLAN に転記されたことの確認であり、決定内容の妥当性や、P0-3 以降の事実の確認は含まない。

**第4改訂**: シェルは権限設定で拒否されたため、ファイル検索ツール(正規表現)で確認した。文書内の整合の確認であり、一次資料・実機の確認ではない。

- S-01〜S-23: §8 に23行あり、全行に状態(決定/承認/保留/未決定)がある。未決定・保留の行には判断時期がある(§14.6 を含む)
- 参照 ID: S-、H-(H-01〜H-12)、R-A1〜A17・B1〜B18・C1〜C6・Z1〜Z14、G0/G1/G2/G2b/G3/G3-a〜e、V-01〜V-09、M-01〜M-18、X-01〜X-26、K-01〜K-20、O-01〜O-16、CR-01〜CR-18 のうち、未定義の ID への参照は見つからなかった(§1 の `X-1`〜`X-5` は旧PLAN の提案番号の記録であり対象外)
- 旧記述: 「P0-3 未着手」「要タグソース確認」「3 reason」「暫定候補・未決定」「M-01〜M-05(G3 の判定材料として)」は、§1・§15 の履歴記録を除いて残っていない
- §11.4 は #1〜#54 の全件を (A)〜(F) に重複なく分類している(目視で照合)
- X-01〜X-26 は §11.2.1 と §10.2 で全件タスクに割り当てた
- §11.1 は、第4改訂後の検索で #1〜#54 の行が連続して1行ずつ存在し、欠落・重複がないことを確認した
- 第4改訂の差分は、その後の最終監査で独立レビューを受け、指摘は反映済み(§10.1.2)。この記録は一次資料・実機による事実検証を意味しない(H-09)

---

## P0-3 調査記録(旧 `docs/P0-3_results.md`)

以下は `docs/P0-3_results.md` の全文である。本文は原文のままとし、見出しのレベルだけを2段下げた。題名の「PLAN 本体は未更新」は記録作成時点の状態であり、その後 PLAN 第4改訂で反映済み(`PLAN_DECISIONS.md` §3、§14.5)。記録中の「PLAN §x」は分割前の PLAN の節番号を指す(節の所在は `PLAN.md`「文書構成と参照ガイド」)。各項目の区分の一覧は `PLAN_DECISIONS.md`「P0-3 確認状態の一覧」。

### P0-3 一次資料の再確認 結果(PLAN 本体は未更新)

- 確認日: **2026-10-01**(全項目共通)
- 範囲: PLAN §10.2 の P0-3 に定義した項目のみ。P0-3b の判断、P0-4〜P0-7、スパイク、ビルド、実装、Windows 実機確認は行っていない
- 外部取得先: Microsoft Learn(Win32 / WDK DDI / Cloud Files)、.NET 公式(dotnet.microsoft.com)、dotnet/runtime の `v10.0.12` タグ、PKWARE APPNOTE、CsWin32 公式の5系統のみ
- 取得方法の注記:
  - dotnet/runtime のソースは `raw.githubusercontent.com/dotnet/runtime/v10.0.12/...` から取得し、読み取りだけを行った(ビルド・実行なし)。GitHub API はレート制限により応答せず、タグの参照(コミット SHA)は直接確認できていない。タグ名の URL が HTTP 200 を返したことのみを根拠にしている
  - 検索結果には5系統以外(Python の send2trash サンプル、APPNOTE の旧版ミラー、Microsoft Q&A)が混じっていた。これらは安全性判断の根拠にしていない。使った箇所は「二次資料」と明記する
- 保証範囲の欄は、文書が実際に述べている範囲だけを書く。文書が述べていない部分は「記載なし」と書く
- 「区分」: ◎ = [一次資料で確認した事実] へ更新可 / ○ = 一部のみ更新可(残りは未確認) / × = 更新不可(未確認のまま)または食い違いあり

#### A. ごみ箱 / IFileOperation

| ID | 確認対象の主張 | 文書名 | 節・API・該当箇所 | 保証範囲(文書が述べている範囲) | PLAN と一致 | 差分 | 区分 |
|---|---|---|---|---|---|---|---|
| R-A2 | `IFileOperation` は STA でのみ使える | Microsoft Learn「IFileOperation interface (shobjidl_core.h)」 | Remarks | STA でのみ使用でき、MTA では使えない。MTA では `SHFileOperation` を使う必要がある | 一致 | なし | ◎ |
| R-A4 | `PreDeleteItem` がエラーを返すと、その削除と以降の操作が取り消される | 「IFileOperationProgressSink::PreDeleteItem」 | Return value | エラー値を返すと、その削除と、`IFileOperation` に保留中の以降の操作がすべて取り消される | 一致 | 「エラー値」一般についての記述で、`E_ABORT` に限定していない。取り消し後の対象が「無傷で残る」ことまでは述べていない。二次資料(send2trash のサンプルのコメント)には「操作は実際には止まらない」とする記述があり、公式文書と旧スパイク(R-A3、R-A14)と食い違う。二次資料なので根拠にしない | ◎(主張の範囲で)。二次資料との食い違いは M-14 等で実機観察 |
| R-A5 | `PostDeleteItem` の `psiNewlyCreated` は、完全削除時に NULL | 「IFileOperationProgressSink::PostDeleteItem」 | Parameters(`psiNewlyCreated`、`hrDelete`、`dwFlags`) | 「ごみ箱内の削除済み項目。完全に削除された場合は NULL」。`hrDelete` は実際の削除結果であり、`DeleteItem` の戻り値ではない。`dwFlags` は「削除中に値が設定・変更されうる」 | 一致 | PLAN に記載のない点: `dwFlags` が削除中に変化しうる | ◎ |
| R-A6 | ALLOWUNDO 指定時に、ごみ箱無効・容量超過・リムーバブル・ネットワークで完全削除に落ちる場合にも 0x80 が消えるか | 「_TRANSFER_SOURCE_FLAGS enumeration」「IFileOperation::SetOperationFlags」 | `TSF_DELETE_RECYCLE_IF_POSSIBLE`(0x80)、`FOF_ALLOWUNDO` | 0x80 は「可能なら、ファイル削除時にごみ箱へ送る」。`FOF_ALLOWUNDO` は「可能なら、undo 情報を保持する」。フォールバック時の 0x80 の扱いは記載なし | 一致(未確認のまま) | 文書は "if possible" を明記するのみ。「`dwFlags` が削除中に変化しうる」(R-A5)ことから、Post 側で再確認する設計の余地があるが、これは推測 | × 未確認(要実機)のまま |
| R-A7 | `FOFX_RECYCLEONDELETE`、`FOF_WANTNUKEWARNING` のフォールバック時の挙動 | 「IFileOperation::SetOperationFlags」 | 各フラグの説明 | `FOFX_RECYCLEONDELETE`(Windows 8 以降): 「削除時はごみ箱へ送り、完全削除しない」。`FOF_WANTNUKEWARNING`: 「ごみ箱に送られず破棄される場合に警告を送る。`FOF_NOCONFIRMATION` を部分的に上書きする」。フォールバック時の挙動は記載なし | 一致 | 追加情報: `FOF_NOERRORUI` のみで `FOFX_EARLYFAILURE` なしの場合、エラーは「無視」として扱われ、中断フラグが立ち、残りは続行される。`FOFX_EARLYFAILURE` ありなら全体が停止する。R-A8(個別失敗でも 0 が返り、`GetAnyOperationsAborted` が True)の旧観測と整合する説明 | × 未確認(要実機)のまま。`FOF_NOERRORUI` / `EARLYFAILURE` の記述は ◎ |
| R-A9 | `GetAnyOperationsAborted` の厳密な定義 | 「IFileOperation::GetAnyOperationsAborted」「IFileOperation::SetOperationFlags」 | Remarks、`FOF_NOERRORUI` | `PerformOperations` が完了前に止まったかを返す。止まる原因は「ユーザー操作、またはシステムによる無通知の停止」。`PerformOperations` が成功コードを返しても、止められた場合がある。成功・失敗にかかわらず呼ぶべき。`FOF_NOERRORUI` 下の個別エラーは、中断フラグを立てて続行する(SetOperationFlags) | 一致 | 原因を網羅した定義はない。「どの個別エラーで True になるか」の完全な表は記載なし | ○(主な原因は確認。網羅性は保証されない) |
| R-A10 | `FOF_NO_CONNECTED_ELEMENTS` の効果。指定しない場合に HTML と関連フォルダが一緒に移動されるか | 「IFileOperation::SetOperationFlags」 | `FOF_NO_CONNECTED_ELEMENTS`(0x2000) | 「関連項目をグループとして移動しない。指定したファイルだけを移動する」(move の文脈の記述) | 一致(未確認のまま) | 「関連項目」の定義、`x.htm` と `x_files` の例、削除操作への適用の有無は記載なし。`SetOperationFlags` を呼ばない場合の既定値は `FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR` | × 未確認(要実機)のまま。フラグの存在と一般的な意味は ◎ |
| R-A15 | `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` は `SHFileOperation` を `FOF_ALLOWUNDO` で呼ぶだけで、ファイル単位の結果を得られない | dotnet/runtime `v10.0.12` タグ | `Microsoft.VisualBasic.Core/.../FileIO/FileSystem.vb`: `ShellDelete`(L1664〜)、`ShellFileOperation`(L1712〜1722)、`DeleteFileInternal`(L1218〜1240) | `ShellDelete` は、`SendToRecycleBin` のときだけ `FOF_ALLOWUNDO` を加えて `SHFileOperation` を呼ぶ。結果は全体の戻り値と `fAnyOperationsAborted` のみ。中断時は `onUserCancel` が `ThrowException` でなければ例外を出さずに戻る | 一致 | **PLAN にない重要な点**: UI 指定が `NoUI`、または `Environment.UserInteractive` が偽のとき、`recycle` が `SendToRecycleBin` でも `IO.File.Delete`(完全削除)が呼ばれる(`DeleteFileInternal`)。ごみ箱を要求しながら無通知で完全削除になる経路が、この API には存在する | ◎ |

#### B. パス・reparse・ID

| ID | 確認対象の主張 | 文書名 | 節・API・該当箇所 | 保証範囲 | PLAN と一致 | 差分 | 区分 |
|---|---|---|---|---|---|---|---|
| R-B3 | FAT では `nNumberOfLinks` が常に 1 | 「BY_HANDLE_FILE_INFORMATION structure (fileapi.h)」 | `nNumberOfLinks` | FAT では常に 1 | 一致 | K-10「FAT ではハードリンクを作れない」は、この文書の範囲を超える。文書は「リンク数が常に 1」としか述べていない | ◎(R-B3 の範囲)。K-10 の「作れない」は × 未確認 |
| R-B6 | `FILE_ID_INFO` で同一ボリューム内のファイルを識別。ReFS の 64bit は一意でない | 「FILE_ID_INFO structure (winbase.h)」「BY_HANDLE_FILE_INFORMATION」 | `VolumeSerialNumber`、`FileId`、`nFileIndexHigh/Low` の説明 | ボリューム通し番号と 128bit ID の組み合わせで、1台のコンピューター上のファイルを一意に識別でき、2つのハンドルが同一ファイルかをこの組み合わせで比較する。64bit の ID は ReFS で一意とは限らない | 一致 | (1) FILE_ID_INFO のページの「最小サポートクライアント」が「None supported」(サーバーは Server 2012)と記載されている。クライアント Windows での対応は、この文書からは読めない。(2) **PLAN にない点**: FAT の ID は、デフラグや、より長い名前への変更で変わりうる(BY_HANDLE_FILE_INFORMATION)。FAT 上では ID 不一致が偽陽性になりうる(安全側) | ◎。ただし (1) は未確認の注記、(2) は人間判断の材料 |
| R-B8 | ファイル ID は削除後に再利用されうる。時間をまたぐ一意性は前提にしない | 「BY_HANDLE_FILE_INFORMATION structure」 | ファイル ID の説明 | 「ファイル ID は時間をまたいで一意とは限らない。ファイルシステムは再利用してよい」 | 一致 | 「[推測・設計]」から昇格できる | ◎ |
| R-B10 | 長いパスには、レジストリ値と manifest の `longPathAware` の両方が必要 | 「Maximum Path Length Limitation」 | Enable Long Paths ほか | `LongPathsEnabled`=1(Windows 10 1607 以降)と、manifest の `longPathAware` の両方が必要。API は `\\?\` で最大約 32,767 文字。Shell UI が解釈できないパスを作れる場合がある、との記載もある | 一致 | Shell(`SHCreateItemFromParsingName`)との関係は記載なし(R-B9、R-B11 は実機) | ◎ |
| R-B14 | 親ディレクトリのハンドルを起点に単一成分を開く手段(`NtCreateFile` の `RootDirectory`)の、CsWin32 での生成可否、Win32 アプリからの利用条件、reparse 非追従の挙動 | 「NtCreateFile function (winternl.h)」、CsWin32 の README と Getting Started | NtCreateFile: `ObjectAttributes.RootDirectory`、`ObjectName`、`Attributes`、`CreateOptions`(`FILE_OPEN_REPARSE_POINT`、`FILE_DIRECTORY_FILE`)、Remarks。CsWin32: NativeMethods.txt、WDK メタデータ | `RootDirectory` が非 NULL なら `ObjectName` はそのディレクトリからの相対名。`RootDirectory` は「先行する `NtCreateFile` の呼び出しで得たハンドル」。`OBJ_CASE_INSENSITIVE` は「大文字小文字を無視し、完全一致検索にしない」。`FILE_OPEN_REPARSE_POINT` は通常の reparse 処理を迂回してその reparse point を直接開く。`NtCreateFile` は `STATUS_REPARSE` を返さない。ヘッダーは winternl.h、ntdll.lib / ntdll.dll。`LoadLibrary` / `GetProcAddress` で動的リンクもできる。CsWin32 は WDK メタデータを別パッケージから取り込める | 部分一致 | (1) **`FILE_DIRECTORY_FILE` を指定する場合に併用できる `CreateOptions` は、文書では限定され(同期 IO、`WRITE_THROUGH`、`OPEN_FOR_BACKUP_INTENT`、`OPEN_BY_FILE_ID`)、`FILE_OPEN_REPARSE_POINT` は含まれない**。「ディレクトリを `FILE_DIRECTORY_FILE` + `FILE_OPEN_REPARSE_POINT` で開く」併用は、この文書では保証されない。(2) `RootDirectory` に渡せるのは「先行する `NtCreateFile` で得たハンドル」とされ、`CreateFile` や `ReOpenFile` で得たハンドルが使えるかは記載なし。(3) CsWin32 が `NtCreateFile` を生成するかは確認できていない。WDK メタデータのパッケージ名が README(`Microsoft.Windows.WDK.Win32Metadata`)と NuGet ページ(`Microsoft.Windows.WDK.WDKMetadata`)で異なる | ○(利用条件・相対名・reparse 非追従の記述は ◎)。生成可否・併用・ハンドルの互換は × 未確認(要実機・要ビルド) |
| R-B15 | 大文字小文字区別ディレクトリでの、ハンドル相対オープンの名前照合 | 「NtCreateFile function (winternl.h)」 | `ObjectAttributes.Attributes`(`OBJ_CASE_INSENSITIVE`) | `OBJ_CASE_INSENSITIVE` を付けると大文字小文字を無視し、付けない場合は「完全一致検索」になると読める記述。大小区別ディレクトリでの挙動は記載なし | — | **S-06(決定: 暫定 A「FS の名前解決に従う」)の前提に関わる。** `OBJ_CASE_INSENSITIVE` を付けない場合、大小無視のディレクトリでも ZIP の `foo` がディスク上の `Foo` に一致しない(`MISSING` になる)可能性がある | × 未確認(要実機)。ただしフラグの意味は文書で確認 |
| R-B16 | `ReOpenFile` で同一オブジェクトを別アクセス権で開き直せるか(名前の再解決なしでのアクセス権昇格) | 「ReOpenFile function (winbase.h)」 | `hOriginalFile`、`dwDesiredAccess`、`dwShareMode`、`dwFlagsAndAttributes` | 指定したファイルシステムオブジェクトを、別のアクセス権・共有モード・フラグで開き直す。`hOriginalFile` は `CreateFile` で作られたもの。前に開いたハンドルが残っている間は、その共有モードと衝突するアクセスは要求できない。`FILE_FLAG_OPEN_REPARSE_POINT` を指定できる。ディレクトリは `FILE_FLAG_BACKUP_SEMANTICS` | 部分一致 | (1) **名前を再解決しないこと(同一オブジェクトを直接開くこと)は文書に明記されていない。** PLAN の H-3 の前提が文書だけでは確認できない。(2) 元ハンドルのアクセス権が昇格を制限するかは記載なし。(3) `hOriginalFile` は「`CreateFile` で作られたもの」とされ、`NtCreateFile` で得たハンドル(R-B14)との組み合わせは記載なし | × 未確認(要実機)。API の存在と、衝突の制約は ◎ |
| R-B17 | `FILE_DISPOSITION_POSIX_SEMANTICS` の対応ファイルシステム・OS 版、および名前が名前空間から消えるタイミング | 「FILE_DISPOSITION_INFORMATION_EX structure (ntddk.h)」(WDK DDI) | Flags と Remarks | POSIX セマンティクスなし: 削除マークされたファイルは、すべてのハンドルが閉じられ、リンク数が 0 になるまで実際には消えない。POSIX セマンティクスあり: POSIX 削除ハンドルが閉じられるとすぐに、リンクが見える名前空間から消える(他の既存ハンドルは最後まで開ける)。`FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE` は「読み取り専用ファイルを削除できるようにする」。zh-CN 版には、`STATUS_CANNOT_DELETE` は読み取り専用、または既存のマップビューがあることを示す、との記載がある | 部分一致 | (1) 対応ファイルシステムと OS 版は、取得したページに記載なし。ユーザーモードの `FILE_DISPOSITION_INFO_EX`(winbase)のページは取得できていない。(2) **PLAN の §10.2「削除マークと実際の消滅の違い」の前提は、文書で確認できた**。(3) `IGNORE_READONLY_ATTRIBUTE` が存在することを確認。SPEC §10.1 により PLAN は使わない | ○(セマンティクスは ◎)。対応 FS/OS 版は × 未確認 |

#### C. クラウド placeholder

| ID | 確認対象の主張 | 文書名 | 節・API・該当箇所 | 保証範囲 | PLAN と一致 | 差分 | 区分 |
|---|---|---|---|---|---|---|---|
| R-C1 | 文書間で記述が食い違う | 「Build a Cloud Sync Engine that Supports Placeholder Files」、「RtlSetProcessPlaceholderCompatibilityMode」 | 前者: 「Compatibility with applications that use reparse points」。後者: Remarks | 前者: cloud files API は、同期エンジンと、メインイメージが `%systemroot%` 配下にあるプロセス以外には reparse point を**常に**隠す。reparse point を正しく扱うアプリは、`RtlSetProcessPlaceholderCompatibilityMode` またはスレッド版で公開させられる。後者: ほとんどのアプリは既定で**公開**を見る。互換性のため、Windows が一部のアプリには偽装を既定にする場合がある | 一致(食い違いが現在も存在) | 前者の文書内に、リンク先(`RtlSetThreadPlaceholderCompatibilityMode`)とは異なる関数名(`RtlSetThreadProcessPlaceholderCompatibilityMode`)の記載がある | ◎(食い違いの存在を確認)。どちらの既定が実際かは × 未確認(要実機)。**明示的に公開を指定する方法が両文書で記載されている**点は一致 |
| R-C2 | `RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)` で公開モードにできる(Windows 10 1803 以降)。戻り値で失敗を判定できるか | 「RtlSetProcessPlaceholderCompatibilityMode function (ntifs.h)」(WDK DDI) | Return value、Remarks、Requirements | 現プロセスのモードを設定し、**以前のモードを返す。失敗は負の値**(`PHCM_ERROR_INVALID_PARAMETER`=−1、`PHCM_ERROR_NO_TEB`=−2)。値は 0(既定)、1(偽装)、2(公開)。最小クライアントは Windows 10 1803 | 一致 | **PLAN にない点**: スレッド版の `RtlSetThreadPlaceholderCompatibilityMode`(Windows 10 1709)が存在する。この関数は WDK DDI の文書であり、ユーザーモードのアプリから使うことは cfapi の文書が示す | ◎(戻り値の判定を含む) |
| R-C4 | 属性のみのオープン(`FILE_READ_ATTRIBUTES`、reparse 非追従)でハイドレーションが起きないか。`FILE_ATTRIBUTE_RECALL_ON_OPEN` を持つ対象を開く場合を含む | 「Build a Cloud Sync Engine…」「File Attribute Constants」 | Hydration Policies。`FILE_ATTRIBUTE_RECALL_ON_OPEN` | 記載なし。参考: ハイドレーションポリシーは「ファイルを開く時点」で、アプリの方針とプロバイダーの方針の厳しい方に決まる。placeholder は通常の利用で自動的にハイドレートされる。`FILE_ATTRIBUTE_RECALL_ON_OPEN` は「ディレクトリ列挙クラスにのみ現れる」属性で、「ローカルに物理的な表現がなく、項目は仮想である」 | — | **PLAN の §4.5.3(対象を開いてから属性を見る順序)への影響**: `RECALL_ON_OPEN` が「ディレクトリ列挙にのみ現れる」なら、対象を開いてからの属性取得では見えない可能性がある。親ディレクトリの列挙で属性を取得する設計(`CfGetPlaceholderStateFromFindData` 等)の検討が要る可能性がある。これは推測 | × 未確認(要実機)のまま |
| R-C5 | クラウドフィルタ(cldflt)は NTFS のみ対応 | 「Build a Cloud Sync Engine…」 | Cloud files API architecture | 「`cldflt.sys` は現在 NTFS ボリュームのみ対応する。NTFS 固有の機能に依存するため」(ページ更新日 2025-11-18。「現在」という限定つき) | 一致 | なし | ◎ |
| R-C6 | 識別機構の候補: 公開モード+属性+reparse タグ。補助として `CfGetPlaceholderStateFromAttributeTag` | 「CfGetPlaceholderStateFromAttributeTag function (cfapi.h)」「CF_PLACEHOLDER_STATE enumeration」「File Attribute Constants」 | Remarks、Return value、各属性の説明 | `FileAttributes` と `ReparseTag` から状態を返す。入力は「ディレクトリを列挙するか、`FileAttributeTagInfo` を直接問い合わせて」得る。状態: `PLACEHOLDER`、`SYNC_ROOT`、`ESSENTIAL_PROP_PRESENT`、`IN_SYNC`、`PARTIAL`、`PARTIALLY_ON_DISK`、`INVALID`。最小クライアントは Windows 10 1709(cldapi.dll)。属性: `RECALL_ON_DATA_ACCESS`=「完全にはローカルにない」、`OFFLINE`=「データがオフラインストレージへ移された」、`PINNED` / `UNPINNED`=「ローカルに保持する/しないというユーザーの意図(階層型ストレージ管理向け)」 | 一致 | (1) `PINNED` / `UNPINNED` は「意図」であり、ハイドレーション状態ではない。「オンライン専用・ローカル・常に保持」の3状態と属性の対応は、文書だけでは決まらない。(2) cfapi を使わないプロバイダーの対象を識別できるかは記載なし | ○(機構と属性の意味は ◎)。3状態の識別範囲は × 未確認(要実機。M-07、M-08) |

#### D. ZIP(dotnet/runtime `v10.0.12` タグ)

| ID | 確認対象の主張 | 該当箇所(`src/libraries/System.IO.Compression/src/System/IO/Compression/`) | 保証範囲(ソースが述べている範囲) | PLAN と一致 | 差分 | 区分 |
|---|---|---|---|---|---|---|
| R-Z1 | `entryNameEncoding=null` では、UTF-8 フラグなしの名前を UTF-8 として解釈する | `ZipArchiveEntry.cs` `DecodeEntryString`(L374〜382) | 汎用ビット 11 が立っていれば UTF-8。立っていなければ、アーカイブの `EntryNameAndCommentEncoding`、それが null なら UTF-8 | 一致 | なし。観測の根拠が、ブランチからタグに置き換わった | ◎ |
| R-Z4 | 読み出し時に CRC を検証しない。Stored で宣言 Uncompressed < Compressed のとき宣言を超えて返す | `ZipArchive.cs`、`ZipHelper.cs`、`ZipArchiveEntry.cs`、`ZipCustomStreams.cs`(`SubReadStream`) | 読み出し経路に CRC 検証は見当たらない(CRC は書き込み・ヘッダー生成側にのみ登場。検索は3ファイル)。Stored は、圧縮サイズで区切った `SubReadStream` をそのまま返し、宣言された展開サイズでは制限しない(`GetDataDecompressor`、L743〜) | 一致 | ソースによる確認。実際の挙動(例外なしの短い出力)は旧スパイクの観測であり、今回再現していない | ○(ソース整合)。挙動は [過去実機] のまま |
| R-Z5 | Deflate で宣言サイズを超える出力を黙って切り詰める | `DeflateZLib/Inflater.cs` `InflateVerified`(L93〜108)、`ZipArchiveEntry.cs`(L749) | 宣言サイズ(`uncompressedSize`)が指定されると、出力はその分で打ち切られ、到達すると `_finished = true`、入力を破棄する。エラーは出さない | 一致 | なし | ◎ |
| R-Z6 | `ZipArchive` は CD を全件読み込む。件数・メタデータ量を事前に検査して止められない | `ZipArchive.cs`(L140〜162、L466〜476、L503〜545)、`ZipArchiveEntry.cs`(L87) | Read モードのコンストラクターは EOCD の読み取りのみ。`Entries` / `GetEntry` の最初のアクセス時に `ReadCentralDirectory` が全エントリを `_entries` へ追加する。名前のデコードは各エントリの構築時に行う。件数の不一致のみ `InvalidDataException`。件数・総量・名前長の上限を設ける拡張点は見当たらない | 一致 | CD の読み込みは、コンストラクターではなく、最初のアクセス時(遅延) | ◎(ソースの範囲で) |
| R-Z7 | 公開 `DeflateStream` の入力 EOF が、Deflate の正常終端を保証しない経路がある | `DeflateZLib/DeflateStream.cs` `ReadCore`(L292〜352)、L1122〜1123、L954 | 基底ストリームが EOF(0 バイト)を返し、`Inflater` がまだ入力を要求している場合、`s_useStrictValidation` が真でなければ**例外を出さずに `break` して、その時点までの出力(0 バイトを含む)を返す**。`s_useStrictValidation` は、`AppContext` スイッチ `System.IO.Compression.UseStrictValidation`(**既定は false**)から静的に読む。真の場合は、`!Finished && NonEmptyInput` のとき `TruncatedData` の `InvalidDataException` を投げる | 一致 | **PLAN にない点**: (1) 厳密検証の opt-in スイッチが存在する。(2) ただし、条件に `NonEmptyInput()` が含まれ、「最終ブロックの終端を見たこと」を一般に保証する仕組みとは述べていない。(3) 静的な読み取りのため、設定は `DeflateStream` の初回使用前でなければならないと読めるが、これは推測 | ◎(既定の挙動)。スイッチの網羅性・Z-A での使用可否は × 未確認(要実機・P0-6) |

#### E. APPNOTE(R-Z13): PKWARE APPNOTE.TXT Version 6.3.10(2022-11-01、STATUS: FINAL)

取得元: `https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT`(PKWARE の参照コピー。公式ページは `www.pkware.com/appnote` と文書内 1.5.2 に記載)。確認した範囲: 1.0〜7.1(Strong Encryption の概説まで)。**Appendix(特に D「Unicode / EFS」と E「AE-x」)と 7.2 以降は読んでいない。**

| 項目 | 節 | 文書が述べていること(要約) | 述べていないこと |
|---|---|---|---|
| Stored | 4.1.7、4.4.5 | 「stored」はファイルを圧縮せずに格納すること。方式 0 | 非暗号化の Stored で「圧縮サイズ = 展開サイズ」が成り立つ、という規則は**明記されていない**。「圧縮せずにコピー」から導けるが、規則としての記載はない |
| 圧縮サイズに暗号化ヘッダーが含まれる | 4.4.8、6.1.3 | 復号ヘッダーがある場合、圧縮サイズにはそのバイト数が含まれる。従来型暗号化では、各ファイルのデータ領域の先頭に 12 バイトのヘッダーがある | → **暗号化された Stored では、圧縮サイズ ≠ 展開サイズが正常**。PLAN の F19 は「非暗号化」に限定しているので、この点とは整合 |
| bit 3 とサイズ・CRC | 4.4.4 bit 3、4.4.7、4.4.8 / 4.4.9、4.3.9 | bit 3 が立つと、LH の `crc-32`・圧縮サイズ・展開サイズは 0 になり、**正しい値はデータ記述子と CD に置かれる** | bit 3 が立つ場合の LH のサイズ欄は 0 であり、CD との不一致の対象にならない |
| データ記述子 | 4.3.9.1〜4.3.9.4 | bit 3 が立てば**必ず存在する(MUST)**。圧縮データの直後に、バイト境界で続く。ZIP64 では各サイズが 8 バイト。署名 0x08074b50 は慣習的に使われ、**署名の有無の両方がありうる**ので、リーダーは両方を考慮すべき(SHOULD) | ZIP64 かどうかを、データ記述子単独で判別する規則は記載なし(LH の extra field と CD で決まる) |
| LH と CD の優先関係 | 4.3.2、4.4.16 | 各 LH には、対応する CD レコードが必ずある。CD の「LH の相対オフセット」は、LH が「あるべき」位置(SHOULD)。CD の順序は、ファイルの出現順と一致しなくてよい(4.4.1.3)。ファイルは任意の順序でよい(4.3.3) | **bit 3 なしの場合に、LH と CD でサイズ・CRC・名前・方式が食い違ったとき、どちらを優先するかの規則は記載なし** |
| データの位置 | 4.3.8 | ファイルデータは LH の直後に置くべき(SHOULD) | エントリ間の未参照バイト(隙間)を禁じる、または定義する規則は記載なし |
| 先頭・末尾の余剰データ | 4.1.9、4.3.1、4.3.16、4.4.25 | 自己展開形式は、ZIP 内に展開コードを含む。EOCD は 1 つだけ(MUST)。EOCD の末尾にコメント長とコメントがある | **EOCD の宣言コメント長を超える末尾のバイト、先頭の余剰バイトを、どう扱うかの規則は記載なし。PLAN §5.1.1 の判別規則は APPNOTE から導いたものではなく、PLAN の設計である** |
| external attributes | 4.4.15、4.4.2 | 「ホストシステム依存」。`version made by` の上位バイトがホストを示す(0=MS-DOS/FAT、3=UNIX、10=Windows NTFS など)。MS-DOS では下位バイトがディレクトリ属性 | UNIX の mode が上位 16bit に入る規則や、symlink の `0xA000` は**記載なし**。**S-10 の「特殊属性の集合」は、APPNOTE からは決まらない**。UNIX extra field(4.5.7、0x000d)は、リンク先名とデバイス番号を持つ |
| 汎用ビット 11(EFS)と 13 | 4.4.4 | bit 11 が立つと、名前とコメントは UTF-8(MUST)。bit 13 は、CD を暗号化する際に LH の値をマスクする | UTF-8 でない場合の既定の文字コードは、読んだ範囲では記載なし(Appendix D 未読) |
| Zip64 | 4.3.14〜4.3.15、4.4.1.4、4.5.3 | EOCD の各フィールドが `0xFFFF` / `0xFFFFFFFF` なら、Zip64 の対応する値を使う | — |
| 分割・複数ディスク | 4.3.3、4.4.1.5、4.4.19〜4.4.20 | ディスク番号が EOCD にある。EOCD と Zip64 EOCD ロケーターは同じディスクにある | — |
| CRC | 4.1.5 | 各ファイルに CRC32 を設けなければならない(MUST) | — |

**PLAN との整合(確認できたもの)**: F10(bit 3 のときサイズ・CRC を LH/CD の比較から外す)と F12(データ記述子の署名の有無・32/64bit)は、上の bit 3 とデータ記述子の記述と矛盾しない。F19 の条件に「暗号化なし」が含まれているのは、4.4.8 / 6.1.3 と整合する。

#### F. .NET(R-Z14)

| 主張 | 文書名 | 該当箇所 | 保証範囲 | PLAN と一致 | 区分 |
|---|---|---|---|---|---|
| .NET 10 が現行の LTS | 「.NET and .NET Core official support policy」(dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core。ページ更新日 2026-09-08) | Supported versions の表 | .NET 10: 2025-11-11 リリース、LTS、サポートフェーズは Active、サポート終了 2028-11-14。.NET 9 と .NET 8 は STS と LTS だが、ともに Maintenance で 2026-11-10 終了。.NET 11 RC1 が go-live(2026-09-08)。偶数版は LTS、奇数版は STS(Release types) | 一致 | ◎ |
| ランタイム 10.0.12 が .NET 10 の最新パッチ | 同上 | 「Latest patch version」 | .NET 10 の最新パッチは 10.0.12、パッチのリリース日は 2026-09-08。**時点依存**: パッチは毎月第2火曜日(Patch Tuesday)にリリースされうる(同ページ Servicing)。次の Patch Tuesday は 2026-10-13 | 一致(2026-10-01 時点) | ◎(時点つき)。2026-10-13 以降は再確認が必要 |
| SDK 10.0.401 がランタイム 10.0.12 を含む | 「Download .NET 10.0」(dotnet.microsoft.com/en-us/download/dotnet/latest。日本語版) | SDK 10.0.401 の欄 | SDK 10.0.401(2026-09-08 リリース)の下に、.NET Runtime 10.0.12、ASP.NET Core Runtime 10.0.12、.NET Desktop Runtime 10.0.12 が併記される。日本語版には「ランタイムは SDK にも含まれています」とある。SDK 10.0.112 も、同じランタイム 10.0.12 と併記される | 一致 | ◎ |
| (H-12 関連)self-contained はランタイムの更新責任がアプリ側にある | 同サポートポリシー | Automatic patching on Windows operating system | フレームワーク依存の配布は Microsoft Update によるランタイム更新の恩恵を受ける。self-contained は変化なし(アプリがランタイムを最新に保つ責任を負う) | 一致(K-16 を裏づける) | ◎ |

注: 検索結果に、10.0.5(2026-03)から 10.0.11(2026-08)までの古い版のスナップショットが混在していた。上の結果は、直接取得した最新のページ(更新日 2026-09-08)に基づく。

---

## 文書分割の記録

- 実施日: 2026-10-01。範囲は文書構造のみ。SPEC、設計判断、ID、Phase/task の割当、ゲート条件、テスト期待値は変更していない。P0-4 以降の調査・実験は行っていない
- 方法: 分割前の `PLAN.md`(1384 行)と `P0-3_results.md`(89 行)を、行範囲の切り出しスクリプトで5文書へ再配置した(節番号は維持)。本文の変更は、`docs/P0-3_results.md` への参照8行に移設先の注記を加えたことのみ。追加したのは、索引・参照ガイド・移動先の案内・各分冊の冒頭説明、P0-3 確認状態の一覧(原文の「区分」欄の転記)、本節
- 機械確認(文書内の整合の確認であり、一次資料・実機の確認ではない):
  - ID 集合(H、S、K、R、V-、P0-4 の V1〜V8、M、X、O、CR、RV、A〜F、G、P、F/L/U、Z、T、C-a〜C-c、#): 分割前後で一致。欠落・新規なし
  - 分割前の非空行の欠落: 参照の注記を加えた8行のみ(各行は注記つきで存在)
  - SPEC §17 #1〜#54: `PLAN_TESTS.md` §11.1 に各1行、原文と完全一致。X-01〜X-26 の行と §11.2.1 の割当表: 原文と完全一致
  - §10(ゲート、依存、タスク)、§4.1.1、§4.7、§13.2: 参照の注記を加えた P0-3 の行を除き、`PLAN.md` に原文のまま存在
  - P0-3 記録の全行: 本書「P0-3 調査記録」に存在(見出しレベルのみ変更)
  - 分割前の節見出し: すべて5文書のいずれかに存在
- `docs/P0-3_results.md` は削除していない(削除は人間が行う)
