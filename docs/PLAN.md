# unextract 実装PLAN(SPEC v4 対応・全面再監査版)

- 改訂日: 2026-10-01
- 仕様上の正: `docs/SPEC.md` v4(唯一の正)。本 PLAN は SPEC を変更しない
- 前版: SPEC v3 前提の PLAN(以下「旧PLAN」)。本版は旧PLANへの追記ではなく、SPEC v4 との突合に基づく全面改訂である
- 本書は PLAN のみ。コード、プロジェクトファイル、CI 設定は作成していない
- 第2改訂: 本書の第1改訂に対する独立レビュー指摘(1〜8 と軽微2件)を反映した。反映内容は §1.7。SPEC は変更していない。判断を要する新規事項は §8 の S-15〜S-17 として人間に戻している(本書は決定していない)
- 第3改訂: 人間の決定(H-01〜H-12 と S 項目)を反映した。決定記録は §14、S 項目の状況は §8 の各行末尾。(第3改訂の時点では P0-3 以降に未着手だった。現状は第4改訂を参照)
- 第4改訂(2026-10-01): (1) P0-3(一次資料の再確認。結果は `docs/P0-3_results.md`(現 `PLAN_HISTORY.md`「P0-3 調査記録」)、確認日 2026-10-01)の結果を §3 ほかの区分へ反映した。(2) P0-3b の人間判断(S-06、S-10、S-15、S-16、H-08、R-B14/R-B16、R-C4、R-Z7)を §14.5 に記録し本文へ反映した。(3) 第3改訂に対する独立レビュー(Codex)の採用指摘を反映した(§1.8)。(4) 新たな要確認事項 S-18〜S-23 を §8 に追加した(本書は決定していない)。(5) G0 を再評価した(§10.1)。SPEC は変更していない。P0-4 以降の調査・スパイク・実機確認には着手していない
- 文書構成の整理(2026-10-01。構成のみ): PLAN を役割別の5文書に分割し、旧 `docs/P0-3_results.md` の内容を統合した。SPEC、設計判断、ID、Phase/task の割当、ゲート条件、テスト期待値は変更していない。節番号(§x)は移動先でも分割前の番号を維持する。記録は `PLAN_HISTORY.md`「文書分割の記録」

## 文書構成と参照ガイド

PLAN は次の5文書で構成する。節番号(§x)は分割前の番号を移動先でも維持しているため、本文中の「§x」は下の「節の所在」で所在を引ける。ID(H/S/K/R/V/M/X/O/CR 等)は文書をまたいで一意であり、分割で変更していない。

| 文書 | 役割 |
|---|---|
| `PLAN.md`(本書) | 現在の実装計画・Phase・Gate・入口。アーキテクチャと安全モデル(§4、§5)、SPEC との対応(§7)、タスクとゲート(§10)、使用パッケージ(§12)、撤退時に人間へ戻す事項(§13.2)、検証の人間許可の境界(§14.4) |
| `PLAN_DECISIONS.md` | 現在有効な設計判断・未決事項・リスク。S-01〜S-23(§8)、H-01〜H-12 の決定記録(§14)、O-01〜O-16(§13.1)、K-01〜K-20(§9)、R の各項目と一次資料確認状態(§3)、P0-3 確認状態の一覧、CR-01〜CR-18(§1.8)、旧スパイク環境(§2.2) |
| `PLAN_VALIDATION.md` | 今後の実機検証・Spike・Manual validation。P0-4〜P0-7 の検証項目の索引、P0-4 の V1〜V8(§4.3.2 から移動)、M-01〜M-18(§6)、検証環境・作業フォルダ・容量・外部取得・人間許可(§14.4 の写し) |
| `PLAN_TESTS.md` | SPEC §17 #1〜#54 と追加テスト X-01〜X-26 の対応、タスク割当、`ZipArchive` 差分テスト、G3 不成立時に影響を受けるテスト(§11) |
| `PLAN_HISTORY.md` | 完了済みの調査・レビュー・改訂の監査証跡。再監査表(§1.1〜§1.7)、各改訂の作業条件(§2)、自己点検の記録(§15)、P0-3 調査記録(旧 `docs/P0-3_results.md` の全文)、文書分割の記録 |

参照ガイド(必要な文書だけを読む):

| 作業 | まず読む | 必要に応じて |
|---|---|---|
| 実装時(P1〜P4 のタスク) | `PLAN.md` §4、§5、§10(タスクの完了条件・依存・ゲート) | `PLAN_TESTS.md`(完了条件のテスト)、`PLAN_DECISIONS.md`(関係する S/H/O/K/R) |
| P0-x 検証時 | `PLAN.md` §10(P0 タスク、ゲート)、§14.4(人間許可の境界) | `PLAN_VALIDATION.md`(検証項目・環境・許可)、`PLAN_DECISIONS.md` §3(関係する R の確認状態)、§8 |
| テスト確認時 | `PLAN_TESTS.md` | `PLAN.md` §10.2(割当先のタスク) |
| 過去判断の根拠確認時 | `PLAN_HISTORY.md` | `PLAN_DECISIONS.md`(現在有効な結論) |

節の所在:

| 節 | 文書 |
|---|---|
| §0 本書の読み方(凡例 §0.2 を含む) | `PLAN.md` |
| §1.1〜§1.7 再監査結果・第2改訂で反映したレビュー指摘(A-01〜A-05、B-01〜B-11、C-01〜C-10、D-01〜D-05、E-01〜E-15、F-01〜F-08、RV-01〜RV-10) | `PLAN_HISTORY.md` |
| §1.8 第4改訂で反映した事項(CR-01〜CR-18) | `PLAN_DECISIONS.md` |
| §2.1、§2.3〜§2.5 各改訂の作業条件 | `PLAN_HISTORY.md` |
| §2.2 旧スパイク環境 | `PLAN_DECISIONS.md` |
| §3 調査結果(§3.0〜§3.4.1) | `PLAN_DECISIONS.md` |
| §4 設計(§4.3.2 の V1〜V8 の表を除く) | `PLAN.md` |
| §4.3.2 の P0-4 検証項目 V1〜V8 の表 | `PLAN_VALIDATION.md` |
| §5 archive-level rejection の分類表 | `PLAN.md` |
| §6 手動確認手順(M-01〜M-18) | `PLAN_VALIDATION.md` |
| §7 SPEC v4 との対応表 | `PLAN.md` |
| §8 SPEC との矛盾・要確認事項(S-01〜S-23) | `PLAN_DECISIONS.md` |
| §9 リスク一覧(K-01〜K-20)と README 記載案 | `PLAN_DECISIONS.md` |
| §10 フェーズ別タスクとゲート | `PLAN.md` |
| §11 テスト計画 | `PLAN_TESTS.md` |
| §12 使用パッケージ一覧 | `PLAN.md` |
| §13.1 決まっていない・確認できていないこと(O-01〜O-16) | `PLAN_DECISIONS.md` |
| §13.2 SPEC変更が必要となる可能性がある未解決事項(V-01〜V-09) | `PLAN.md` |
| §14.1〜§14.3、§14.5、§14.6 決定記録 | `PLAN_DECISIONS.md` |
| §14.4 H-01 の細目 | `PLAN.md`(正)。`PLAN_VALIDATION.md` に写し |
| §15 自己点検の記録 | `PLAN_HISTORY.md` |
| P0-3 調査記録(旧 `docs/P0-3_results.md`) | `PLAN_HISTORY.md`(各項目の区分の一覧は `PLAN_DECISIONS.md`「P0-3 確認状態の一覧」) |

今後の P0-x 結果の反映先(P0-x ごとの結果ファイルを恒久的な PLAN 文書として増やさない。実験の完了後に次へ反映する):

| 内容 | 反映先 |
|---|---|
| 現在有効な結論・判断・未決事項 | `PLAN_DECISIONS.md` |
| 今後必要な検証・残タスク | `PLAN_VALIDATION.md` |
| 完了済み実験の詳細証跡 | `PLAN_HISTORY.md` |
| Phase/task/gate への影響 | `PLAN.md` |
| テスト coverage への影響 | `PLAN_TESTS.md` |

---

## 0. 本書の読み方

### 0.1 SPEC 要件と PLAN 案の分離

- 本書で「**要件**」と書くものは SPEC v4 の該当節の要約であり、本書はそれを変更・緩和・追加しない
- 本書で「**実装案**」と書くものは、**SPECを満たすための現時点の実装案**であって SPEC 上の決定事項ではない。各案には満たすべき SPEC の節を `[§x.y]` で紐づける。案を差し替えても SPEC は変わらない
- SPEC を満たせない可能性が判明した場合、本書は SPEC の緩和を決定しない。§13.2「SPEC変更が必要となる可能性がある未解決事項」として人間に判断を戻す
- SPEC が固定していない事項(ZIP 読み取り方式、解決 API、ごみ箱の保証条件、上限値など)を、本書の記述によって SPEC 上の決定事項へ昇格させない

### 0.2 事実の区分(凡例)

本書の事実に関わる記述には、次のいずれかの区分を付ける。旧PLANの「確認済み(実機)」「確認済み(一次資料)」という語は継承しない。

| 記号 | 区分 | 意味と使い方 |
|---|---|---|
| **[過去実機]** | 過去スパイクの実機結果 | 旧PLAN に記録された過去の観測。**今回再検証した事実ではない**。観測条件は §2.2 の「旧スパイク環境」。観測条件が不足する場合、一般保証として使わない |
| **[旧一次資料要約]** | 旧PLAN記載の一次資料要約(未再読) | 旧PLANが参照した一次資料の要約。第4改訂で、P0-3 で再確認した項目はすべて下の区分へ移した。**第4改訂時点で本区分の項目は残っていない** |
| **[一次資料で確認した事実]** | 一次資料で確認した事実 | P0-3(確認日 2026-10-01。`docs/P0-3_results.md`(現 `PLAN_HISTORY.md`「P0-3 調査記録」))で、文書名+節を確認した記述。P0-3 の「◎」の項目と、「○」の項目のうち確認できた範囲。**文書が述べている範囲に限る**。文書が述べていない部分は同じ行で [未確認] と分けて書く。本書の第4改訂作成者は一次資料を再取得していない(P0-3 の記録に依拠) |
| **[推測・設計]** | 推測・設計判断 | 本書の判断。根拠を併記する |
| **[未確認]** | 未確認(要手動確認 / 要実機 / 要一次資料) | 括弧内に何が必要かを書く |

補足:

- 旧PLANが dotnet/runtime の `release/10.0` ブランチで確認した事項のうち、R-A15、R-Z1、R-Z4〜R-Z7 は、P0-3 で `v10.0.12` タグのソースにより再確認した(§3)。ただし P0-3 はタグのコミット SHA を確認できていない(タグ名の URL が取得できたことのみ。§3.0)
- P0-3 の「×」の項目、および P0-3 の範囲外の項目は [未確認] のまま残す
- 二次資料(ブログ、Q&A、フォーラム)は安全性の結論の根拠にしない。補助的に触れる場合は「二次資料・確度」を明記する

---

## 1. 再監査結果

→ §1.1〜§1.7(A-01〜A-05、B-01〜B-11、C-01〜C-10、D-01〜D-05、E-01〜E-15、F-01〜F-08、RV-01〜RV-10)は `PLAN_HISTORY.md` §1、§1.8(CR-01〜CR-18)は `PLAN_DECISIONS.md` §1.8 へ移動した。

---

## 2. スパイク環境と今回の追加調査

→ §2.1、§2.3〜§2.5(各改訂の作業条件)は `PLAN_HISTORY.md` §2、§2.2(旧スパイク環境。[過去実機] の観測条件)は `PLAN_DECISIONS.md` §2.2 へ移動した。

---

## 3. 調査結果

→ `PLAN_DECISIONS.md` §3 へ移動した(§3.0 P0-3 の反映についての注記、R-A1〜R-A17、R-B1〜R-B18、R-C1〜R-C6、R-Z1〜R-Z14、§3.4.1 APPNOTE)。P0-3 の項目ごとの区分(◎/○/×)は `PLAN_DECISIONS.md`「P0-3 確認状態の一覧」、調査記録の原文は `PLAN_HISTORY.md`「P0-3 調査記録」。

---

## 4. 設計(SPEC を満たすための現時点の実装案)

本章のすべては**実装案**である。各項に満たすべき SPEC の節を付ける。

### 4.0 モジュール構成 [§18]

```
src/
  Unextract.Core/       判定・ZIP 構造検証・パス検証・計画・状態機械。Win32 非依存
  Unextract.Windows/    ファイルシステム抽象・クラウド識別・削除の Windows 実装(CsWin32)
  Unextract.Cli/        引数・表示・確認・終了コード
tests/
  Unextract.Core.Tests/      Fake の FS / Deleter による検証
  Unextract.Windows.Tests/   実 FS での統合テスト
  Unextract.TestHelpers/     RawZipBuilder、FsFixture、Snapshot、ScriptedSink
```

`IFileSystemProbe` / `IDeleter`(§18 の名称)は、下記 §4.3 の抽象群の総称として維持する。

