# 未確認・未解決事項

役割: 現在の不確実性と限定付き観測の更新先。残課題・リリース判断、該当する安全性変更のときに読む。完了したら知識を担当正本へ反映し、行を閉じる。

[手動](#manual-status) / [手順保留](#procedure-review) / [観測範囲](#observations) / [対象外](#out-of-scope) / [リリース・運用](#release-decisions)

<a id="manual-status"></a>
## 手動確認の状態

現行手順の実施状態の唯一の更新先は本書。旧方式の合格は現行手順の合格に転用しない。未実施は前提不成立とも失敗とも異なる。

| 項目 | 状態・影響 | 次の確認 | 参照 |
|---|---|---|---|
| M01・M02・M03・M04 | 未実施。実端末の確認入力と確認待ちCtrl+C | 各項目の独立fixtureで実施 | [確認入力](MANUAL_TESTS.md#m01) |
| M05 | 未実施。進捗と逐次行の見え方 | stdout/stderr同一端末とリダイレクトで確認 | [M05](MANUAL_TESTS.md#m05) |
| M06 (932) / M06 (65001) | 両方未実施。フォントとコードページの表示 | それぞれの端末環境・文字化けと字形欠落を分けて記録 | [M06](MANUAL_TESTS.md#m06) |
| M07 | 未実施。確認待ちの変更を現在状態で判定 | 別ウィンドウで変更してから確認 | [M07](MANUAL_TESTS.md#m07) |
| M08 | 未実施。PTY X28は機能部分の回帰で視認性を証明しない | 実端末で警告の折り返し・位置を確認 | [M08](MANUAL_TESTS.md#m08) |
| M09 | 未実施。設計は同時openに依存しないが必須確認から除外しない | FileStream近似と製品構成の差を記録。低優先の調査 | [M09](MANUAL_TESTS.md#m09) |
| M10 | 未実施・手順レビュー待ち | 下表の測定条件を先に決める | [M10](MANUAL_TESTS.md#m10) |
| M11 | 未実施・手順未確立 | マップを保持しファイルハンドルを閉じる別プロセスhelperを設計 | [M11](MANUAL_TESTS.md#m11) |
| M12 (a) / M12 (b) | 両方未実施。製品と同じ構成ではない補助観測 | 祖先改名とread-only openを分けて実施 | [M12](MANUAL_TESTS.md#m12) |
| L18 / M13 | 未実測・手順レビュー待ち。BOM拒否テストと端末保存形式は別 | 下表のfixture分離・続行条件を先に決める | [M13](MANUAL_TESTS.md#m13) |

<a id="procedure-review"></a>
## 実施前の手順保留

| 問題・影響 | 分かっている範囲 | 次の確認・判断 | 根拠 |
|---|---|---|---|
| M10の差1件許容 | 削除指示後・close前を外から特定できない。表示と実削除の差は測定系にも依存し、差1件を許す根拠は未確定 | 表示の保存・中断の観測方法と合否基準をレビュー。許容を緩めず、それまでは現行手順で合格判定しない | [M10](MANUAL_TESTS.md#m10)、[指示後STOP](spec/filesystem.md#failure-boundary) |
| M13の「削除を伴う実行1回」と手順6の2コマンド | 第1入力がUTF-16として拒否される推定に依存。L02/X21はBOM拒否を確認するがPowerShell実測ではない | 保存形式を確認し、推定が外れた場合の独立fixtureと続行条件を決める。第2コマンドへ無条件に続行しない | [M13](MANUAL_TESTS.md#m13)、[entries](spec/cli.md#entries) |
| M11の書き込み可能マップ | 共有モードの判定に参加しない経路を排除できるか未確認 | 再現方法を設計し、書き換えが可能かと最終確認の限界を実測 | [FS限界](spec/filesystem.md#limitations) |

<a id="observations"></a>
## 限定付き観測と未確認範囲

S26/S34/S37/S38の実測は2026-10-03、Windows 11 10.0.26300、NTFS、SDK 10.0.401。旧PoCは2026-10-02、同Windows・NTFS、.NET 10.0.12。環境・操作時点を越えた保証に拡張しない。

| 問題・影響 | 分かっている範囲 | 次の確認 | 根拠 |
|---|---|---|---|
| 祖先改名 S26 | Strict、同一プロセス別ハンドルから対象の親を改名すると5で失敗、対象は削除。別プロセス・全祖先・全モードを証明しない | Windows/構成変更時とM12(a)で未確認範囲を確認 | [Win S26](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)、[共有モードの理由](RATIONALE.md#sharing-limits) |
| read-only open S37 | H1で付与後のopenは成功、Strict/Fastで属性による非削除。M12(b)は未実施 | 近似手順と別環境で確認 | [Win S37](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs) |
| 属性 S38 | archive/normal/hidden/not-content-indexed/read-only/system/temporary/offline/compressed/sparse/dir/junctionの12項目一致。一般保証ではない | EFSとファイルsymlink、環境差を確認 | [Win S38](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)、[事前判定](spec/filesystem.md#special-files) |
| ACL S34 | 実行前の対象DELETE拒否は親DELETE_CHILDによりStrict/Fastで削除。比較中H3での変更はChangeTime不一致STOP。H3はStrictのみ | 全時点・全モードに外挿せず必要な競合だけ再現 | [Win S34](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)、[ACL理由](RATIONALE.md#acl) |
| hardlink親ID | 非削除例外は仕様・Core/Win S20で確定。USNがどのリンクの親を返すかの環境差の理由は未確認 | API/環境変更時にリンク別の値を調査。通常候補の親照合を省かない | [例外](spec/filesystem.md#hardlink-parent-id)、[理由](RATIONALE.md#hardlink-parent-id) |
| ファイルsymlink差替え | 旧PoCは作成特権がなく前提不成立、実挙動未確認。判定不能を成立扱いしない | 作成可能な隔離fixtureでS17/S19系を確認 | [失敗境界](spec/filesystem.md#failure-boundary) |
| EFS削除 | 読み取り可能属性として許可するが実削除未確認、S38にも未確認 | EFSを作成できるfixtureでT08と削除を確認 | [属性](spec/filesystem.md#special-files) |
| クラウドplaceholder | 旧PoCに再現環境なし、未確認。同期意味論は製品対象外 | 必要な環境で非削除側の判定を確認 | [対象](SPEC.md#scope) |
| 列挙直後の改名 | 列挙中の改名による見落とし/旧名と新名の両方は旧PoCで観測。見つけた直後の改名→open失敗/ID不一致という組合せは推定 | 必要時に列挙→open間へ変更を注入 | [実名確認](spec/filesystem.md#real-names) |
| CI権限 | 旧CI実績はランナー管理者権限の有無を確認していない | 権限依存ケースはログの前提不成立と環境を別に報告 | [テスト原則](TESTING.md#principles) |

<a id="out-of-scope"></a>
## 対象外との区別

非NTFS・USN機能のないFSは現行対象外。必須検証の未実施と混同しない。クラウド同期の意味論も対象外だが、placeholderを安全に非削除へ倒す確認の未実施は上表に残す。

<a id="release-decisions"></a>
## リリース・運用で残る判断

| 問題・影響 | 分かっている範囲 | 次の確認・判断 | 根拠 |
|---|---|---|---|
| リリース番号 | 旧READMEに「新しいバージョン番号はリリース時に決める」と明示。現行の番号設定を文書再編で変更しない | リリース担当が番号・移行案内を決定 | `e618713:README.md`「実装状況」、[project](../src/Unextract.Cli/Unextract.Cli.csproj) |
| CIのNode.js注釈 | 2026-10-02の旧CI記録にNode.js 20廃止予定の注釈と対応見送りがある。現在のCIでの再現・対応要否は未確認 | 次のCI実行で注釈と依存actionsを確認し、必要なら別のCI変更として扱う | `606d89c:docs/PLAN_VALIDATION.md`「CI の初回実行」、[workflow](../.github/workflows/ci.yml) |
