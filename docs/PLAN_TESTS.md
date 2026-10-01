# unextract PLAN — テスト計画(SPEC §17 #1〜#54 と追加テスト)

- 本書は `docs/PLAN.md` の分冊である。文書構成と節の所在は `PLAN.md`「文書構成と参照ガイド」。仕様上の正は `docs/SPEC.md` v4。本書は SPEC を変更しない
- 内容は、分割前の `PLAN.md`(第4改訂、2026-10-01)の §11 を、節番号を維持して原文のまま移したもの
- テストを完了条件とするタスク、その依存とゲートの正は `PLAN.md` §10.2。手動確認(M-xx)は `PLAN_VALIDATION.md` §6

---

## 11. テスト計画

種別: **Core** = Fake の FS / Deleter での単体(OS 非依存)。**Win** = 実 Windows FS での統合。**手動** = §6 の手動確認。
「Fake の保証」は判定ロジックが正しいことだけを示し、実 Windows の API 挙動は保証しない。「実 Win の保証」は API とファイルシステムの実挙動を含む。

### 11.1 SPEC §17 #1〜#54 対応表

| # | 対象 | 種別 | 必要なヘルパー | CI | Fake でのみ成立する保証 / 実 Win で確認する保証 |
|---|---|---|---|---|---|
| 1 | 完全一致 → MATCHED | Core+Win | RawZipBuilder、FakeFS、TempDir | 可 | Fake: 判定 / Win: 単一成分解決と実ハンドルからの読み取り |
| 2 | 1byte 変更 → MODIFIED | Core+Win | 同上 | 可 | 同上 |
| 3 | サイズ違い → MODIFIED | Core | 同上(展開呼び出しの記録) | 可 | Fake で十分(展開しないことはロジックの性質) |
| 4 | target に無い → MISSING | Core+Win | 同上 | 可 | Win: 子が存在しない場合の戻り値を MISSING に写像すること |
| 5 | 非対応・不一致ファイルを削除しない | Core+Win | RecordingDeleter、TempDir | 可 | P1-8: Fake で Deleter に渡らない / P3-4・P3-5: 実削除可能な経路で Win の実ファイルが残る |
| 6 | 0 バイト(正常 MATCHED、CRC 不正 ERROR) | Core | RawZipBuilder(CRC 改ざん、空 Deflate、非最終ブロックのみ) | 可 | Core で十分 |
| 7 | 暗号化・CRC 不整合・終端欠落 → 当該のみ ERROR、他の MATCHED は削除 | Core+Win | RawZipBuilder、RecordingDeleter | 可(Phase 1 は Core/Fake のみ。Win の削除部分は Phase 3、G3 の結果に依存。§11.4) | P1-4: Fake で entry-local の独立性 / P3-4・P3-5: Win で当該 ERROR が残り、他の MATCHED が実削除される |
| 8 | 展開出力が宣言超過 → ERROR | Core | RawZipBuilder(Deflate/Stored 詐称) | 可 | Core で十分 |
| 9 | 巨大宣言・サイズ不一致 → MODIFIED、展開せず、メモリ増加なし | Core | RawZipBuilder(Zip64 で 1 TiB 宣言、実データ KB 級) | 可 | 割り当て量の計測は実行環境依存のため閾値に余裕を持たせる |
| 10 | エントリ総数上限超過 → archive-fatal | Core+Win(CLI) | 上限注入、EOCD 件数詐称 | 可 | Win: 終了コード 3、target・ZIP 不変 |
| 11 | 空になったディレクトリ → 削除 | Core+Win | TempDir | 可(Win の削除手段は G3 依存。ディレクトリ削除自体は disposition) | Fake: 計画 / Win: ハンドル保持と削除順序 |
| 12 | ユーザーファイルが残る → 削除しない | Core+Win | TempDir | 可 | Win: 空でない場合の失敗を残すこと |
| 13 | 既存の空ディレクトリ・明示エントリ → 削除しない | Core+Win | RawZipBuilder(`d/`)、TempDir | 可 | |
| 14 | target を削除しない | Core+Win | TempDir | 可 | |
| 15 | `../` → target 外にアクセスしない | Core | FakeFS(アクセスログ) | 可 | Core で十分(FS に到達しないことはロジックの性質) |
| 16 | `a/../b` → UNSAFE_PATH | Core | RawZipBuilder | 可 | |
| 17 | 絶対パス等 → UNSAFE_PATH | Core | RawZipBuilder(生の名前バイト) | 可 | |
| 18 | 兄弟の接頭辞一致 → target 外 | Core+Win | TempDir(`x`、`x2`) | 可 | Fake: 文字列比較を使わない構造 / Win: 実解決 |
| 19 | 親が junction → SKIPPED | Win | FsFixture.CreateJunction | 可(junction は管理者不要 [過去実機]) | 実 Win のみ意味がある |
| 20 | symlink / reparse / ハードリンク → スキップ | Win(+手動 M-13) | FsFixture(Junction、HardLink、Symlink) | 一部(symlink は権限依存。CI ランナーの権限は [未確認]。権限がなければ理由を出してスキップ) | 実 Win のみ |
| 21 | AMBIGUOUS | Core | RawZipBuilder | 可 | |
| 22 | dry-run で target 不変 | Win | Snapshot(パス、サイズ、ハッシュ、更新日時) | 可 | 実 Win のみ。属性の比較は追加テスト X-14 |
| 23 | ZIP 不変 | Win | Snapshot(存在、ハッシュ、更新日時) | 可 | P2-5: dry-run で ZIP 自身を削除候補にしない / P3-4・P3-5: Recycle と `--delete-permanently` の各実削除経路の後も元 ZIP が存在し、ハッシュ・更新日時が不変(X-22) |
| 24 | 検証後の書き換え → 削除しない | Core+Win | 削除フェーズ前のフック | 可 | |
| 25 | 検証後の置換 → ID 確認で削除しない | Core+Win | フック(リネーム置換) | 可 | Fake: 判定 / Win: 実 ID |
| 26 | 読み取り不能・ロック中 → 削除しない | Win | FsFixture.LockExclusive、ACL(自作ファイルのみ) | 可 | P2-3: 実 Win の解決レイヤーで検査 / P3-4・P3-5: 実削除可能な経路でも対象が残る |
| 27 | ごみ箱不可・保証不可 → 既定は ERROR、`--delete-permanently` のみ削除 | Core+Win+手動 | ScriptedDeleter、診断手段 | 一部(本物の「ごみ箱不可」環境は CI で再現困難: ドライブ種別・ごみ箱設定の変更が必要。M-01〜M-06、M-16) | Fake: 「保証できない → ERROR」のロジック / 実 Win: 保証条件そのもの(G3)。`--delete-permanently` 部分は P3-5(SPEC どおり MVP に含める) |
| 28 | 非 TTY・`--yes` なし → 2 | Win | プロセス起動(stdin リダイレクト) | 可 | |
| 29 | 拒否 target(配下を含む)→ 3 | Core+Win | 拒否判定は注入パスで Core、解決は Win(システムディレクトリは読み取りなしで解決のみ) | 可 | |
| 30 | `%USERPROFILE%` 配下 → 許可 | Core+Win | 注入プロファイルパス / 実ディレクトリ | 可(ローカルはオプトイン) | 開発機のホームに書き込むため、ローカル実行は環境変数で明示した場合のみ |
| 31 | ハードリミット超過 → 展開前に ERROR | Core | 上限注入 | 可 | |
| 32 | 構造異常 → archive-fatal、不変 | Core+Win(CLI) | RawZipBuilder(F1〜F8) | 可 | Win: target・ZIP 不変 |
| 33 | 先頭 MATCHED、後続 CRC 不一致 → 先頭のみ削除、終了コード 1 | Core+Win | RawZipBuilder、RecordingDeleter | 可(Phase 1 は Core/Fake のみ。Win の削除部分は Phase 3、G3 依存。§11.4) | P1-4: Fake で削除対象として渡るのが先頭のみ / P3-4・P3-5: Win で先頭 MATCHED のみ実削除、後続 CRC 不一致は ERROR、終了コード 1 |
| 34 | 終端欠落(出力・CRC 一致)→ ERROR | Core | RawZipBuilder(最終ブロックを欠く Deflate。非最終 Stored ブロックで全内容を表現する等) | 可 | G1 の中心。採用方式の実装(公開 `DeflateStream` 等)そのものを通すこと |
| 35 | データ範囲の重なり → archive-fatal | Core | RawZipBuilder(F11) | 可 | |
| 36 | LH 位置異常・Zip64 矛盾・DD 境界不定 → archive-fatal | Core | RawZipBuilder(F9、F10、F12、F13) | 可 | |
| 37 | 受理範囲外 → unsupported | Core+Win(CLI) | RawZipBuilder(U1〜U4。SFX は先頭に無害なバイト列を付けた模擬) | 可 | |
| 38 | 独立性を確認できない異常 → archive-fatal | Core | 境界ケース fixture(F14、F15、F18) | 可 | |
| 39 | 名前付きストリーム付きファイル → SKIPPED | Core+Win | FsFixture.WriteAlternateStream | 可(CI ランナーのボリュームが NTFS であることが前提。[未確認]) | Fake: 判定 / Win: API の実挙動 |
| 40 | 名前付きストリーム付きディレクトリ → 削除しない | Core+Win | 同上 | 同上 | |
| 41 | 大小区別ディレクトリの `Foo`/`foo` | Win(+手動 M-09) | FsFixture.SetCaseSensitive | [未確認](CI で `setCaseSensitiveInfo` が使えるか) | 実 Win のみ |
| 42 | 親ディレクトリの差し替え → 別個体を開かない | Core+Win(+手動 M-11) | フック(rename、junction 化) | 可(junction 化は管理者不要の見込み) | Fake: 再解決しない構造 / Win: H-1 の実挙動 |
| 43 | 削除予定ディレクトリの判定、候補以外を列挙しない | Core+Win | FakeFS(列挙ログ) | 可 | Fake: 列挙範囲 / Win: 判定結果 |
| 44 | 削除開始後の未検出異常 → 内部安全性エラー、3 | Core | フック注入 | 可 | Fake のみ(実在の欠陥を前提とするため) |
| 45 | 空成分・`.` 成分 → UNSAFE_PATH、末尾区切り1個は受理 | Core | RawZipBuilder | 可 | |
| 46 | unsupported と archive-fatal の理由の区別 | Core+Win(CLI) | RawZipBuilder | 可 | 表示のスナップショット |
| 47 | target の最終成分が junction → 3 | Win | FsFixture.CreateJunction | 可 | |
| 48 | 祖先に junction、最終成分は通常 → 個体保持で処理。固定できなければ 3 | Win+Core | FsFixture、Fake(固定失敗の注入) | 可 | 「固定できない」経路は実 Win での再現手段が不明のため Fake で確認([未確認]) |
| 49 | 内容一致かつ読み取り専用 → ERROR(両モード) | Core+Win | FsFixture.SetReadOnly | 可 | Win: 実属性の取得 |
| 50 | 削除候補ディレクトリの同名別個体への差し替え → 削除しない | Core+Win(+手動 M-11) | フック | 可 | Fake: 判定 / Win: ハンドル保持の効果 |
| 51 | クラウド識別の初期化失敗 → 3 | Core(+手動 M-08) | ICloudStateProvider 注入 | 可(Core) | 実際の初期化失敗は CI で再現困難(再現手段が不明) |
| 52 | 1対象のクラウド状態取得失敗 → 当該 ERROR、他は処理 | Core | 注入 | 可 | Fake のみ |
| 53 | 完全削除と同一性不一致を別 reason、どちらも中止・3 | Core | ScriptedDeleter | 可 | Fake のみ。実 Win での完全削除の発生は M-01〜M-06、M-16 で観察。結果不明の `UnknownShellOutcome` は追加テスト X-20 |
| 54 | 不正 UTF-8 名 → UNSAFE_PATH | Core | RawZipBuilder | 可 | |