### 4.1 ZIP 読み取り [§6、§6.1、§6.2、§12]

#### 4.1.1 方式の選定(Phase 1 ゲート G1 の対象)

方式は §6.2 の4能力をすべて満たすことを G1 で示したものだけを採用する。現時点の候補と評価:

| 候補 | 内容 | §6.2-1 終端 | §6.2-2 超過 | §6.2-3 事前上限 | §6.2-4 全件構造検査 | 評価 |
|---|---|---|---|---|---|---|
| Z-A(第一候補) | 自前の構造リーダー(EOCD / Zip64 / CD / LH / DD を自前で解析)+ 展開は公開 `DeflateStream` を有界入力に適用 | **要設計**(下記) | 可(自前で計数) | 可 | 可 | 終端確認方法の成立が条件 |
| Z-B | 自前の構造リーダー + 自前の Inflate 実装(RFC 1951) | 可(BFINAL と end-of-block を自前で観測) | 可 | 可 | 可 | 実装・検証量が大きい。Z-A 不成立時の代替 |
| Z-C | `ZipArchive` + 自前の CRC・バイト数照合 | 不明 | **不可**(R-Z5) | **不可の可能性**(R-Z6) | 不明 | 採用しない |
| Z-D | サードパーティ ZIP ライブラリ | — | — | — | — | 依存と根拠資料の制約(一次資料ルール)から候補外。Z-A/Z-B がともに不成立の場合のみ再検討 |

Z-A の終端確認方法(案。保守的方針):

1. 有界入力ストリーム(`[dataStart, dataStart+CompressedSize)`)を自作し、`DeflateStream` が**入力 EOF(有界入力からの 0 バイト読み取り)に到達したかを記録**する
2. **入力 EOF に到達した展開は、すべて「終端未確認」として当該エントリを `ERROR` とする**(L4)。入力 EOF の到達が最終ブロック終了によるものか入力の欠落によるものかを区別する手段は、現時点で確立していない。区別を試みない [推測・設計]
3. 「正常終端」と認めるのは、`DeflateStream` が出力 EOF を返し、かつ有界入力が EOF を一度も返していない場合に限る
4. この方針は偽陽性(正常なエントリを `ERROR` にする)を許容する安全側の設計である。**この制約で実用上成立するか**は G1 で評価する。評価指標: 正常 ZIP の fixture(`ZipArchive` 生成、外部ツール生成、乱数生成)で、正常エントリが `ERROR` にされる割合。正常エントリの多くが `ERROR` になる場合は Z-A 不成立とし、Z-B(自前 Inflate)を検討する
5. 圧縮範囲の末尾に未消費バイトが残ったか(L8)は、観測できる場合のみ検出する追加検査である(S-08、決定済み)。`DeflateStream` の内部バッファリングのため直接観測できない可能性がある [推測・設計]。**観測できないことは Z-B を検討する理由にしない**。観測できない場合、L8 は検出されない異常として残存リスク K-15 に記録する

6. `System.IO.Compression.UseStrictValidation`(R-Z7。既定 off)は P0-6 の検証項目とする。スイッチの条件に `NonEmptyInput()` 等が含まれるため、**このスイッチだけで §6.2-1 の正常終端確認が成立するとは扱わない**。上の 1〜3 の保守判定と併用するかどうかを含め、fixture と `v10.0.12` タグのソースで確認した結果を G1 の判断材料とする(§14.5)

R-Z7 の既定挙動(基底の EOF で例外なく途中までの出力を返す)は、P0-3 で `v10.0.12` タグのソースにより確認した。2〜3 の挙動(正常終端でも入力 EOF が観測されるか)が fixture と実機でどうなるかは [未確認](要実機。P0-6)。G1 の判定材料とする。

**撤退条件**(G1):

> §6.2 の能力を満たせない場合は、別方式を検討する。それでも満たせない場合は、SPECを変更せず、Phase 1 のゲート未達として停止し、「SPEC変更が必要となる可能性がある未解決事項」として人間に判断を戻す。PLAN作成者がSPEC緩和を決定してはならない。

対象バージョンの確認は、ブランチではなく、該当するタグ(例: dotnet/runtime `v10.0.12`)のソースで行う。`global.json` で固定する SDK に同梱されるランタイムの版に合わせる。

#### 4.1.2 二段階の処理 [§6.2-3、§6.2-4、§12.2]

1. **構造検証フェーズ**(エントリ内容の展開なし。全エントリ。MISSING / MODIFIED になるものも含む)
   1. ファイル末尾から EOCD を探索。候補の一意性を確認
   2. Zip64 locator / Zip64 EOCD を検証
   3. 宣言件数・CD サイズを §6.1 の上限と照合(CD を読む前)
   4. CD を逐次読み、各レコードの名前長・extra 長・累積メタデータ量・パス深さを上限と照合
   5. 各エントリの Local Header を読み、CD と照合(署名、名前バイト、方式、フラグ、サイズ、Zip64 extra)
   6. bit3(Data Descriptor)付きエントリは、データ範囲直後の DD を読み CD と照合
   7. 全エントリのデータ範囲・LH・CD・EOCD の区間を集め、重なり・隙間・範囲外を検査
   8. unsupported の判定(§5.2)
   9. 1件でも archive-level rejection があれば、分類結果を出さず終了コード 3
2. **内容検証フェーズ**(分類・再検証で、サイズが一致したエントリのみ展開)
   - entry-local error(§5.3)は当該エントリの `ERROR`
   - Stored(方式0)には圧縮ストリームの終端がない。終端確認の代替として、構造フェーズで、非暗号化の Stored について解釈に使う宣言圧縮サイズ = 宣言展開サイズを確認し(不一致は F19、archive-fatal。S-16 確定)、内容フェーズで範囲内から読んだバイト数が宣言サイズと完全に一致することを確認する。範囲を超えて読まない [推測・設計]。bit 3 のエントリでは、LH のサイズ・CRC が 0 であることを矛盾として扱わず、DD と CD の値を使う(APPNOTE 4.4.4)

削除開始前に 1 が全件完了していることを、状態機械(`ArchiveVerified` 状態を経ないと削除フェーズへ遷移できない)で保証する。

#### 4.1.3 インターフェース案

```csharp
namespace Unextract.Core.Archive;

public sealed record ZipEntryRecord(
    int Index, ReadOnlyMemory<byte> RawName, bool Utf8Flag, ushort Flags, ushort Method,
    uint Crc32, ulong CompressedSize, ulong UncompressedSize,
    ushort VersionMadeBy, uint ExternalAttributes, long LocalHeaderOffset, long DataOffset);

public interface IVerifiedArchive : IDisposable                 // 構造検証を通過した場合のみ生成される
{
    IReadOnlyList<ZipEntryRecord> Entries { get; }
    EntryContentReader OpenContent(ZipEntryRecord e, ExpansionBudget budget); // 宣言サイズ・ディスクサイズ・1エントリ上限
}

public abstract record ArchiveOpenResult
{
    public sealed record Verified(IVerifiedArchive Archive) : ArchiveOpenResult;
    public sealed record Fatal(ArchiveFatalReason Reason, string Detail) : ArchiveOpenResult;          // §12.2
    public sealed record Unsupported(UnsupportedReason Reason, string Detail) : ArchiveOpenResult;   // §12.5
}

public enum ContentResult { Matched, Differs, ExceededDeclared, ExceededDisk, ExceededEntryLimit,
                            MissingTermination, SizeMismatch, CrcMismatch, DataError, Unsupported, Encrypted, ReadFailed }
```

`ZipEntryRecord` は判定用であり、ZIP は読み取り専用・`FileShare.Read` で開く [§12.1]。ZIP のハンドルは処理の終了まで保持し、開いた時点で ZIP ファイル自身の `NodeIdentity` を記録する(ZIP 自身が target 内にある場合の除外に使う。§4.4 手順3、S-18)。

### 4.2 target の初期化順序 [§4、§7.2、§14]