### 11.2 追加テスト(PLAN が導入した安全性の主張に対するもの。SPEC の番号外)

| ID | 主張 | 種別 | 内容 |
|---|---|---|---|
| X-01 | 抽象は複数成分のパスを受け取らない | Core(アーキテクチャテスト) | 公開 API に `string` パスを受ける解決メソッド・削除メソッドがないこと(例外は利用者入力の入口 `IFileSystemProbe.InitializeTarget` のみ)。`IFileDeleter`・`IDirectoryRemover` がノードと `PathComponent` だけを受け取ること |
| X-02 | 識別前に内容を読まない | Core | Fake で、クラウド・ストリーム・reparse の確認より前に `OpenContent` が呼ばれないこと |
| X-03 | 構造検証が全件完了してから削除フェーズへ遷移 | Core | 状態機械のテスト。後方エントリの LH 異常で削除が一度も呼ばれないこと |
| X-04 | Local Header・DD の全件検証 | Core | MISSING/MODIFIED のエントリの LH 異常でも archive-fatal |
| X-05 | メタデータ総量・名前長・深さの上限 | Core | 注入した小さい上限で archive-fatal |
| X-06 | 終端確認の偽陽性方向 | Core | 入力 EOF 到達時は終端未確認として ERROR(§4.1.1-2) |
| X-07 | Shell 結果の状態機械 | Core | ScriptedSink で Post 欠落、重複、Pre 中止、`GetAnyOperationsAborted`=True、結果取得失敗の各組み合わせ |
| X-08 | 「削除済み」は積極的確認時のみ | Core | Recycled 判定に保持ハンドル照合を要すること |
| X-09 | 関連項目の追加の中止 | Core+手動 M-12 | 依頼外の項目の Pre で E_ABORT、内部安全性エラー |
| X-10 | 読み取り専用の Shell 直前の再確認 | Core | Pre 時点の属性付与で中止 |
| X-11 | ディレクトリ削除は同一ハンドルで行い、子から親 | Core+Win | 子が削除保留のまま残る場合に親を削除しない |
| X-12 | 拒否リストは junction 経由の別名でも効く | Win | target を拒否対象を指す junction の配下として指定(システムディレクトリは読み取りのみ) |
| X-13 | 分類 ID と削除時 ID の不一致は ERROR | Core | |
| X-14 | dry-run で属性も不変 | Win | Snapshot に属性を追加 |
| X-15 | ZIP 側の特殊種別(S-10: 安全原則は確定) | Core | 根拠を確認した属性(O-14)を持つエントリが削除候補にならないこと。根拠を確認していない属性を推測で特殊種別として扱わないこと(認識集合の拡張に合わせて期待値を追加) |
| X-16 | CP437 の全 256 値の往復と、UTF-8 フラグ優先 | Core | |
| X-17 | 読み取り専用 + 内容不一致 → `MODIFIED`(`ERROR` にすり替えない)。読み取り専用 + 内容一致 → `ERROR`(両モード。#49 の補完) | Core+Win | 比較を省略しないこと。サイズ不一致の読み取り専用も `MODIFIED` |
| X-18 | §5.1.1 の判別規則: 切断 EOCD と末尾余剰の区別(F3/U3)、一定ずれの先頭余剰(U2)と未説明の隙間(F15)、fatal と unsupported の併発 | Core | RawZipBuilder。表示 reason が規則どおりに異なること(#46 の補完。S-15 確定)。表示に「規格違反」の文言を含まないこと |
| X-19 | Stored・非暗号化で、解釈に使う宣言圧縮サイズ ≠ 宣言展開サイズ → archive-fatal(F19、S-16 確定) | Core | MISSING/MODIFIED のエントリでも検出されること。bit 3 で LH のサイズ・CRC が 0 のものは F19 にしないこと(DD/CD の値で判定)。暗号化された Stored は F19 にしないこと |
| X-20 | Shell の結果が説明のつかない状態(Post 欠落・重複、結果取得失敗、説明のつかない `GetAnyOperationsAborted`)→ 元の位置にあるかを問わず `UnknownShellOutcome`、`PermanentDeletionOccurred` と区別、以降を中止し終了コード 3。説明のつく失敗(Post の失敗 hr、こちらの `E_ABORT`)で対象が元の位置にあれば当該 `ERROR` で継続 | Core | ScriptedSink、ScriptedDeleter。#53 の補完。S-05 決定済み。4 reason すべてが区別して表示・記録されること |
| X-21 | 同一個体への別名(S-18): 8.3 短縮名の別名、大文字小文字の別名などで、複数のエントリが同じ個体へ解決された場合、その個体を削除候補にしない。ID が異なることを別個体の証明として扱わない | Core+Win(+手動 M-17) | Fake: 同じ `NodeIdentity` を返す2エントリ、解決後の実名が要求名と異なるエントリ / Win: 8.3 名が生成されるボリュームでの実解決。CI の可否は [未確認](8.3 名生成の設定に依存)。分類名の期待値は S-18 の決定後に確定 |
| X-22 | ZIP 自身が target 内にあり、エントリが ZIP 自身へ解決される場合、ZIP を削除候補にしない(identity ベース) | Core+Win | P1-8(Fake)・P2-5(dry-run): 同一 object への解決を除外し、ZIP 自身を削除候補にしない。P3-4・P3-5(実削除統合): ZIP を target 内に置いた fixture で Recycle と `--delete-permanently` の各経路を通し、処理後も ZIP が存在し、ハッシュ・更新日時が不変(#23 の補完)。ID 不一致を別 object の証明にしない(S-18)。分類名の期待値は S-18 の決定後 |
| X-23 | ファイル/ディレクトリの構造衝突: 明示的な `a` と `a/` → `AMBIGUOUS`(SPEC §8)。`a` と `a/b`(暗黙のディレクトリ)→ S-23 の決定に従う | Core | RawZipBuilder。#21 の補完。暗黙の場合の期待値は S-23 の決定後に確定 |
| X-24 | `--target` が UNC ルート(`\\server\share`)→ 拒否、終了コード 3 | Core(+手動 M-05) | 拒否判定に UNC ルートの実パスを注入。実共有での確認は M-05(CI では共有を用意できない) |
| X-25 | 確認プロンプト `[y/N]` の既定は中止: 空入力、EOF、`n`、その他の文字 → 何も削除せず終了コード 2。`y`/`Y` のみ続行 | Win(CLI) | プロセス起動(TTY を模擬した入力)。TTY の模擬が CI で困難な場合は、入力判定部の Core テストで補う |
| X-26 | 状態機械の追加遷移: `ReportedFailed`(または Pre で中止)のあと対象が元の位置にない → `UnknownShellOutcome`。`ReportedRecycled` で Post の dwFlags に 0x80 がない → `UnknownShellOutcome` | Core | ScriptedSink。§4.7.3 |

#### 11.2.1 追加テストの割当

| X | タスク(完了条件) | Phase |
|---|---|---|
| X-01 | P1-7 | 1 |
| X-02、X-15、X-17 | P1-8 | 1 |
| X-03、X-04、X-05、X-18、X-19 | P1-3(G1 の証拠を含む) | 1 |
| X-06 | P1-4(G1 の証拠) | 1 |
| X-16 | P1-5 | 1 |
| X-23 | P1-6 | 1 |
| X-21、X-22 | P1-8(Fake)、P2-3(X-21 の実 FS)、P2-5(X-22 の dry-run)、P3-4・P3-5(X-22 の実削除統合) | 1、2、3 |
| X-12、X-24 | P2-2 | 2 |
| X-14 | P2-5 | 2 |
| X-13 | P3-1 | 3 |
| X-11 | P3-2 | 3 |
| X-20 | P3-3 | 3 |
| X-07、X-08、X-09、X-10、X-26 | P3-4(G3 成立時のみ) | 3 |
| X-25 | P4-1 | 4 |

### 11.3 正常 ZIP の `ZipArchive` 差分テスト(必須)

- **目的**: 自前の構造リーダーが、正常な ZIP に対して標準実装と同じ解釈をすること(互換性と構造解析の誤りの検出)
- **比較項目**: エントリ数と順序、名前(UTF-8 フラグ付きはそのまま、フラグなしは `ZipArchive` に CP437 を明示して比較)、宣言サイズ、圧縮サイズ、CRC、圧縮方式、暗号化フラグ、展開後の内容(SHA-256)、展開バイト数
- **入力の範囲**:
  - `ZipArchive`(作成モード)で生成した ZIP(Stored / Deflate、0 バイト、空アーカイブ、多数エントリ、深い階層)
  - `RawZipBuilder` の正常系(Zip64、Data Descriptor 付き、extra field 付き)
  - 外部ツールで作成した正常 ZIP(Windows の圧縮フォルダー、7-Zip、Info-ZIP 等)を、ライセンス上問題のない自作内容で fixture として固定(作成ツールと版を記録)
  - 生成器によるランダムな正常 ZIP(内容・名前・サイズを乱数で生成、シード固定)
- **独立性の限界**:
  - 展開に同じ `DeflateStream`(同じ基盤の inflate 実装)を使う部分は、`ZipArchive` と比べても**独立した基準にならない**。この比較は、境界・オフセット・メタデータの解釈の一致だけを示す
  - 展開内容の独立した基準は、ZIP を作るときに使った**元のデータ**(既知の平文)とのバイト比較、および CRC の独立計算とする
  - Z-B(自前 Inflate)を採用した場合は、`DeflateStream` が展開内容についての独立した基準になる
- 異常系は差分テストの対象外(`ZipArchive` は §6.2 を満たさないため、比較の基準にできない)

### 11.4 G3 不成立時に影響を受ける SPEC テスト(#1〜#54 からの再抽出)

前提: G3 不成立の場合、Recycle を MVP から除外し、既定モードでは削除しない(全 `MATCHED` を `ERROR`)。`--delete-permanently` は SPEC どおり残す(PLAN 側で外さない)。#1〜#54 の全件を、既定モードの実削除に依存するかで分類した。

| 区分 | テスト | 影響 |
|---|---|---|
| (A) 既定モードでの実削除を期待しており、記載どおりには成立しない | #7(他の `MATCHED` は削除される)、#11(空になったディレクトリを削除)、#33(先頭のみ削除、終了コード 1) | `--delete-permanently` でのみ成立する。テストの読み替えは SPEC の解釈に当たるため、V-04 として人間が決める |
| (B) 「削除しない」ことを確認するテストで、既定モードでは削除経路がないため自明に成立し、検証力を失う | #5、#12、#13、#14、#23、#24、#25、#26、#40、#50 | #23 は、dry-run で ZIP 自身を候補にしない検査は残るが、元 ZIP が実削除後も不変という確認には実削除経路が必要。検証力を保つには `--delete-permanently` の経路で実行する必要がある。その扱いを V-04 で人間が決める |
| (C) 削除開始後の挙動を確認するテストで、既定モードでは削除が始まらない | #44(削除開始後の未検出異常) | `--delete-permanently` の経路、または Fake でのみ確認できる |
| (D) Recycle 固有の事象を前提とするテスト | #53(完全削除の発生を内部安全性エラーとする) | 「完全削除の発生」は Recycle モードの保証の破綻であり、Recycle を除外すると実環境では発生しない。Fake での確認のみ残る |
| (E) モードごとの期待値を含むテスト | #27(既定は `ERROR`、`--delete-permanently` のみ削除)、#49(両モードで `ERROR`) | 既定モードの期待値は成立する。`--delete-permanently` 側は P3-5 で確認 |
| (F) 影響なし | #1〜#4、#6、#8〜#10、#15〜#22、#28〜#32、#34〜#39、#41〜#43、#45〜#48、#51、#52、#54 | 分類、構造検証、パス検証、target 初期化、dry-run、CLI の確認であり、既定モードの実削除に依存しない |

追加テストのうち X-07〜X-10、X-26 は Recycle の実装を前提とし、G3 不成立時は実施しない。X-20 は Fake での状態機械の確認として残す。§19(MVP 完成条件「ごみ箱へ送られる」)も影響を受ける(V-04)。