| 手順 | 内容 | 失敗時 |
|---|---|---|
| T1 | ユーザー指定パスを絶対パス化(字句的な処理のみ。ファイルシステムを辿らない) | 不正 → 3 |
| T2 | **クラウド識別機構の初期化**(プロセス全体の placeholder 互換モードを公開に設定し、成功を確認する。§4.5.3)。**target を含むファイルシステムの観測より前に成功していなければならない**。理由: 公開モードでない状態では reparse point が隠れる場合があり(R-C1)、T4 以降の reparse 検査の結果がモードに依存するため | 失敗・成功を確認できない → 3(#51)。削除処理へ進まない。dry-run でも同じ(S-09) |
| T3 | 指定パスを、最終成分を reparse 非追従で開く。祖先の junction はこの時点で OS により辿られる。開く API は、H-1 の `RootDirectory` として使えるハンドルを得られるものとする(`CreateFile` 系のハンドルが使えるかは R-B14 [未確認]。P0-4) | 開けない → 3 |
| T4 | **最終成分の reparse・種別の検査**: 同じハンドルで属性を取得し、`FILE_ATTRIBUTE_REPARSE_POINT` ならば 3。ディレクトリでなければ 3 | 3(#47) |
| T5 | **実パス解決**: 同じハンドルで最終パスを取得(T4 と同一の個体から得るので、検査と解決の対象がずれない) | 取得不能 → 3 |
| T6 | **個体の保持**: 同じハンドルを保持する(データアクセス権を含め、共有に DELETE を含めない。R-B5)。T3 で最初からこのアクセス権・共有モードで開く。以降のすべての解決はこの個体が起点 | 保持できない(アクセス権不足等)→ 3(#48 後半) |
| T7 | **拒否リストの判定**(ポリシー): T5 の実パスを、拒否候補(ドライブ/ボリュームルート、`%USERPROFILE%` 自体、Windows、Program Files、Program Files (x86)、ProgramData とその配下、UNC ルート)と成分単位・大文字小文字を区別せずに比較 | 該当 → 3(#29、X-24) |
| T8 | 後続処理(ZIP の構造検証、分類)。T1〜T7 がすべて成功した場合のみ | — |

位置づけ:

- 概念上の順序は「字句処理 → クラウド識別機構の初期化 → 最終成分を非追従で開く → reparse・種別検査 → 最終パス取得 → ハンドル保持 → 拒否判定 → 後続処理」である。T3 で開いたハンドルを T6 でそのまま保持するため、T3〜T6 は同一ハンドル上の操作である
- 拒否リストは**受理範囲を狭める安全ポリシー**であり [§4]、包含の根拠ではない。包含は T6 の個体を起点とした単一成分解決の構造で保証する [§7.2]
- 拒否候補は既知フォルダ API と環境変数の和集合(多めに拒否)。候補自体も存在すれば最終パスへ解決して比較する [推測・設計]
- 「祖先の junction を個体として固定できない」とは、T3〜T6 のいずれかが不成立の場合を指す。T5 で DOS パスを得られない(ドライブ文字のないボリューム等)場合も、拒否判定ができないので 3 とする(S-14)
- 未確認: T3 で指定パスの途中の成分が別ボリュームへの junction の場合、および UNC 上の target の場合の T5 の戻り値形式 [未確認](要実機。M-05)

### 4.3 対象の解決: 単一成分の解決契約 [§7.2]

#### 4.3.1 契約(抽象のインターフェース)

- 対象への到達は、**保持中の検証済みディレクトリ個体**から、**検証済みの単一成分**で子を開くことだけで行う
- 抽象は複数成分のパス文字列を受け取る API を持たない。`PathComponent` は §7.1 の検証器だけが生成できる型とする
- この契約を、分類(dry-run)・削除直前の再検証・ファイル削除・ディレクトリ削除のすべてに使う。削除の抽象(下記の `IFileDeleter`、`IDirectoryRemover`)も、対象を保持中のノードと検証済みの単一成分だけで受け取り、パス文字列を受け取らない
- **適用範囲の限界**: ファイルのごみ箱送りの最終段(§4.7.2)は、Shell にパス文字列(または、パスから作った `IShellItem`)を渡し、Shell 側で再解決される。この段は本契約の外にある。**本書は、この最終段について §7.2「削除にも適用」を満たすと主張しない**。窓を縮小する手段(ハンドル保持、`PreDeleteItem` での ID 照合、事後照合)は §4.7 にあるが、縮小は充足ではない。SPEC §10.1・§16 は窓が残ることを認めているが、**PLAN はこれを §7.2 の例外とも充足とも解釈しない**。この段は G2 の判定範囲に含め、P0-4 で §7.2 を満たし得る手段(ハンドル由来の `IShellItem` の作成など)を調査する。満たせない場合は G2 を通過扱いにせず、「§10.1・§16 を §7.2 の例外と解釈してよいか」「SPEC 変更が必要か」を人間に戻す(S-17 決定済み、V-07)

```csharp
namespace Unextract.Core.FileSystem;

public sealed class PathComponent { /* §7.1 検証済みの単一成分。内部コンストラクタのみ */ }
public readonly record struct NodeIdentity(ulong VolumeSerial, UInt128 FileId);   // 補助的な照合値。時間をまたぐ一意性は前提にしない

public interface IFileSystemProbe
{
    TargetInitResult InitializeTarget(string userSuppliedPath);   // §4.2 の T1〜T7(T2 は ICloudStateProvider.Initialize)。成功時 ITargetRoot。
                                                                  // 文字列を受けるのはこの入口だけ(利用者の入力)。子の解決には使わない
}

public interface ITargetRoot : IDisposable
{
    IDirectoryNode Root { get; }         // 保持中の target 個体
    string DisplayPath { get; }          // 表示専用。判定・解決に使わない
}

public interface IDirectoryNode : IDisposable                   // ハンドルを保持する。Dispose まで同一個体
{
    NodeIdentity Identity { get; }
    NodeProbe<IDirectoryNode> OpenChildDirectory(PathComponent name, DirectoryAccess access);
    NodeProbe<IFileNode>      OpenChildFile(PathComponent name, FileAccessPurpose purpose);
    NodeProbe<NodeMetadata>   QueryMetadata();                  // 属性・reparse・名前付きストリーム・クラウド状態
    NodeProbe<ChildListing>   ListChildrenNonRecursive(int maxEntries); // 候補ディレクトリにだけ使う(§11)
}

public interface IFileNode : IDisposable
{
    NodeIdentity Identity { get; }
    NodeMetadata Metadata { get; }      // 長さ・リンク数・属性・ストリーム・クラウド状態(内容を読まずに取得)
    NodeProbe<Stream> OpenContent();    // 識別完了後にだけ呼べる。同一個体から読む(名前を再解決しない)
    NodeProbe<NodeMetadata> Refresh();  // 同一ハンドルで再取得
}

public enum FileAccessPurpose { InspectOnly, VerifyContent, VerifyForRecycle, VerifyForPermanentDelete }

// ---- 削除の抽象(§18 の IDeleter に相当)。第4改訂で追加 ----

// 削除直前の再検証を通過したことを表す。Core の再検証器だけが生成できる(内部コンストラクタ)
public sealed class VerifiedDeletionTarget
{
    public IDirectoryNode Parent { get; }        // 保持中の検証済み親個体
    public PathComponent Name { get; }           // 親からの単一成分
    public IFileNode File { get; }               // 再検証に使い、保持中のハンドル(名前を再解決せずに得た個体)
    public NodeIdentity ExpectedIdentity { get; }
}

public interface IFileDeleter
{
    DeletionMode Mode { get; }                   // Recycle(既定)/ Permanent(--delete-permanently)
    FileDeletionOutcome Delete(VerifiedDeletionTarget target);
}

public interface IDirectoryRemover
{
    // 保持中のディレクトリ個体そのものを、空であることを同一ハンドルで確認してから非再帰で削除する(§4.6、S-03)
    DirectoryRemovalOutcome RemoveIfEmpty(IDirectoryNode node, NodeIdentity expected);
}

public abstract record FileDeletionOutcome
{
    public sealed record Deleted(DeletionMode ConfirmedMode) : FileDeletionOutcome;          // 積極的に確認できた場合だけ(§4.7.3)
    public sealed record NotDeleted(NotDeletedReason Reason) : FileDeletionOutcome;          // 当該エントリ ERROR。元の位置にあることを確認済み
    public sealed record InternalSafetyError(InternalSafetyReason Reason) : FileDeletionOutcome; // 以降を中止、終了コード 3(§4.8)
}
public abstract record DirectoryRemovalOutcome { /* Removed / Kept(reason) / InternalSafetyError(reason) */ }
```

`NodeProbe<T>` は成功/`Missing`/`Reparse`/`AccessDenied`/`Locked`/`NotRegular`/`Error` 等を表す。外部カテゴリへの写像は Core の分類器が行う。

削除の抽象の契約(実装案):

- `IFileDeleter` と `IDirectoryRemover` は、対象を `VerifiedDeletionTarget` または保持中の `IDirectoryNode` でのみ受け取る。パス文字列を受け取る削除 API を抽象に置かない(X-01)
- 呼び出し側(Core の削除オーケストレーター)は、`FileDeletionOutcome.InternalSafetyError` を受けたら以降の削除を直ちに中止する。`Deleted` 以外を「削除済み」として集計しない
- **契約の外にある部分の明示**: Recycle モードの実装(§4.7)は、最終段で Shell にパスまたはパス由来の `IShellItem` を渡す。この段は §7.2 の単一成分解決の契約の外にあり、本書はこの段で §7.2 を満たすと主張しない(S-17、G2、V-07)。Permanent モードの実装案は、`File` の保持ハンドルに対する disposition 削除であり、名前の再解決を伴わない [推測・設計]。ただし Permanent の実装(P3-5)と Recycle の実装(P3-4)は、それぞれ G2/G3 の結果に依存し、本書では具体 API を確定しない
- テスト用の実装: `RecordingDeleter`(呼び出しの記録のみ。何も消さない)、`ScriptedDeleter`(指定した `FileDeletionOutcome` を返す。内部安全性エラーの注入に使う)、`FakeDirectoryRemover`。いずれも上の契約に従う

#### 4.3.2 実装案

| 案 | 内容 | 長所 | 懸念・採用に必要な確認 |
|---|---|---|---|
| H-1(第一候補) | `NtCreateFile` で `OBJECT_ATTRIBUTES.RootDirectory = 親ハンドル`、`ObjectName = 単一成分`、`FILE_OPEN_REPARSE_POINT`、`FILE_OPEN`。開いた後に属性を取得し reparse なら SKIPPED | 名前の再解決が親個体の内側に限られ、§7.2 の構造に直結。成分が短いので長いパスの制限を受けにくい [推測・設計] | P0-3 で文書上の成立を確認できなかった(R-B14 部分確認)。P0-4 の検証項目 V1〜V7(下記) |
| H-2(代替) | 祖先チェーンの全ディレクトリを共有 DELETE なしで保持(リネーム不可にする)したうえで、`\\?\` 付きフルパスで `CreateFileW` し、開いた個体の親が保持中の親個体と同一であることを事後確認 | Win32 公開 API のみ | パス文字列による再解決を含むため、§7.2「未検証の祖先パスを再解決してはならない」との適合が不明確(§8 S-02)。保持による固定が全経路(junction への変換、ボリュームのマウント変更)に有効かは [未確認] |
| H-3(補助) | `ReOpenFile` で、属性のみで開いたハンドルからデータアクセス用ハンドルを得る | 名前を再解決せずにアクセス権を上げられる(と期待される。文書に明記なし) | R-B16 部分確認。`hOriginalFile` は文書上 `CreateFile` 由来とされ、H-1(`NtCreateFile` 由来)との組合せは文書上成立しない。P0-4 の V3〜V4 |

P0-4 で H-1/H-3 について検証する項目 V1〜V8(P0-3b の判断。§14.5)の表は `PLAN_VALIDATION.md` §4.3.2 へ移動した。項目: V1 `NtCreateFile` の `RootDirectory`、V2 `FILE_DIRECTORY_FILE`+`FILE_OPEN_REPARSE_POINT`、V3 `NtCreateFile` 由来ハンドルと `ReOpenFile`、V4 `ReOpenFile` が名前を再解決しない前提、V5 target 初期化ハンドルとの互換性、V6 CsWin32 での生成、V7 `OBJ_CASE_INSENSITIVE` の照合(S-06)、V8 NT 名前空間での名前(S-22)(§10.2 P0-4 の記載順)。

V1〜V6 のいずれかが成立せず代替もない場合は G2 に戻し、SPEC を緩和しない(§13.2 V-02)。

H-1 が不成立で H-2 の適合性も人間が認めない場合、§7.2 を満たす方式がないことになるため、Phase 2 ゲート(G2)未達として停止し、人間に判断を戻す(§13.2)。

### 4.4 分類フェーズの処理順 [§6、§7.3、§7.4、§10.1、§11]

各エントリについて(§7.1 で UNSAFE_PATH、§8 で AMBIGUOUS のものはファイルシステムにアクセスしない):

1. target 個体から中間ディレクトリを単一成分ずつ開く。各ディレクトリで reparse → `SKIPPED_SPECIAL_FILE`、状態取得失敗 → `ERROR`
2. 最終成分を `InspectOnly`(属性読み取りのみ、reparse 非追従、データアクセスなし)で開く。存在しなければ `MISSING`。大文字小文字の異なる実名に解決された場合の扱いは **S-06(未決定。P0-4 の V7 の後に決める)**。決定までは、要求した成分と解決された実名が異なる場合を記録し、S-18 の別名検出の材料にする
3. **内容を読む前に**、次を同一ハンドルで取得して判定する(複数の条件が同時に成立する場合の優先順位は S-21。未決定)
   - ZIP ファイル自身との同一性: 対象の `NodeIdentity` が ZIP の `NodeIdentity`(§4.1.3)と一致する場合は、削除候補にしない(S-18。分類名は未決定)。ID が一致しないことは「ZIP 自身ではない」証明として扱わない(R-B6)。追加の防御として、ZIP のハンドルは共有に DELETE を含めずに保持する(削除操作は共有違反で失敗する見込み [推測・設計])
   - reparse 属性(クラウドのタグを含む)→ `SKIPPED_SPECIAL_FILE`
   - クラウド状態(§4.5.3)。識別された placeholder → `SKIPPED_SPECIAL_FILE`。取得失敗 → `ERROR`
   - ディスクファイル以外(`GetFileType`、ディレクトリ)→ `SKIPPED_SPECIAL_FILE`
   - リンク数 > 1 → `SKIPPED_SPECIAL_FILE`
   - 名前付きデータストリーム(§4.5.1)→ `SKIPPED_SPECIAL_FILE`(reason `NamedDataStream`)、確認不能 → `ERROR`
   - 読み取り専用属性 → **フラグとして記録するだけで、この時点では分類しない**。比較は省略しない。内容が異なる読み取り専用ファイルは `MODIFIED` のまま(手順5で扱う)
   - サイズ不一致 → `MODIFIED`(展開しない。読み取り専用でも `MODIFIED`)
   - 宣言サイズ > 1エントリ上限 → `ERROR`(展開しない)
4. データアクセス用ハンドルを得る(H-3 の `ReOpenFile` を優先する案。H-1 との組合せは P0-4 の V3〜V5 で確認するまで成立扱いしない。使えない場合は同一親から単一成分で再オープン)。共有 `READ` のみ。
   - 手順2のハンドルは属性のみのアクセスであり、共有モードによる置換阻止は効かない(R-B5)。手順2〜4の間に置換されうる
   - 再オープンした場合の `NodeIdentity` の照合は、**置換を検出するための補助条件**であり、「ID 一致 = 同一個体」の保証ではない(ID は時間をまたぐ一意性を保証しない。§4.3.1、§4.6)。不一致は「変化あり」として当該ファイルを `ERROR`。一致しても同一個体の証明としては扱わない
5. ストリーム比較(固定チャンク)、終端・サイズ・CRC の確認 → `MATCHED` / `MODIFIED` / `ERROR`。**最後に**、結果が `MATCHED` で手順3の読み取り専用フラグが立っている場合だけ `ERROR`(reason `ReadOnly`)に置き換える [§10.1、#49]。`MODIFIED` と他の `ERROR` はそのまま
6. 分類時の `NodeIdentity`・サイズ・解決された実名を記録し、ハンドルを閉じる(確認待ちの間に大量のハンドルを保持しないため)
7. **同一個体への別名の検出(S-18)**: 全エントリの分類後、`MATCHED` 候補を含むエントリの間で、同じ `NodeIdentity` が観測された組があれば、その組のすべてのエントリについて当該個体を削除候補にしない(分類名は S-18 で決める。決定までは削除しない側の扱いのみを確定とする)。また、要求した成分と解決された実名が異なるもの(8.3 短縮名による解決など)も、同じ個体を指す別エントリの検出材料にする
   - 「同じ ID を観測した → 同一個体の可能性があるので除外」は安全側の検出として使う。「ID が異なる → 別個体」とは判断しない(R-B6: FAT では ID がデフラグや名前変更で変わりうる。R-B8: ID は時間をまたぐ一意性を保証しない)
   - ID による検出が及ばない場合(FAT 上の ID 変化など)は、解決後実名や別名を含む検出方法を P0-4/M-17 で検証する。ZIP 自身を含め、S-18 の安全原則を保証できないまま P1-8 の分類器を完了させない(§10.2、K-17)
8. 削除予定ディレクトリのシミュレーション: `MATCHED` の祖先(target を除く)を候補とし、候補ディレクトリだけを非再帰で列挙して、予定外の子・名前付きストリーム・reparse があれば候補から外す。候補以外は列挙しない [§11、#43]

ZIP 側の構造衝突(§8)は、ファイルシステムへのアクセス前に Core で判定する。明示的なファイルエントリ `a` とディレクトリエントリ `a/` の衝突は SPEC §8 により `AMBIGUOUS`。ファイルエントリ `a` と、`a/b` のようにパスの途中の成分として `a` を含むエントリ(暗黙のディレクトリ)との衝突の扱いは S-23(未決定)。

検査の順序は「識別してから読む」を満たすことが要件 [§7.3] であり、上記の順序はその実装案である。手順2〜3 でクラウド状態を、対象を開いた後の属性取得だけで識別できるかは R-C4 により確認済みではない(§4.5.3)。

### 4.5 特殊対象の検査

#### 4.5.1 名前付きデータストリーム [§7.4]

- 実装案: 同一ハンドルで `GetFileInformationByHandleEx(FileStreamInfo)` を取得する
  - ファイル: 無名ストリーム(`::$DATA`)以外が1つでもあれば reason `NamedDataStream`
  - ディレクトリ: ストリームが1つでもあれば `NamedDataStream`
  - 取得失敗(バッファ不足以外のエラー、非対応 FS での想定外の戻り値)→ `ERROR`
- 外部カテゴリはファイルでは `SKIPPED_SPECIAL_FILE`。ディレクトリでは「削除しない」(ディレクトリはエントリの分類対象ではないため、削除予定ディレクトリから外し、理由を表示する)
- 例外なし(`Zone.Identifier` を含む)
- [未確認](要一次資料・要実機): 必要なアクセス権、ストリームのないディレクトリでの戻り値(エラーコードで返る可能性)、FAT/exFAT での戻り値。P0-3 の範囲に含まれていなかったため、P0-4(一次資料と実機)と M-10 で確認する(§13.1 O-04、O-15)

#### 4.5.2 読み取り専用 [§10.1]

- 分類時・再検証時の両方で属性を確認する。内容比較は省略しない(§4.4 手順3・5)。比較結果が `MATCHED` で `READONLY` なら `ERROR`。`MODIFIED` や他の `ERROR` はその結果のまま維持し、`ERROR` に上書きしない。`--delete-permanently` も同じ
- 属性を無視・解除する削除手段は採らない(`FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE` を使わない、属性を書き換えない)
- 残存リスク: 属性の変更は共有モードで防げない [推測・設計]。再検証から Shell の移動までの間に属性が付与された場合、Shell は移動しうる(R-A16)。`PreDeleteItem` 内で保持ハンドルの属性を再取得し、`READONLY` なら `E_ABORT` する案を採る。それでも残る窓は §9 のリスクに記載

#### 4.5.3 クラウド識別 [§7.3、§14]

- 初期化(プロセス全体): 公開モード設定(R-C2)を **target の観測より前**(§4.2 T2)に呼ぶ。戻り値が負(失敗)、または設定後の状態を確認できない場合は**削除開始前に終了コード 3**。dry-run でも同じ(S-09 決定)。R-C1 の文書間の食い違いがあるため、既定のモードには依存しない
- 個別対象: 属性のみのハンドルで、reparse タグ、クラウド関連属性を取得。placeholder と識別 → `SKIPPED_SPECIAL_FILE`。取得失敗 → 当該対象 `ERROR`
- **R-C4 による制約(P0-3b の判断。§14.5)**: `FILE_ATTRIBUTE_RECALL_ON_OPEN` はディレクトリ列挙でのみ現れる(一次資料)。したがって、対象を開いた後の属性取得だけに依存する上記の案は**確認済みとして扱わない**。P0-4 で次の候補を検証する
  - C-a: 対象を開いた後の属性・reparse タグの取得(現行案)で、各状態が識別できるか
  - C-b: 親ディレクトリの列挙(または名前を指定した問い合わせ)で得た属性による識別(`CfGetPlaceholderStateFromFindData` 等)
  - C-c: 属性のみのオープンそのものがハイドレーションを起こさないか
  - C-b を採用する場合の SPEC §11/§15「候補ディレクトリ以外のtarget内を列挙してはならない」との整合は **S-20(未決定)**。本書は解釈しない。名前を指定した問い合わせでも、名前にワイルドカードとして解釈される文字が含まれうる点は S-22 と合わせて確認する
- **識別前に内容を読まない**: §4.4 の手順 2〜3 は内容アクセスを伴わないハンドルで行う。属性のみのオープンが回収を起こすかは R-C4 [未確認](C-c)
- 保証範囲: 採用した機構で識別できることを手動確認(M-07)した状態だけ。識別できない可能性がある状態を通常ファイル扱いしてよいという意味ではない。M-07 で識別できない状態が見つかった場合、その状態を安全に除外する手段がなければ §13.2 に戻す
- 抽象: `ICloudStateProvider { InitResult Initialize(); CloudProbe Probe(IFileNode or IDirectoryNode); }`。テストでは初期化失敗と個別失敗を注入する(#51、#52)

### 4.6 ディレクトリ個体の寿命と削除順序 [§10.2、§7.2]

- **主根拠はハンドルの連続保持**、ID は補助
  - 削除フェーズでは、target 個体から各ファイルの祖先ディレクトリを単一成分ずつ開き、**削除フェーズの終了(ディレクトリ削除の完了)までハンドルを保持**する(ノードのキャッシュ。同じディレクトリは1回だけ開く)
  - ディレクトリ削除は、その保持ハンドル自身に対して行う。したがって「削除したファイルの祖先として確認した個体」と「削除する個体」は同一ハンドルで結ばれる
  - ID は、開いた時点で記録し、削除直前に同一ハンドルで再取得して一致を確認する(不一致は内部安全性エラー「対象の同一性の不一致」)。ID は時間をまたぐ一意性を保証しないため(R-B8)、**分類フェーズで記録した ID とハンドルを閉じた後の ID の一致は、同一個体の証明として使わない**。分類時 ID との不一致は「変化あり」として当該ファイルを `ERROR` にする材料にだけ使う
  - FAT 上の扱い(R-B6): FAT では ID がデフラグや名前変更で変わりうる。同一ハンドルでの再取得で ID が変わった場合、上の規則では内部安全性エラーとして全体を中止する(安全側だが、正常な操作でも中止しうる)。これを許容するか、FAT 上で ID 照合をどう位置づけるかは、M-04 の観察結果とともに G2 で判断する([未確認]。§13.1 O-16)。ID 照合を緩める方向の変更は、本書では行わない
- 保持ハンドルのアクセス権・共有(案): `FILE_LIST_DIRECTORY|FILE_TRAVERSE|FILE_READ_ATTRIBUTES|DELETE|SYNCHRONIZE`、共有 `READ|WRITE`(DELETE を共有しない → 他者のリネームを阻止。R-B5)
  - 懸念: この保持中に、子ファイルを Shell が `$Recycle.Bin` へ移動できるか(R-B5 の後半)、自分の保持ハンドルが自分の他の操作と衝突しないか [未確認](要実機)
  - ハンドル数は「削除したファイルの祖先ディレクトリ数」に比例する。上限はエントリ総数上限以下 [推測・設計]
- 削除順序: 深い順(子から親)。各ディレクトリについて
  1. 同一ハンドルで属性(reparse でない)、名前付きストリームなし、ID 一致を確認
  2. 同一ハンドルで子を非再帰列挙し、空であることを確認
  3. 同一ハンドルで disposition 削除(非再帰。空でなければ失敗する。R-B18)。`POSIX_SEMANTICS` を指定
  4. ハンドルを閉じ、**親から名前が消えたことを確認**してから親へ進む
- 削除マークと消滅の違い: disposition はマークであり、実際の消滅は最後のハンドルが閉じた時点(POSIX セマンティクスでは名前空間からの除去が早まる)[推測・設計、R-B17 未確認]。他プロセス(ウイルス対策など)がハンドルを持っていると、親から見て子が残り、親は空でなくなる → 親は削除しない(安全側)。POSIX セマンティクスが使えない FS でも同様に「残す」側に倒れる
- target は削除しない。ZIP の明示ディレクトリエントリ・事前から空のディレクトリは対象外 [§10.2]
- 「RemoveDirectory 相当」との関係は §8 S-03

### 4.7 ごみ箱削除と Shell 結果の状態機械 [§10.1、§12.6](Recycle ゲート G3 の対象)

**位置づけ**: 以下は G3 で保証条件が確立できた場合の実装案である。確立できない間、既定モードの削除は行わない(全 `MATCHED` を `ERROR`、reason `RecycleNotGuaranteed`)[§2、§10.1]。

#### 4.7.1 保証条件の候補(未確立)

事後検出は保証の代替にならない [§10.1]。削除開始前に「完全削除へフォールバックしない」ことを保証する条件として、次を調査対象とする。いずれも現時点で確立していない。

| 候補 | 内容 | 現状 |
|---|---|---|
| G3-a | `PreDeleteItem` の dwFlags に 0x80 がなければ `E_ABORT` | **必要条件の観測としてのみ扱い、事前保証には数えない**。理由: dwFlags は削除中に設定・変更されうる(R-A5、一次資料)ため、Pre 時点で 0x80 があっても最終的にごみ箱へ送られる保証にならない。ALLOWUNDO を外した場合に 0x80 が消えることは [過去実機]。ボリューム側のフォールバックで消えるかは [未確認](R-A6)。Pre でのエラー戻り値が実際に削除を止め、対象が無傷で残るかは [未確認](R-A4、M-16) |
| G3-b | ボリュームのごみ箱設定(無効化、最大容量)を事前に読み、ファイルサイズと比較 | 設定の取得手段と、それが Shell の判断と一致するかは [未確認](要一次資料・要実機) |
| G3-c | ドライブ種別(固定以外、リモート、UNC を除外) | 必要条件にはなるが十分条件ではない [推測・設計](固定ドライブでもごみ箱無効・容量超過がありうる) |
| G3-d | `FOFX_RECYCLEONDELETE` | 文書上の意味は「ごみ箱へ送り、完全削除しない」(R-A7、一次資料)。フォールバック時に実際に完全削除を止めるか(失敗として返るか)は [未確認](要実機) |
| G3-e | フラグの組合せ: `FOF_NO_UI`、`FOF_NOCONFIRMATION`、`FOF_NOERRORUI`、`FOFX_EARLYFAILURE`、`FOFX_RECYCLEONDELETE`、`FOF_WANTNUKEWARNING`(および `FOF_ALLOWUNDO`) | 各組合せが、ごみ箱へ送れない場合の完全削除へのフォールバックを防げるかを、P0-7 で M-01〜M-06 の環境ごとに確認する。`FOF_WANTNUKEWARNING` は `FOF_NOCONFIRMATION` を部分的に上書きする(R-A7、一次資料)。`FOF_NO_UI` が `FOF_NOCONFIRMATION` を含むかは [未確認](要一次資料)。**安全なフラグ集合は推測で確定しない** |

G3 の判定基準(案): 手動確認 M-01〜M-06 と M-16 のすべての環境で、候補の組み合わせが「完全削除の前に必ず止める」ことを観測し、かつ一次資料の記述がその挙動と矛盾しないこと。1つでも完全削除が起きた、または観測できない環境がある場合、その環境を事前に判別して除外できなければ不成立。**事後検出(Post の `psiNewlyCreated`==NULL、Post の dwFlags)は判定の根拠に数えない**(§10.1)。M-06(長いパス)は、ごみ箱へ送れないことによる完全削除へのフォールバックが起こらないことの確認対象として含める。

#### 4.7.2 1件ずつの操作

- 1ファイルにつき1つの `IFileOperation` インスタンス、専用 STA スレッド
- フラグ: **未確定**。候補の出発点は旧案 `FOF_ALLOWUNDO|FOF_NO_UI|FOFX_EARLYFAILURE|FOF_NO_CONNECTED_ELEMENTS` だが、`FOF_NO_UI` が `FOF_NOCONFIRMATION` を含む場合に完全削除の警告を抑止しうる点(R-A7)を含め、G3-e の確認結果で決める。`FOF_NO_CONNECTED_ELEMENTS` は R-A10(M-12)の確認後
- 渡すパス(Shell 内部でパスが再解決されるため、§7.2 の単一成分解決の契約はこの段に及ばない。S-17): 保持ハンドルの最終パスから作る(R-B9 により `\\?\` を除いた形)。パス経由であることは §16 の残存リスクのとおり
- 保持: 再検証に使ったファイルハンドル(共有 `READ|DELETE`)と祖先ディレクトリのハンドル

#### 4.7.3 状態機械

```
Pending
  └─ PerformOperations 開始
       ├─ PreDeleteItem(psiItem)
       │    ├─ psiItem が依頼した項目でない(関連項目の追加等)     → E_ABORT, 記録: ExtraItem
       │    ├─ 0x80 なし                                          → E_ABORT, 記録: NoRecycleFlag
       │    ├─ psiItem の ID ≠ 保持ハンドルの ID                   → E_ABORT, 記録: IdentityMismatchAtShell
       │    ├─ 保持ハンドルの属性に READONLY                        → E_ABORT, 記録: ReadOnlyAtShell
       │    └─ それ以外                                            → S_OK, 状態: Approved
       ├─ PostDeleteItem(dwFlags, hr, psiNewlyCreated)              (dwFlags は記録する。R-A5)
       │    ├─ SUCCEEDED(hr) かつ psiNewlyCreated ≠ NULL            → 状態: ReportedRecycled
       │    ├─ SUCCEEDED(hr) かつ NULL                             → 状態: ReportedPermanent
       │    └─ FAILED(hr)                                          → 状態: ReportedFailed
       └─ PerformOperations 戻り + GetAnyOperationsAborted を必ず呼ぶ
最終判定(保持ハンドルで照合する)
```

| 観測 | 保持ハンドルによる照合 | 最終結果 |
|---|---|---|
| ReportedRecycled | 最終パスが同一ボリュームの `$Recycle.Bin` 配下、かつ `psiNewlyCreated` の ID = 保持ハンドルの ID、かつ削除保留でない、かつ Post の dwFlags に 0x80 がある | **Recycled(削除済み)**。ここでのみ「削除済み」とする |
| ReportedRecycled | 上記の ID・位置の条件のいずれかが不成立 | 内部安全性エラー `IdentityMismatch`(別の対象を操作した可能性) |
| ReportedRecycled | ID・位置は成立するが、Post の dwFlags に 0x80 がない | 内部安全性エラー `UnknownShellOutcome`(dwFlags の意味が文書で確定していないため、結果を説明できない状態として扱う。M-01〜M-06 の観察で再評価) |
| ReportedPermanent、または保持ハンドルが削除保留 | — | 内部安全性エラー `PermanentDeletionOccurred` |
| ReportedFailed / Pre で中止(NoRecycleFlag、ReadOnlyAtShell) | 最終パスが元の位置(保持中の親個体の子として同じ名前で同じ ID)、かつ削除保留でない | 当該ファイル `ERROR`(削除されていない。S-05 の「説明のつく失敗」) |
| ReportedFailed / Pre で中止(NoRecycleFlag、ReadOnlyAtShell) | 元の位置にない、または位置を確認できない(保持ハンドルが削除保留の場合は上の `PermanentDeletionOccurred` の行が優先) | 内部安全性エラー `UnknownShellOutcome`(失敗が報告されたのに対象が動いた・消えた可能性があり、説明がつかない) |
| Pre で中止(ExtraItem、IdentityMismatchAtShell) | — | 内部安全性エラー `IdentityMismatch`(§8 S-01) |
| Post 欠落、結果取得失敗、`GetAnyOperationsAborted`=True で説明がつかない、Post の重複(**説明のつかない結果**) | 元の位置にあるかを問わない | 内部安全性エラー `UnknownShellOutcome`(独立 reason)。以降の削除を中止し、終了コード 3(S-05 決定済み)。元の位置にあれば、削除されていないことを表示に含める。完全削除の可能性を排除できない状態として扱い、`PermanentDeletionOccurred` とは断定しない |
| 同上 | 元の位置になく、ごみ箱内の照合もできない | 同上(`UnknownShellOutcome`)。所在不明であることを表示に含める |

内部安全性エラーは以降の削除を直ちに中止し、終了コード 3 [§12.6]。

### 4.8 内部安全性エラーの reason [§12.6、#44、#53]

```csharp
public enum InternalSafetyReason
{
    PermanentDeletionOccurred,       // 完全削除の発生(§10.1 の保証の破綻)
    IdentityMismatch,                // 対象の同一性の不一致(別の対象を操作した/しうる)
    UndetectedArchiveRejection,      // 削除前の構造検証で検出されなかった archive-level rejection 相当の異常
    UnknownShellOutcome,             // Shell の結果を説明できない(Post 欠落・重複、結果取得失敗、説明のつかない GetAnyOperationsAborted、
                                     // 失敗の報告後に対象が元の位置にない等)。元の位置にあるかを問わない。完全削除が起きたと断定しない(S-05、H-06)
}
```

- いずれも以降の削除を中止し、終了コード 3。表示と記録で reason を区別する
- `UnknownShellOutcome` は SPEC §12.6 の3分類(「少なくとも」)に加える独立 reason である。観測していない完全削除を `PermanentDeletionOccurred` として記録・表示しない。完全削除の可能性を排除できない状態であることを表示に含める。§12.6 の3分類のどれにも断定しない(S-05 決定済み。追加は「少なくとも」の範囲内と解釈する)
- `UndetectedArchiveRejection` の検出点: 削除フェーズの再検証で、構造検証時と異なる構造情報(LH の再読込結果の不一致等)が得られた場合。テストではフックで注入する(#44)

### 4.9 リソース制限 [§6.1]

| 上限 | 超過時 | 値 | 根拠の状態 |
|---|---|---|---|
| エントリ総数 | archive-fatal | SPEC 初期案 100,000 | 測定で確定する。旧測定(R-Z10)は名前長不明のため根拠にしない |
| 1エントリの展開量 | 当該エントリ `ERROR` | SPEC 初期案 16 GiB | 処理時間の上限としての妥当性を測定で確認 |
| メタデータ総量(CD のバイト数+展開後の名前の総量) | archive-fatal | **未定** | 測定前。開発中は仮値をテストで注入する |
| 1エントリの名前の長さ(バイト) | archive-fatal | **未定** | 同上 |
| パスの深さ(成分数) | archive-fatal | **未定** | 同上 |

- 具体値は、測定条件(エントリ数 N、名前長 L、深さ D、圧縮方式、キャッシュ状態、マシン)を明記した実測が得られるまで確定値として扱わない
- これらの上限は性能のための値であると同時に、**MVP が受理する入力の範囲を定める互換性上の上限**である。値の決定は受理範囲の決定でもあるため、人間の判断事項とする(§14)
- 上限はすべて構造検証フェーズで、エントリの内容を処理する前に検査する [§6.2-3]
- 定数はテストで小さい値に差し替えられる構造にする(#10、#31)

### 4.10 Ctrl+C とキャンセル(設計検討。SPEC 要件ではない)

SPEC はキャンセルの挙動を定めていない。本節は検討であり、案の確定は削除 API のキャンセルポイントと状態遷移の調査(R-A17)の後に行う。

- 調査項目: `PerformOperations` 実行中のキャンセル手段(Sink の戻り値、`UpdateProgress` 等)、キャンセル後に Post が呼ばれるか、`GetAnyOperationsAborted` の値、`Console.CancelKeyPress` と STA スレッドの関係、disposition 削除の途中での中断
- 暫定の方向性(未決定): 分類・確認待ち中のキャンセルは何も削除せず終了。削除中はファイルの境界でのみ停止し、処理中の1件は §4.7.3 の最終判定まで完了させる
- 終了コードの対応は SPEC にない(§8 S-04)

### 4.11 CLI と配布 [§4、§13、§14]

- 引数解析は小規模な自前実装(オプション4つ)。未知のオプションはエラー
- TTY 判定は `Console.IsInputRedirected` [推測・設計]。非 TTY かつ `--yes` なしは終了コード 2
- 確認プロンプトは `[y/N]`。`y`/`Y` 以外(空入力、EOF を含む)は中止し、何も削除せず終了コード 2 [§4]。X-25
- 出力は §13 の形式。archive-level rejection では分類結果を出さない
- 配布: **self-contained single-file を暫定第一候補とする(H-12。採用決定ではない)**。理由 [推測・設計]: G1 で検証したランタイムを配布物に固定できる。framework-dependent では利用者側のランタイムの `DeflateStream` が使われ、G1 の検証と一致しない可能性がある。self-contained single-file は [過去実機] でごみ箱送りまで動作した。P0-6 で、ランタイム固定の意味、ネイティブ圧縮ライブラリの扱い(同梱・抽出)、更新・脆弱性修正時の再配布への影響を確認し、その結果を受けて最終決定する。**NativeAOT は未検証であり採用条件にしない**。実機確認(M-15)の後に判断する
- 長いパス: アプリ manifest に `longPathAware` を入れるかは任意。H-1 では成分単位で開くため影響が小さい [推測・設計]。Shell に渡すパスの長さは R-B9、R-B11

---

## 5. archive-level rejection の分類表 [§12]

分類原則 [§12.4]: ある異常を entry-local としてよいのは、当該 entry 以外の境界・metadata・参照位置・内容検証の独立性を損なわないと確認できる場合に限る。確認できなければ archive-fatal。迷うものは archive-fatal。

「APPNOTE 上可能」は、ZIP の仕様上は正当でありうるという意味で、unsupported(§12.5)の判断とは別である。APPNOTE で確認できた事実と記載がない事項は §3.4.1。

**表の理由づけについて**: 本表の archive-fatal の理由は「unextract が構造を安全に解釈できない(境界・metadata・参照位置・独立性を確定できない)」ことであり、「ZIP 規格違反」と断定するものではない。特に F15、F19、§5.1.1 は APPNOTE が要求する規則ではなく、SPEC §12.4・§12.5 を決定論的かつ安全側に実装するための unextract 独自の受理規則である(S-15、S-16 確定)。表示文言も「規格違反」とは書かない。

### 5.1 archive-fatal(§12.2)

| # | 異常 | 検出フェーズ | 根拠(§12.4) |
|---|---|---|---|
| F1 | EOCD が見つからない / ZIP として読めない | 構造 | 全体の構造不明 |
| F2 | EOCD 候補が複数あり一意に決まらない | 構造 | 参照位置が曖昧 |
| F3 | EOCD の宣言コメント長がファイル末尾を超える(切断)。または EOCD 位置から末尾までの長さと一貫せず、余剰データとして認識できない場合(§5.1.1、S-15) | 構造 | EOCD の境界不明 |
| F4 | Zip64 locator / Zip64 EOCD の欠落・不整合、0xFFFF/0xFFFFFFFF の番兵があるのに Zip64 情報がない | 構造 | metadata 不信 |
| F5 | CD のオフセット・サイズが範囲外、算術的にあふれる | 構造 | 参照位置不信 |
| F6 | CD の切断、レコード署名不正、宣言件数と実レコード数の不一致(件数の詐称) | 構造 | 境界不信 |
| F7 | CD の末尾と EOCD(または Zip64 EOCD)の間に未説明のバイト | 構造 | 境界不信 |
| F8 | エントリの LH オフセット・サイズが範囲外、またはあふれる | 構造 | 参照位置不信 |
| F9 | LH の署名不正、LH の名前長・extra 長が範囲外 | 構造(全件) | 境界不信 |
| F10 | LH と CD の不一致(名前バイト、方式、フラグ(bit3 以外の差を含む)、サイズ・CRC(bit3 なしの場合)、UTF-8 フラグ) | 構造(全件) | どちらの metadata を信じるか決まらない |
| F11 | 圧縮データの範囲が他のエントリの LH/データ、CD、EOCD と重なる。複数の CD レコードが同じ LH を指す | 構造(全件) | 独立性不信(#35) |
| F12 | Data Descriptor の位置・形式(署名の有無、32/64bit)が一意に決まらない、または DD の値が CD と不一致 | 構造(全件) | 境界不信(#36) |
| F13 | Zip64 extra の欠落・長さ不正・CD と LH の Zip64 値の矛盾 | 構造(全件) | metadata 不信(#36) |
| F14 | extra field の TLV 構造が壊れている(長さが extra 領域を超える等) | 構造(全件) | Zip64 等の解釈に影響しうるため独立性を確認できない(迷う → fatal) |
| F15 | エントリ間・先頭エントリ前(SFX/余剰データとして認識できないもの)に、どのエントリにも属さないバイト | 構造 | unextract がそのバイトの役割を安全に解釈できない(APPNOTE はこれを禁じも定義もしない。§3.4.1)。迷う → fatal(S-07 決定)。表示は未参照のバイトがあることが分かる文言とし、「規格違反」とは書かない |
| F16 | エントリ総数、メタデータ総量、名前長、パス深さの上限超過 | 構造 | §6.1 |
| F17 | ディスク番号等が分割形式を示すが、分割形式として一貫しない | 構造 | 認識できない → fatal(§12.5 末尾) |
| F18 | 未知の汎用ビットフラグのうち、境界や暗号化に関わるもの(bit13 等)が一貫しない | 構造 | 迷う → fatal |
| F19 | Stored(方式0)・非暗号化で、**解釈に使用する**宣言圧縮サイズ ≠ 宣言展開サイズ(bit 3 なしでは LH と CD の値、bit 3 ありでは DD と CD の値。bit 3 ありの LH のサイズ・CRC が 0 であることは矛盾として扱わない) | 構造(全件) | **確定(S-16)**。理由は APPNOTE の規則ではない(APPNOTE は「圧縮サイズ = 展開サイズ」を明記していない。§3.4.1)。unextract が圧縮データの境界(圧縮サイズ基準)と期待展開量(展開サイズ基準)を独立して安全に確定できないため、§12.4 に基づく保守的な受理条件として fatal とする。暗号化された Stored は対象外(暗号化ヘッダーにより両者が異なるのが正常。APPNOTE 4.4.8、6.1.3)。§12.3 の「展開サイズと宣言サイズの不一致」は**実際の出力量**と宣言の不一致であり別物 |

#### 5.1.1 archive-fatal と unsupported の判別規則(確定。S-15)

§12.5 は「認識できない・曖昧で何の形式か判断できない場合は archive-fatal」と定める。以下は、その認識条件を決定論的にするための規則であり、P0-3b で案 B として確定した(§14.5)。**これは PKWARE APPNOTE が要求する ZIP 規格ではなく、SPEC §12.5 を決定論的かつ安全側に実装するための unextract 独自の受理規則である**(APPNOTE には先頭・末尾の余剰データやエントリ間の未参照バイトの扱いの規則がない。§3.4.1)。SPEC の決定事項ではない。規則に当てはまらない入力を fatal とする理由は「ZIP 規格違反」ではなく、「unextract が構造を安全に解釈できない」ことである。

1. **EOCD の特定**: 末尾から EOCD 署名を探索し、候補が1つに決まる場合のみ先へ進む。複数なら F2
2. **末尾**: 宣言コメント長が末尾を超える → F3。宣言コメント長の範囲の後ろにバイトが残り、かつ ②以降の検査で CD とエントリの参照が一貫している → U3。参照が一貫しない → F3
3. **先頭**: 全エントリの LH・DD・CD の位置が、一定のずれ s(s=0 の絶対オフセットを含む)で一貫し、先頭エントリの前にバイトがある → U2。ずれが一定でない、または一貫しない → F15
4. **隙間**: エントリ間、最終エントリと CD の間、CD と EOCD の間の未説明バイトは U2/U3 に含めず F15 とする(§12.5 は「ZIP構造の前後」の余剰データのみ)
5. **併発**: fatal に該当する異常が1件でもあれば、unsupported ではなく fatal として表示する(§12.5 は同じ終了コード 3。表示 reason のみが異なる)
6. 複数ディスク(U1)、CD の暗号化(U4)は、ディスク番号・フラグが**一貫して**示している場合のみ unsupported。一貫しなければ F17、F18

### 5.2 unsupported archive format(§12.5)

構造は認識でき、APPNOTE 上ありうるが、MVP で意図的に受理しない。

| # | 形式 | 認識の条件(案) | 認識できない場合 |
|---|---|---|---|
| U1 | 複数ディスク(split / multi-disk) | EOCD / Zip64 EOCD のディスク番号が 0 以外で一貫している、または先頭に分割マーカー | F17 |
| U2 | 自己展開形式(SFX)/ 先頭の余剰データ | 先頭エントリの LH より前にバイトがあり、全オフセットがファイル先頭基準で一貫している | オフセットのずれ等で一貫しない → fatal |
| U3 | 末尾の余剰データ | EOCD の宣言コメント長の範囲を超えて末尾にバイトがあり、EOCD が一意に特定でき、全エントリ・CD の参照が一貫している(§5.1.1) | F2/F3 |
| U4 | Central Directory の暗号化 | 汎用ビットフラグ bit13 等が一貫して示している。bit 13 が CD 暗号化時に LH の値をマスクすることは確認済み(APPNOTE 4.4.4)。CD 暗号化の構造の詳細(7.2 以降)は未読のため、認識条件の根拠は P1-3 の前に確認する(O-15) | F18 |

表示は §13 の unsupported 形式。終了コード 3(#37、#46)。

### 5.3 entry-local error(§12.3)

構造検証を通過したエントリの、内容検証だけの失敗。当該エントリ `ERROR`、他の `MATCHED` は有効。

| # | 異常 | 検出 | 独立性の確認根拠 |
|---|---|---|---|
| L1 | 暗号化(bit0、bit6) | 構造時に判明し、分類で ERROR | 境界は CD/LH で確定済み |
| L2 | 未対応の圧縮方式(Stored / Deflate 以外) | 同上 | 同上 |
| L3 | CRC 不一致 | 内容 | 自エントリの範囲内 |
| L4 | 圧縮ストリームの正常終端の欠落(終端未確認を含む) | 内容 | 同上 |
| L5 | 展開サイズと宣言サイズの不一致、宣言超過 | 内容 | 同上 |
| L6 | 1エントリの展開量の上限超過(宣言で判明する場合は展開しない) | 構造/内容 | 同上 |
| L7 | Deflate データの不正(不正な符号等) | 内容 | 自エントリの範囲内(範囲は構造で確定済み) |
| L8 | 圧縮範囲内で最終ブロック後に未消費バイトが残る | 内容 | 範囲は CD/LH で確定済み。観測できる場合のみ検出し、観測不能は Z-B の理由にしない(S-08、K-15) |

注: パスの拒否(`UNSAFE_PATH`)と曖昧(`AMBIGUOUS`)はエントリの分類であり、本表の対象外 [§7.1、§8]。

注: L1、L2、L6(宣言で判明する場合)は構造フェーズで判明するが、§12.3 末尾は「展開を要するため、サイズが一致して比較に進んだエントリについてのみ確認すればよい」と定める。これらと `MISSING`、`MODIFIED`(サイズ不一致)、`SKIPPED_SPECIAL_FILE`、`UNSAFE_PATH`、`AMBIGUOUS` が同時に成立しうる場合の分類の優先順位は、SPEC から一意に導けるかを含め **S-21(未決定)**。本書は外部カテゴリを追加・変更しない。いずれの順位でも、該当エントリは削除されない(`MATCHED` にならない)ことは変わらない [推測・設計]。

### 5.4 内部安全性エラー(§12.6)

削除開始後に判明した、構造検証で検出されなかった異常 → `UndetectedArchiveRejection`(§4.8)。内部安全性エラーの reason は全4種: `PermanentDeletionOccurred`、`IdentityMismatch`、`UndetectedArchiveRejection`、`UnknownShellOutcome`(§4.8)。

---

## 6. 手動確認手順

→ `PLAN_VALIDATION.md` §6 へ移動した(M-01〜M-18、共通条件、R-A4 と M-16 の注記)。同節の次の1文は、ゲートとの関係のため本書にも残す:

M-01〜M-06 と M-16 は G3 の判断材料。M-07/M-08 は G2b の判断材料。M-09/M-11/M-14/M-17 は G2 の判断材料。M-18 は S-19 の判断材料。

---

## 7. SPEC v4 との対応表

### 7.1 節ごとの対応

| SPEC | 要件(要約) | PLAN の該当節 | 充足方法(実装案)/ 状態 |
|---|---|---|---|
| §1 目的 | 検証可能な一致だけを削除 | 全体 | 分類器が `MATCHED` 以外を Deleter に渡さない構造 |
| §2 原則 | Fail Safe、終了コード 3 の範囲、ストリーミング、再帰削除禁止、ごみ箱の事前保証 | §4.1、§4.7、§4.11、§12 | BannedApiAnalyzers で再帰削除 API を禁止。G3 |
| §3 対象 | Windows 11、C#、LTS 固定、OSS 構成 | §10 Phase 1/4、§12 | `global.json`、LICENSE、README、CI |
| §4 CLI | オプション(`--delete-permanently` を含む)、確認、非 TTY、target 拒否、最終成分 reparse は解決前 | §4.2、§4.11 | T1〜T8(クラウド識別機構の初期化は T2 で、target の観測より前)。`--delete-permanently` を PLAN 側で MVP から外さない(V-04) |
| §5 カテゴリ | 7カテゴリ、内部 reason は可 | §4.4、§4.5、§4.8 | reason `NamedDataStream`、`ReadOnly`、`RecycleNotGuaranteed` 等 |
| §6 同一性 | サイズ → ストリーム比較 → 終端・サイズ・CRC | §4.1、§4.4 | 終端確認は G1 |
| §6.1 リソース | ストリーミング、各上限 | §4.9 | 値は測定待ち |
| §6.2 能力 | 4能力 | §4.1.1、§10 G1 | 撤退条件つき |
| §7.1 拒否 | 正規化前ルール | §4.3(`PathComponent`) | Core の検証器 |
| §7.2 解決 | 個体起点の単一成分解決、文字列比較を包含に使わない | §4.2、§4.3 | H-1 / H-2、G2。**ごみ箱送りの最終段は Shell のパス再解決を含み、§7.2 の充足は確定しない。G2 の判定範囲に含め、P0-4 で調査する(S-17、H-10)** |
| §7.3 reparse・クラウド | 全中間・対象の検査、識別前に読まない、初期化失敗 3、個別失敗 ERROR、ZIP 側の symlink 属性 | §4.2、§4.4、§4.5.3 | M-07/M-08。識別方式は R-C4 により P0-4 で検証(S-20)。ZIP 側の属性は S-10(安全原則は確定、具体的な認識集合は根拠確認後。O-14) |
| §7.4 名前付きストリーム | ファイル・ディレクトリとも削除しない、確認不能は ERROR | §4.5.1 | M-10 |
| §8 曖昧 | 重複・大小違い・ファイル/ディレクトリ衝突 | §4.3(Core)、§4.4 | 正規化成分列を大文字小文字無視でグループ化。暗黙のディレクトリとの衝突は S-23、ファイルシステム上の別名による同一個体への解決は S-18 |
| §9 文字コード | UTF-8 フラグ / CP437 | §4.1.3(生の名前バイトを保持し自前でデコード) | 不正 UTF-8 は UNSAFE_PATH |
| §10.1 ファイル削除 | 再検証、読み取り専用 ERROR、ごみ箱既定、事前保証、TOCTOU 調査 | §4.5.2、§4.7、§10 G3 | 保証未確立の間は削除しない |
| §10.2 ディレクトリ | 祖先個体の同一性、子から親、非再帰、空・reparse・ストリーム | §4.6 | ハンドルの連続保持 |
| §11 Dry Run | 不変、削除予定ディレクトリ、候補のみ列挙、rejection も同じ | §4.4-8 | 書き込み系 API を呼ばない構造。読み取りに伴うメタデータ更新との関係は S-19、クラウド識別のための列挙との関係は S-20(いずれも未決定) |
| §12 ZIP | rejection の種別と分類 | §4.1.2、§5 | 構造検証フェーズ |
| §12.6 内部安全性エラー | 中止、3、reason 分離 | §4.7.3、§4.8 | 4 reason(SPEC の3分類 + `UnknownShellOutcome`。S-05) |
| §13 出力 | 形式 | §4.11 | スナップショットテスト |
| §14 終了コード | 0〜3 | §4.2、§4.7、§4.11 | |
| §15 やらないこと | | 全体 | 無関係ファイルを列挙しない(§4.4-8)。ZIP 自体を削除しない(§4.4 手順3、X-22) |
| §16 README | 文言 | §9.2 | §16 の文言を正とする |
| §17 テスト | #1〜#54 | §11 | 全件対応 |
| §18 実装方針 | 構成、抽象、CsWin32、方式は PLAN で選ぶ | §4.0、§4.1、§4.3 | |
| §19 完成条件 | dry-run → 実行、ごみ箱へ送られる | §10 Phase 4 | **G3 不成立の場合、§19 の「ごみ箱へ送られる」を満たせない** → §13.2 |

### 7.2 要件の充足状況の要約

- 充足の見込みがある(実装案あり、未確認事項は実装中に確認可能): §1、§3、§4、§5、§7.1、§8、§9、§11、§13、§14、§15、§17
- ゲート判定まで充足が確定しない: §6/§6.2(G1)、§7.2(G2。ごみ箱送りの最終段を判定範囲に含む。S-17、H-10。H-1 の成立は P0-4 の V1〜V8)、§7.3/§7.4(G2b。R-C4、S-20)、§8(S-18、S-23)、§10.1(G3)、§10.2(G2、M-14)、§11(S-19、S-20)、§19(G3)

---

## 8. SPEC との矛盾・要確認事項

→ `PLAN_DECISIONS.md` §8 へ移動した(S-01〜S-23 と各行の決定状況)。未決定・保留の S 項目の判断時期は `PLAN_DECISIONS.md` §14.3、§14.6。S-18 の撤退条件は本書の §4.4 手順7、§10.1.2、§10.2(P0-4、P1-8)にも記載がある。

---

## 9. リスク一覧と README 記載案

→ `PLAN_DECISIONS.md` §9 へ移動した(§9.1 K-01〜K-20、§9.2 README 記載案)。

---

## 10. フェーズ別タスクとゲート

### 10.1 ゲート一覧

| ゲート | 位置 | 成立条件 | 不成立時 |
|---|---|---|---|
| G0 | Phase 1 開始前 | 次の (a)〜(e) がすべて満たされた時点で成立する。(a) SPEC/TASK/PLAN の独立レビューの完了、(b) P0-3 の結果の PLAN への反映、(c) P0-3b の決定の PLAN への反映、(d) G0 前に必要な人間の判断の完了、(e) 後続ゲートへ意図的に送った未確認事項の明示。**P0-4、P0-6、G1、G2、G2b、G3 で確認すべき事項が残っていること自体は、G0 不成立の理由にしない**。現在の判定は §10.1.2 | Phase 1 を開始しない |
| **G1** | Phase 1 の ZIP 方式確定時(ZIP 方式に依存しない P1 基盤の後、ZIP 依存の Core の前。§10.1.1) | 採用方式が §6.2 の4能力をすべて満たすことを、fixture テストで示す(終端欠落 #34、超過 #8、事前上限 #10・メタデータ上限、全件構造検査 #32/#35/#36)。`v10.0.12` タグのソースで終端確認の根拠を確認。正常 ZIP の fixture が入力 EOF の保守判定(§4.1.1)によって過剰に `ERROR` とならないこと | 撤退条件(§4.1.1)に従う: 別方式を検討し、それでも満たせない場合は SPEC を変更せず Phase 1 のゲート未達として停止、§13.2 として人間に戻す |
| G2 | Phase 2 の解決層実装前 | H-1(または人間が適合を認めた代替)で §7.2 を満たし、#41、#42、#47、#48 が実 FS で通る見込みをスパイクで示す。**ごみ箱送りの最終段(Shell のパス再解決)も判定範囲に含み、§7.2 を満たし得る手段が P0-4 で見つかること(S-17、H-10)** | 停止し §13.2。最終段で手段が見つからない場合は G2 を通過扱いにせず、§7.2 の解釈または SPEC 変更の要否を人間に戻す(V-07) |
| G2b | Phase 2 のクラウド・ストリーム実装前 | M-07/M-08 で識別範囲を確認、M-10 でストリーム検査を確認 | 識別できない状態を除外する手段がなければ停止し §13.2 |
| **G3** | Phase 3 の Recycle 実装着手前(判断材料の取得と判定は Phase 0 の P0-7 に前倒しする) | 完全削除へフォールバックしないことを**削除開始前に保証する条件**を、M-01〜M-06、M-16 と一次資料で確立(§4.7.1)。事後検出は条件に数えない。P0-7 で成立と判定しても、Recycle 実装着手前に再確認する | **Recycle 機能を MVP から除外する**(既定モードは削除しない。全 `MATCHED` を `ERROR`、reason `RecycleNotGuaranteed`)。**`--delete-permanently` は SPEC §4 で定義され #27・#49 で試験されるため、G3 不成立を理由に PLAN 側で MVP から外さない**。外す必要が生じた場合は SPEC 変更として人間に戻す(V-04)。§19 との関係は §13.2。不成立の場合、既定モードのファイル実削除経路が存在しなくなるため、**Phase 3 の通常計画を開始せず**、V-04 として人間に戻す(影響を受ける SPEC テストは §11.4) |

保証条件が確立していない間の既定動作は「削除しない」(§2、§10.1)。G3 の判定前に Recycle 経路で削除するコードを有効にしない。

注: TASK.md「必ず反映する事項 1」は「`--delete-permanently` を MVP に残すかは、その時点で別途決定する」とする。第4改訂では、これを「PLAN 側の判断で外すことはできず、外す場合は SPEC 変更として人間が決める」と読み替えて記載した(独立レビュー H7 の採用。人間の指示による)。

### 10.1.2 G0 の現在の判定(第4改訂時点)

| 条件 | 状態 | 根拠 |
|---|---|---|
| (a) 独立レビュー | **済** | 第3改訂に対する独立レビューに加え、第4改訂に対する最終監査が完了。最終監査で指摘された #5・#7・#23・#26・#33 の task/phase 割当、X-22 の実削除側、§11.4 の #23、S-18 の撤退条件を本改訂で反映した。範囲は4文書の照合であり、一次資料・実機による追加検証は含まない(H-09) |
| (b) P0-3 結果の反映 | **済** | §3(区分の更新)、§3.4.1、§4、§5、§9 |
| (c) P0-3b 決定の反映 | **済** | §14.5、§8(S-06、S-10、S-15、S-16)、§3.4(R-Z14)、§4.3.2(V1〜V8)、§4.5.3(C-a〜C-c)、§4.1.1(R-Z7) |
| (d) G0 前に必要な人間の判断 | **済** | §14.1 と §14.5 の判断で、G0 前に必要とされたものはすべて決定済み。新設の S-18〜S-23 は判断時期を G0 より後(P1-6、P1-8、P2-5、G2、G2b)に置いており、G0 前の判断を要しない(§8 の各行、§14.6) |
| (e) 後続ゲートへ送った事項の明示 | **済** | §13.1(O-01〜O-16)、§13.2(V-01〜V-09)、§14.6 |
| P0-3 範囲漏れの個別判定 | G0 の阻害要因ではない | O-15 の各項目を個別に判定(§13.1) |

**再判定: G0 は PASS(条件 (a)〜(e) を満たす)**。第4改訂の最終監査で指摘された割当と S-18 の撤退条件を反映した。P0-4 はこの修正と独立に開始可能。Phase 1 は本修正と G0 再判定の完了後に開始可能とする。S-18 の検出方法は P0-4/M-17 の結果が出るまで未確立であり、確立できなければ P1-8 を BLOCKED とする(§10.2)。

### 10.1.1 ゲートと実装着手の依存

```
P0-1 → P0-2(§14.1) → P0-3 → P0-3b(§14.2 の最終確認) → G0 → P1 基盤 → [P1-3, P1-4: ZIP 方式の候補実装と検証] → G1
                                                                   │
        G1 → ZIP 依存 Core(P1-8, P1-9, P1-10) → G2/G2b 判定済み → Windows 依存実装(Phase 2)
                                                                   │
        G3 成立(P0-7 で前倒し、着手前に再確認) → Phase 3 開始
        G3 不成立 → Phase 3 の通常計画を停止 → V-04 → 人間が新しい Phase 3 構成を決定

並行: P0-4 → G2 の材料、P0-5 → 診断手段、P0-6 → G1 の予備判断、P0-7 → G3 の判断
```

第4改訂時点: P0-1(独立レビュー)、P0-2、P0-3、P0-3b は完了。G0 は §10.1.2 のとおり。P0-4〜P0-7 は未着手。

| 区間 | 実装してよい | ゲート通過前に実装・有効化してはならない |
|---|---|---|
| G1 の前 | P1-1(雛形)、P1-2(`RawZipBuilder`)、P1-5(名前デコード・パス検証)、P1-6(曖昧性)、P1-7(抽象と Fake)、P1-3/P1-4(G1 の証拠となる ZIP 方式の候補実装) | 採用方式を前提にした分類器(P1-8)、dry-run 出力の確定(P1-9)、上限値の確定 |
| G2/G2b の前 | P2-1(CsWin32 導入)、P0-4 のスパイク | 解決層(P2-3)、target 初期化(P2-2)、クラウド・ストリーム検査(P2-4)の本実装 |
| G3 の前 | P3-1、P3-2、P3-3 の **Fake 上の先行実装とテストのみ**(「実装してよい」であり「Phase 3 を開始してよい」ではない) | Phase 3 の開始、実 FS 上のディレクトリ削除結合(P3-2 実 FS 側)、Recycle 経路で実際に削除するコード(P3-4)の有効化、`--delete-permanently` の実装(P3-5) |

### 10.2 タスク

| ID | タスク | 完了条件 | 依存 |
|---|---|---|---|
| **Phase 0: 調査** | 開始条件: 人間の指示(別セッション) | | |
| P0-1 | 本 PLAN の独立レビュー(範囲: 文章・内部整合性。一次資料による事実検証を含まない。H-09)。事実検証は P0-3 以降 | レビュー結果に基づく PLAN の再修正。**完了**(第3改訂へのレビュー反映に加え、第4改訂の最終監査とその指摘の修正を完了。§10.1.2) | — |
| P0-2 | §14.1 の第1段階の決定(H-01 の P0-3 実施許可、H-08 を含む) | 人間が決定済み(本改訂で記録) | P0-1 |
| P0-3 | 一次資料の再確認([旧一次資料要約] の全項目、R-A9、R-A10、R-B14、R-B16、R-B17、R-C2、R-C4、R-C6、R-Z13(Stored、bit 3、Data Descriptor、LH/CD のサイズ欄、external attributes を含む)、R-Z14)。外部取得は TASK の5系統に限る。dotnet/runtime は `v10.0.12` タグで確認 | 各項目に文書名+節+確認日を付けて区分を更新。5系統以外が必要になった場合は人間へ戻す。**完了**(2026-10-01。結果は `docs/P0-3_results.md`(現 `PLAN_HISTORY.md`「P0-3 調査記録」)、区分の更新は第4改訂 §3)。範囲漏れは後続調査として O-15 に記録 | P0-1、P0-2(H-01、H-08) |
| P0-3b | P0-3 に依存する暫定決定の最終確認(S-10、S-15、S-16、H-08 の出典記録)。P0-3 で新たに判明した事項(S-06、R-B14/R-B16、R-C4、R-Z7)の扱い | 人間が判断。**完了**(§14.5) | P0-3 |
| P0-4 | スパイク(Windows 実機。人間側の Windows 検証環境): 単一成分解決(H-1)と H-3 の検証項目 **V1〜V8**(§4.3.2。`NtCreateFile` の `RootDirectory`、`FILE_DIRECTORY_FILE`+`FILE_OPEN_REPARSE_POINT`、`NtCreateFile` 由来ハンドルと `ReOpenFile`、`ReOpenFile` が名前を再解決しない前提、target 初期化ハンドルとの互換性、CsWin32 での生成、`OBJ_CASE_INSENSITIVE` の照合(S-06)、NT 名前空間での名前(S-22))、ディレクトリ保持と Shell 移動(M-14)、名前付きストリーム API(一次資料と実機。O-04)、**クラウド識別の候補 C-a〜C-c(R-C4。S-20 の判断材料)**、同一個体への別名の検出手段(M-17、S-18)、FILE_ID_INFO のクライアントでの取得可否(R-B6)、**ごみ箱送りの最終段で §7.2 を満たし得る手段(ハンドル由来の `IShellItem` の作成など。S-17、H-10)** | G2・G2b の判断材料と、S-06、S-20、S-22 の判断材料を記録する。**S-18 の完了条件**: File ID、解決後実名、8.3 alias、case alias、その他確認できる filesystem alias を用い、複数 entry または entry と ZIP 自身が同一 target object へ解決される場合に削除候補から除外できる検出方法を検証する。FAT 等で ID が変化する場合も含め、ID 不一致を別 object の証明にしない。安全原則を保証できる方法が確立できなければ P1-8 を BLOCKED とし、人間へ戻す。S-17 の判断が Phase 2 の可否を決めるため、Phase 0 の早い段階で行う | P0-3 |
| P0-5 | 診断手段の用意(ごみ箱送り1件の全観測を記録。Pre/Post の dwFlags、フラグ組合せの切り替えを含む。製品コードとは分離。Windows 実機。人間側の Windows 検証環境) | M-01〜M-06、M-12、M-16 を実施可能 | P0-3 |
| P0-6 | スパイク: `DeflateStream` の終端確認(Z-A の方法)をタグソースと fixture で確認。**`System.IO.Compression.UseStrictValidation`(R-Z7)の挙動を検証項目に含める**(このスイッチだけで §6.2-1 が成立するとは扱わない)。dotnet/runtime `v10.0.12` タグのコミット SHA を記録する(O-15)。H-02 の成立判定基準(正常 ZIP の fixture で正常エントリが `ERROR` にされる割合)を測る。**H-12 の確認を含む**: ランタイム固定の意味、ネイティブ圧縮ライブラリの扱い(single-file への同梱・抽出)、更新・脆弱性修正時の再配布への影響。実行環境を問わず試せる部分もあるが、配布対象は Windows であり、Linux での結果を Windows の確認の代替としない | G1 の予備判断。H-12 の最終決定の材料 | P0-3 |
| P0-7 | M-01〜M-06 と M-16 の実施(G3-e のフラグ組合せ、`FOF_WANTNUKEWARNING` を含む)と G3 の判定(診断手段 P0-5 を使う。Windows 実機。人間側の Windows 検証環境)。**個別許可制**: 変更内容、変更前状態、復元手順、復元確認方法を提示してから許可を得る(§14.4)。専用の検証環境・ボリュームを原則とする。M-18 の設定変更も同じ扱い | M-01〜M-06、M-16 の結果記録、G3 の成立/不成立の判定。安全なフラグ集合は観測結果に基づいてのみ提案する。不成立ならゲート不成立時の扱い(§10.1 G3)に従い人間へ | P0-3、P0-5、個別許可 |
| **Phase 1: Core + dry-run** | 開始条件: G0 | | |
| P1-1 | ソリューション雛形、`global.json`、`Directory.Build.props`、LICENSE、BannedApiAnalyzers | 空のビルド・テストが通る | G0 |
| P1-2 | `RawZipBuilder`(任意の LH/CD/EOCD/Zip64/DD、名前バイト、隙間・重なり・前後データ) | §5 の全行の fixture を生成できる | P1-1 |
| P1-3 | ZIP 構造検証(§4.1.2-1)と分類表(§5)。U4 の fixture の前に、CD 暗号化の認識条件の根拠を確認(O-15) | #10、#32、#35〜#38、#46、X-03、X-04、X-05、X-18、X-19 | P1-2 |
| P1-4 | 内容検証(展開、終端確認、超過、CRC) | #6、#8、#9、#31、#34、X-06。**#7・#33 は Core/Fake の範囲(entry-local の独立性の判定と、`RecordingDeleter` に渡る対象の確認)に限る**。実削除を伴う部分は Phase 3 以降(§11.4) | P1-3 |
| **G1** | ZIP 方式の判定 | §10.1 | P1-3、P1-4 |
| P1-5 | 名前デコードとパス検証(`PathComponent`) | #15〜#17、#45、#54、X-16 | P1-1 |
| P1-6 | 曖昧性 | #21、X-23。S-23 の判断が必要 | P1-5、S-23 |
| P1-7 | 抽象(§4.3。削除の抽象を含む)と Fake(`FakeFileSystem`、`RecordingDeleter`、`ScriptedDeleter`、`FakeDirectoryRemover`、クラウド・ストリーム注入) | 抽象のレビュー完了。X-01 | P1-1 |
| P1-8 | 分類器(§4.4)、削除予定ディレクトリのシミュレーション、同一個体への別名と ZIP 自身の除外(S-18) | #1〜#5、#11〜#14、#18、#39、#40、#43、#49、#51、#52(Fake)、X-02、X-15、X-17、X-21・X-22(Fake)。S-10 の認識集合の根拠(O-14)、S-18 の分類名、S-21 の判断が必要。**P0-4/M-17 で S-18 の安全原則を保証できる検出方法を確立できない場合は BLOCKED。K-17 を残存リスクとして受容して進めず、人間へ戻す(ZIP 自身を含む)** | G1、P1-6、P1-7、S-18、S-21、O-14、P0-4/M-17(S-18 の検出方法) |
| P1-9 | dry-run 出力・終了コード | §13 形式のスナップショット | P1-8 |
| P1-10 | 上限値の測定(N、L、D を変えた測定。条件を記録) | 測定表。値の決定は人間 | P1-3 |
| **Phase 2: Windows 実装** | 開始条件: Phase 1 完了、G2 | | |
| P2-1 | CsWin32 導入 | ビルドが通る(NativeAOT 互換は完了条件にしない) | P1-1 |
| P2-2 | target 初期化(§4.2) | #29、#30、#47、#48、X-12、X-24 | P2-1、G2 |
| P2-3 | 単一成分解決とノード(§4.3) | #19、#20、#26、#41、#42、X-21(実 FS) | P2-1、G2、S-06 |
| P2-4 | 名前付きストリーム、読み取り専用、クラウド識別 | #39、#40、#49、#51、#52(実 FS は可能な範囲) | P2-3、G2b |
| P2-5 | 実 FS での dry-run 結合 | #22、#23 と X-22 の dry-run 側(ZIP 自身を削除候補にしない)、#43、X-14。#23/X-22 の実削除後の不変性は P3-4/P3-5 | P1-9、P2-4、S-19 |
| **Phase 3: 削除** | 開始条件: Phase 2 完了、**G3 成立**(P0-7 の判定を着手前に再確認)。G3 不成立の場合は Phase 3 の通常計画を開始せず、V-04 として人間に戻し、人間が新しい Phase 3 の構成を決定する。なお P3-1〜P3-3 の Fake 上のロジック実装・テストは、Phase 3 の開始ではなく「G3 前の先行実装」として許容する(§10.1.1) | | |
| P3-1 | 再検証(削除直前のサイズ・内容・ID・属性・ストリーム・クラウド)と `VerifiedDeletionTarget` の生成 | #24、#25、X-13 | P2-3 |
| P3-2 | ディレクトリ個体の保持と削除(§4.6) | #11〜#14、#40、#50(実 FS)、X-11。Fake 上のロジックは G3 前に先行可。実 FS 側の結合は Phase 3 開始後 | P3-1、G3(実 FS 側) |
| P3-3 | 内部安全性エラーの4 reason(§4.8)と中止 | #44、#53、X-20 | P3-1 |
| **G3** | Recycle ゲートの判定(P0-7 で前倒し済み。着手前に再確認) | §10.1 | P0-7 |
| P3-4 | (G3 成立時のみ)ごみ箱削除と状態機械(§4.7)、実削除統合 | #5・#26: 削除不可対象が実FSに残る。#7: 当該 ERROR は残り、独立した MATCHED のみ実削除。#33: 先頭 MATCHED のみ実削除、後続CRC不一致は ERROR、終了コード1。#23/X-22: Recycle 経路の処理後も元ZIPが存在し、hash・更新日時が不変。#27、X-07〜X-10、X-26 | G3、P3-1 |
| P3-5 | `--delete-permanently`(SPEC §4 の定義どおり。G3 の成否にかかわらず計画に含める。外す場合は SPEC 変更として人間が決める。V-04)、実削除統合 | #5・#26: 削除不可対象が実FSに残る。#7: 当該 ERROR は残り、独立した MATCHED のみ実削除。#33: 先頭 MATCHED のみ実削除、後続CRC不一致は ERROR、終了コード1。#23/X-22: permanent 経路の処理後も元ZIPが存在し、hash・更新日時が不変。#27 後半、#49 | P3-1、G2。着手は Phase 3 の開始条件(G3 成立、または G3 不成立時に人間が V-04 で決める新しい Phase 3 構成)に従う |
| **Phase 4: 仕上げ** | 開始条件: Phase 3 完了 | | |
| P4-1 | CLI(確認、非 TTY、`--yes`、サマリー) | #28、X-25 | P3-* |
| P4-2 | README(§16 + §9.2 の採用分)、CONTRIBUTING、セキュリティポリシー | レビュー済み | P4-1 |
| P4-3 | CI(Windows ランナー: build、test、single-file publish) | 全テスト緑。実機専用テストは理由を出力してスキップ | P4-1 |
| P4-4 | MVP 完成条件(§19)の手動確認 | 手順どおり動く(G3 不成立時は §13.2 の判断に従う) | P4-1 |

P0-4 の作業項目・完了条件の補足: M-11(親ディレクトリの rename、junction 化、同名の別個体への差し替え)を実施し、保持ハンドルによる阻止、阻止できない場合の検出結果を G2 の判断材料として記録する。実施順は、環境変更を要しない基礎検証、特に V1〜V5 と S-17 の成立性確認を可能な範囲で先行する。方式自体が成立しない場合、不要な環境変更を行わない。環境変更が必要な検証には §14.4 の P0-4 の確認境界を適用する。

---

## 11. テスト計画

→ `PLAN_TESTS.md` §11 へ移動した(§11.1 SPEC §17 #1〜#54 対応表、§11.2 追加テスト X-01〜X-26、§11.2.1 追加テストの割当、§11.3 `ZipArchive` 差分テスト、§11.4 G3 不成立時に影響を受ける SPEC テスト)。タスクごとの完了条件となるテストは本書 §10.2 にもある。

---

## 12. 使用パッケージ一覧

版数は旧スパイク時の観測値であり、採用時(P1-1)に最新安定版を再選定する。

| パッケージ | 旧観測時の版 | 用途 | 採用理由 |
|---|---|---|---|
| Microsoft.Windows.CsWin32 | 0.3.335 | Win32 / COM の宣言生成(Windows プロジェクトのみ、`PrivateAssets=all`) | SPEC §18 の指定 |
| System.IO.Hashing | 10.0.12 | CRC-32 | ZIP ライブラリが CRC を検証しない(R-Z4)。Microsoft 製・MIT |
| (共有フレームワーク)`CodePagesEncodingProvider` | .NET 同梱 | CP437 | 追加パッケージ不要(R-Z2) |
| テストフレームワーク(xUnit v3 または MSTest)、Microsoft.NET.Test.Sdk | 未選定 | テスト | Phase 1 で選定 |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 未選定 | 再帰削除 API・パスベース削除 API の禁止。`Microsoft.VisualBasic.FileIO.FileSystem` の削除 API を含める(R-A15) | §2 のコード規約を機械的に担保 |

不採用: System.CommandLine(オプション4つに対し依存の価値が低い)、サードパーティ ZIP ライブラリ(§4.1.1 Z-D)、`Microsoft.VisualBasic` の `FileSystem.DeleteFile`(R-A15。理由: (1) ファイル単位の結果を得られない、(2) **UI 指定が `NoUI`、または非対話(`Environment.UserInteractive` が偽)のとき、ごみ箱を要求していても `IO.File.Delete`(完全削除)を呼ぶ経路がある**。(2) は SPEC §10.1 の「完全削除へフォールバックしない」と両立しない)。

---

## 13. 未解決事項

### 13.1 決まっていない・確認できていないこと

→ `PLAN_DECISIONS.md` §13.1 へ移動した(O-01〜O-16、O-15 の内訳と G0 への影響)。

### 13.2 SPEC変更が必要となる可能性がある未解決事項

以下は、調査結果によっては SPEC を満たせない可能性がある。本 PLAN は SPEC の緩和を決定しない。該当した場合は停止し、人間に判断を戻す。

| # | 事項 | 発生条件 | 影響する SPEC |
|---|---|---|---|
| V-01 | §6.2 の能力を満たす ZIP 読み取り方式がない | G1 未達(撤退条件) | §6、§6.2 |
| V-02 | §7.2 を満たす解決方式がない(H-1 不成立、H-2 不適合) | G2 未達 | §7.2、§10.1、§10.2 |
| V-03 | クラウド状態の一部を識別できず、安全に除外する手段もない | G2b 未達 | §7.3 |
| V-04 | ごみ箱の事前保証条件が確立できず、Recycle を MVP から除外 | G3 不成立(P0-7 で判明しうる) | §10.1、§10.2(既定モードではファイルが削除されないため「削除したファイルの祖先」が生じず、ディレクトリ削除も成立しない)、§19(「ごみ箱へ送られる」)、§16 の記述、§11.4 の (A)〜(D) のテスト(#5、#7、#11〜#14、#23〜#26、#33、#40、#44、#50、#53。#23 は実削除後の ZIP 不変の確認)。**`--delete-permanently` は SPEC §4 で定義され #27・#49 で試験されるため、G3 不成立を理由に PLAN 側で MVP から外さない。外す必要が生じた場合は SPEC 変更として人間が決める** |
| V-05 | ディレクトリ個体の同一性を保ったまま削除する手段がない(M-14 で保持と移動が両立しない等) | P0-4 | §10.2 |
| V-06 | 読み取り専用属性の付与の窓(S-12)が許容されない場合 | 人間の判断 | §10.1 |
| V-07 | ごみ箱送りの最終段(Shell のパス再解決)で §7.2 を満たす手段がない | P0-4 の調査で手段が見つからない場合(G2 は通過扱いにしない) | §7.2、§10.1、§16。「§10.1・§16 の残存リスク記述を §7.2 の例外と解釈してよいか」または「SPEC 変更が必要か」の人間の判断 |
| V-08 | クラウド placeholder の安全な識別に、候補ディレクトリ以外の列挙(または名前を指定した問い合わせ)が必要で、§11/§15 の列挙制約と両立しない | P0-4(C-a〜C-c)の結果、S-20 で人間が両立しないと判断した場合 | §7.3、§11、§15 |
| V-09 | dry-run の読み取りに伴うメタデータ更新を避けられず、§11「一切変更しない」と両立しない | M-18 と S-19 の判断の結果 | §11、#22 |

---

## 14. Phase 1 開始前に人間が決める必要のある事項(決定記録)

→ 状態の凡例、§14.1〜§14.3、§14.5、§14.6(H-01〜H-12 の決定記録、P0-3b の決定記録、S 項目の判断時期)は `PLAN_DECISIONS.md` §14 へ移動した。§14.4(H-01 の細目。検証の人間許可の境界)は安全条件のため本書に残す(`PLAN_VALIDATION.md` に写しがある)。

### 14.4 H-01 の細目

| 項目 | 決定 |
|---|---|
| 実施主体・環境 | Phase 0 の調査・スパイクは実施可能な作業環境で行う。Windows 固有の実機確認は人間側の Windows 検証環境で実施する。Linux 環境の結果を Windows 実機確認の代替として扱わない |
| 外部取得 | TASK 指定の5系統のみ(Microsoft Learn、.NET 公式ドキュメント、対象タグの `dotnet/runtime`、PKWARE APPNOTE、`microsoft/CsWin32`)。それ以外が必要になったら、安全性判断の根拠にせず人間へ戻す |
| 作業場所・容量 | `%TEMP%\unextract-spike`、合計 5 GiB 以下。超過が必要な場合は事前承認 |
| P0-4 の環境変更 | `%TEMP%\unextract-spike` 内で既存の環境設定を変更せず実施できるスパイクは、H-01 の既存許可範囲で実施可能。VHD/ボリューム設定、8.3 名生成設定、case-sensitive 設定、その他ホストまたは検証環境の状態変更が必要な場合は、変更の直前で停止する。「変更対象・変更内容・変更前状態・復元手順・復元確認方法・影響範囲」を人間へ提示し、明示的な許可を得てから実施する。許可がない状態では設定変更を行わない。これは SPEC の緩和ではない |
| P0-7 | 個別許可。システム変更は専用の検証環境・ボリュームを原則とし、変更内容、変更前状態、復元手順、復元確認方法を提示してから許可を得る |

Phase 3 の G3 判定時に決める事項(今は決めない): Recycle の MVP 除外の要否、§19 との関係、§11.4 のテストの扱い(V-04)。`--delete-permanently` は SPEC §4 で定義されているため PLAN 側では外さない。外す必要が生じた場合は SPEC 変更として人間が決める(V-04)。

---

## 15. 自己点検の記録

→ `PLAN_HISTORY.md` §15 へ移動した。
