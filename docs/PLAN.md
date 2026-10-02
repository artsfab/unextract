# unextract MVP 実装計画

仕様の唯一の基準は [`SPEC.md`](SPEC.md)。本書は実装順と完了判定を定める。旧 v4 の ZIP 独自構造パーサ、Recycle Bin、ディレクトリ削除、クラウド識別、専用ログ、比較ハンドルを削除まで保持する案は引き継がない。テスト項目は [`PLAN_TESTS.md`](PLAN_TESTS.md)、技術判断の根拠は [`PLAN_DECISIONS.md`](PLAN_DECISIONS.md)、実測記録は [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) にまとめる。

## 1. 実装の境界

- Windows 11、C#、.NET 10 (LTS)。SDK は `global.json` で 10.0.401 (`rollForward: latestPatch`) に固定した (`PLAN_DECISIONS.md` DEC-15)。ZIP 読み取りは `System.IO.Compression.ZipArchive`、CRC-32 計算は Microsoft 公式パッケージ `System.IO.Hashing`。
- ZIP の EOCD/CD/LH/DD を独自に解析しない。ZIP64/DD/SFX はライブラリの挙動に任せる。期待 CRC は公開プロパティ `ZipArchiveEntry.Crc32` (.NET Core 2.1 / .NET Standard 2.1 で追加、.NET 10 に含まれる) から得る。
- Windows 側は文書化 API (`CreateFileW`、`GetFileInformationByHandleEx`、`SetFileInformationByHandle`、`GetFinalPathNameByHandleW`、`DeviceIoControl(FSCTL_READ_FILE_USN_DATA)`) を `LibraryImport` で呼ぶ。ntdll の未文書 API と reflection は使わない。
- 判定ロジックと Windows のファイル個体・属性・削除操作を分離する。削除 API に未検証の ZIP パスを直接渡さない。
- 調査やテスト用の ZIP fixture 生成器 (壊れた ZIP を作るためのバイト書き換えを含む) は許すが、テスト専用とし製品側の ZIP パーサを兼ねさせない。
- 単一の事前検証結果をもとに、**削除許可ゲート**を1回だけ開く。1件でも FATAL があればゲートを開かない。`--dry-run` はゲートの直前まで (初回分類と結果表示) 通常実行と同じコードを通り、その後の削除直前再検証には入らない。
- 初回比較と削除直前の2回目の比較は、同じ「エントリ内容の検証基準」(SPEC §5.2) の実装を共有する。違いは違反時の扱い (初回は FATAL/MODIFIED、2回目は停止) だけにする。Fast はどちらの比較も行わない (§4 の「Fast モード」)。
- 実行モードは Strict (既定) と Fast (`--fast`、SPEC §15) で、1回の実行全体で固定する。Fast は既存の `Unextract.Core`・`Unextract.Windows`・`Unextract.Cli` の構成の中のモード分岐として実装し、Fast 用の層、専用の実装クラス、抽象化、追加の状態管理は作らない (`PLAN_DECISIONS.md` DEC-19、DEC-23)。削除候補は Strict では `MATCHED`、Fast では `SAME_SIZE` とし (SPEC §6)、削除許可ゲート、確認、削除フェーズは両モードとも削除候補に対して働く。

## 2. 削除有効化前の確認ゲート

根拠、採用した挙動、試した fixture、未確認範囲を [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) に記録する。ゲートが通るまで実削除を有効化しない。

| ID | 確認内容 | 状態と通過条件 |
|---|---|---|
| G1 | .NET 10、ZipArchive の名前復号と ZIP64/DD/SFX/Stored/Deflate の挙動 | **通過**。調査 (V1、V3) と Z 系テストの合格。SPEC §4.1・§5.4 と一致 |
| G2 | 内容比較候補の CRC-32、宣言サイズと実測量、破損、暗号化、未対応方式。`Crc32` の導入版 | **通過**。調査 (V2)。ランタイムは CRC・不足・暗号化を検出しないため unextract が検出する (SPEC §5.2)。C 系テストの合格 |
| G3 | 属性の許可集合、ZIP 特殊種別、ADS、hardlink、reparse、case strict、File ID、親 File ID、ハンドル設計 | **通過 (未確認の事項を除く)**。非破壊の実測 (V4〜V7)、SPEC §13 の PoC 1〜8、D 系テストの実 NTFS での合格、E-2 の実測 (D22 を含む)。§13 の未確認の事項は成立と見なさない。§4 の対応表に反映済み |
| G4 | resource limits | **通過**。値と適用範囲は確定 (SPEC §11)。R 系の境界テストの合格。benchmark は不要 |

Fast (SPEC §15) は、新しい Windows API、ハンドル構成、ZIP の読み取り方法を加えず、Strict の処理の一部 (§4 の「Fast モード」の2点) を実行しないだけなので、新しいゲートは設けない。G2 の内容検証は Strict の初回比較と2回目の比較に関するもので、Fast では該当する処理を行わない (SPEC §15.2)。G1、G3、G4 は両モードに共通する。

ゲート不成立なら、推測で「正常」と扱わない。ブロッカーが残る間は削除機能を有効化しない。SPEC §13 の未確認の事項は成立と見なさず、SPEC §14 に従う。

## 3. 実装順

| 段階 | 作業 | 完了条件 |
|---|---|---|
| A. 基盤 | ソリューション、`global.json`、CLI 引数 (`--fast` を含む。§4 の「Fast モード」)、結果型 (`SAME_SIZE` を含む)、README とライセンスの骨格 | ビルド・基本 CLI テストが通る |
| B. ZIP 事前検証 | ZIP ハンドル保持、CP437 指定の ZipArchive アダプタ、U+FFFD 拒否、全エントリのパス・構造・特殊種別検証、resource limits (宣言値) | 危険な構造と上限超過で削除許可ゲートが閉じる。target に触れない |
| C. target 分類・内容検証 | target ルート検査と保持、親成分の分類表 (SPEC §6.1)、比較用ハンドル、属性許可リスト・ADS・hardlink・reparse・ZIP 自身の判定、サイズ比較、SPEC §6.1 の手順7 のモード分岐 (Strict はエントリ内容の検証基準による初回比較、Fast は内容を読まず `SAME_SIZE`)、スナップショット (親 File ID を含む) 記録とハンドルの解放 | MISSING とサイズ不一致の内容を読まず、共有違反で全体 FATAL。Strict は内容比較候補の異常で全体 FATAL、Fast は内容比較候補の ZIP と target の内容を読まない。判定不能は FATAL |
| D. dry-run と表示 | 正常完走時の全カテゴリー全パス (削除候補のカテゴリーはモードに応じて `MATCHED` または `SAME_SIZE`)、FATAL 時の判定済みパス・原因・未判定件数、簡易 progress、Fast の結果ヘッダーの警告 | dry-run で変更せず、同じモードの通常実行と同じ初回分類。空 ZIP/target/候補0件はプロンプトなしで正常終了 |
| E-1. 削除 PoC | 実施済み (SPEC §13、`PLAN_VALIDATION.md` の「削除 PoC」)。§4 のエラー対応表を確定した | 完了。ブロッカーなし。未確認2点は SPEC §14 に従う |
| E-2. 削除 | `[y/N]` と `--yes` (Fast は `[y/N]` の直前に警告)、削除用ハンドルの再オープン、同一性再検証、SPEC §8.3 の手順3 のモード分岐 (Strict は2回目の全バイト比較、Fast は実行しない)、最終確認 (両モード)、ハンドルベース削除、成立確認、`DELETE_FAILED` と停止の分岐、識別確認 | 削除候補 (Strict は `MATCHED`、Fast は `SAME_SIZE`) のみ削除。事前 FATAL で0件。競合時も、削除対象の同一性・安全性の再検証によって、別のファイルや安全条件を満たさないファイルを削除しない (両モード。Strict は内容の変更も2回目の全バイト比較で検出して停止する)。未知のエラーで停止 |
| F. 仕上げ | Windows CI、実機確認、README の対応範囲・制限、配布ビルド | [`PLAN_TESTS.md`](PLAN_TESTS.md) の必須項目が通り、G1〜G4 と SPEC §13 の記録が揃う |

段階 B〜D は削除操作を無効にして進める。E-1 は完了し、ブロッカーは無かった。Fast (SPEC §15) は新しい段階を設けず、A、C、D、E-2、F の各段階にモード分岐として加える。

## 4. 主要な実装上の約束

### ZIP と全件ゲート

ZIP は `FileShare.Read` で開いて終了まで保持し、実行中の書き換えを防ぐ。全エントリの名前・パス・重複・case 衝突・file-dir 構造・特殊種別と resource limits (宣言値) を target に触れる前に検査する。ZIP 内部の file-parent 衝突は全体 FATAL、target 側の親成分は SPEC §6.1 の表で分類する。

ZIP の内容を読むのは、target に安全な通常ファイルがありサイズが `Length` と一致する内容比較候補だけ (Strict。Fast は内容比較候補でも読まない)。候補では `IsEncrypted` を確認してから `Open()` し、ストリームを必ず終端まで読む。読み取り中に `Length` 超過で即中断、終端でバイト数が `Length` と一致、CRC-32 が `Crc32` と一致することを確認し、例外は全て異常とする。バイト不一致でも読み切りを省略しない。

事前検証の結果は「削除許可」「全体中止」のどちらか。後方エントリでエラーになっても、先頭の削除候補は削除しない。

### Windows の対象解決

target ルートは、先に確認用ハンドル (`FILE_READ_ATTRIBUTES` のみ、`OPEN_REPARSE_POINT` 付き) で最終成分が reparse でないことと File ID を確認してから、`FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES` (DELETE を共有しない) で開いて保持し、File ID の一致を確かめて最終パス (`\\?\` 形式のまま) と File ID を記録する (SPEC §3 の手順1、§8.1)。実名は SPEC §6.2 のとおり、検証済みのディレクトリハンドルから `FileIdExtdDirectoryInfo` で項目を列挙し、ZIP の成分名と序数比較して確認する (親成分・最終成分とも同じ手段。ディレクトリごとに1回列挙し、照合しなかった名前は保持しない。列挙の失敗は FATAL。1回の列挙で同じ名前が複数回返ったら最初の1件だけを採用し、見落とされた名前は `MISSING`)。途中の成分を SPEC §6.1 の表で分類する。最終成分は比較用ハンドルで開き、File ID が列挙で見つけた項目と一致することと、最終パスの序数一致を確認してから、同じハンドルで属性許可リスト・reparse・リンク数・ADS・ZIP 自身を判定する。API 失敗、存在確認後のオープン失敗 (共有違反を含む) は FATAL。削除候補のスナップショットを記録したらハンドルを閉じる。比較用ハンドルを結果表示や確認待ちの間に保持しない。

### 削除

各削除候補について、削除用ハンドル (`GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE`、`FILE_SHARE_READ`、`OPEN_REPARSE_POINT | OPEN_NO_RECALL`) を期待パスで1回だけ開き、同じハンドルで SPEC §8.3 の 2〜6 (同一性再検証 → 2回目の全バイト比較 → 最終確認 → 削除 → 成立確認) を行う。Fast は2回目の全バイト比較 (手順3) だけを行わず、他は同じに行う (下の「Fast モード」)。同一性再検証では親 File ID と最終パスの両方を比較し、どちらも省略しない (`PLAN_DECISIONS.md` DEC-13)。削除は `SetFileInformationByHandle(FileDispositionInfoEx)` に flags `FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS` (0x3) を渡し、`IGNORE_READONLY_ATTRIBUTE` は使わない。成立は同じハンドルの `DeletePending == true` で判定し、API の成功だけでは判定しない (DEC-11)。パスベースの削除は使わない。

### Fast モード

Fast (SPEC §15) は既存の構成の中のモード分岐として実装する (`PLAN_DECISIONS.md` DEC-19、DEC-23)。置き場所は次のとおり。

| 項目 | 置き場所 |
|---|---|
| `--fast` の解析 | 既存の引数解析 (`Unextract.Core` の `CommandLineParser`) に加える。他のオプションと同じく、重複は入力エラー。`--dry-run`、`--yes`/`-y` と併用でき、順序を問わない。使い方の表示にも `[--fast]` を加える (SPEC §2)。`Unextract.Cli` は解析結果のモードを実行要求に渡すだけにする |
| モード (Strict / Fast) と分類 `SAME_SIZE` | `Unextract.Core`。モードは実行全体で1つの値とし、初回分類と削除フェーズに同じ値を渡す |
| SPEC §6.1 の手順7 の分岐 | `Unextract.Core` (初回分類) |
| SPEC §8.3 の手順3 の分岐 | `Unextract.Core` (削除フェーズ) |
| Win32 の呼び出し | `Unextract.Windows` のまま。Fast 用の API やハンドル構成を加えない。比較用・削除用ハンドルの構成は両モードで同じ (削除用ハンドルの `GENERIC_READ` は Fast では読み取りに使わないが、構成を変えない。DEC-22) |

**検証・削除フロー上の Strict と Fast の明示的な処理差は、次の2点だけとする** (DEC-19、DEC-22)。表示、分類名、`--fast` の解析はこれに含めない。

| 箇所 | Strict | Fast |
|---|---|---|
| SPEC §6.1 の手順7 (サイズ一致の安全な通常ファイル) | SPEC §5.2 の内容検証・全バイト比較を行い、`MATCHED` / `MODIFIED` / FATAL | ZIP エントリと target のどちらの内容も読まず `SAME_SIZE` |
| SPEC §8.3 の手順3 (2回目の全バイト比較) | 行う | 行わない。手順1、2、4、5、6 は Strict と同じに行い、手順4 (最終確認) も行う |

この2点を実行しない結果として、Fast では次が発生しない。いずれも該当する処理が呼ばれないことの帰結として扱い、**個別の Fast 分岐は加えない** (SPEC §15.2)。

- ZIP エントリの内容 (entry body) の読み取り。`IsEncrypted` の確認、`Open()`、CRC-32 の計算を含む。
- SPEC §5.2 の 1〜5 の違反による FATAL と、比較中の target の読み取り失敗による FATAL (SPEC §9)。
- 1エントリの実測展開量と実測展開量の合計の計上と、その上限による FATAL (SPEC §11)。
- SPEC §8.4 の段階3 (再比較) による停止。

次は分岐せず両モードで同じに行う: SPEC §6.1 の手順1〜6、§6.2、§7、比較用ハンドルと共有違反の FATAL、宣言側の resource limits (SPEC §11)、再検証用スナップショット (§8.2)、削除用ハンドル、削除方式 (flags 0x3)、`DeletePending` による成立確認、`DELETE_FAILED` と停止の境界 (下の対応表を含む)、終了コード。`--dry-run` と通常実行の初回分類の一致は同じモード同士で成り立たせる (SPEC §2、§15.1)。

表示 (処理差に含めない): 解析結果のカテゴリーは、Strict では `MATCHED`、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DIRECTORY`、Fast では `SAME_SIZE`、`MODIFIED`、`MISSING`、`SKIPPED_SPECIAL_FILE`、`DIRECTORY` とする。削除候補のカテゴリーだけが入れ替わり、もう一方は件数0としても一覧と合計行に出さない (SPEC §10、DEC-21)。

**Fast の警告** (SPEC §10、§15.6、DEC-20)。文言は次の1行とし、結果ヘッダーと `[y/N]` の直前で同じものを使う。

```text
警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。
```

- 言語・文体は、既存の CLI のユーザー向け出力 (日本語。「入力エラー: 」「FATAL: 」「停止: 」の接頭辞、「確認できません。」「削除しません。」の文末) に合わせた。
- 結果ヘッダー: 解析結果の一覧 (SPEC §3 の手順5) の先頭行として標準出力に書く。結果表示に至る Fast の実行では、通常実行、`--dry-run`、削除候補0件、削除開始前の FATAL (ZIP の事前検証の FATAL、初回分類中の FATAL。判定済み・未判定の件数を一覧に出す場合)、`--yes` のいずれでも書く。結果表示に至らない終了 (引数・target の入力エラー、ZIP を開けない場合、内部エラー) では書かない。
- `[y/N]` の直前: 既存の確認プロンプトと同じ経路 (`IConfirmationPrompt.Ask` に渡す文字列の、既存のプロンプトの前の行) で出す。製品の CLI では標準出力になる。確認プロンプトを出さない場合 (`--yes`、非対話による中止、削除候補0件、`--dry-run`、FATAL) は出さない。
- Strict ではどちらも出さない。

### エラーコードと `DELETE_FAILED` / 停止の対応

SPEC §8.4 の境界を、Win32 エラーコードに次のように対応付ける。SPEC §13 の PoC 7 の実測 (Windows 11 10.0.26300、NTFS、他プロセスの操作は別プロセス) で確定した。削除用オープンで観測したコードは 32、5、2、3 の4種類である。**表に無いコードは全て停止とする。**

| 段階 | Win32 エラー | 扱い | 根拠 |
|---|---|---|---|
| オープン | `ERROR_SHARING_VIOLATION` 32 | 識別確認が「一致に見える」なら `DELETE_FAILED`、不一致・失敗なら停止 | PoC 7 #1・#2。他プロセスが書き込み中、または `FILE_SHARE_DELETE` なしで読み取り中。識別確認はどちらも「一致に見える」 |
| オープン | `ERROR_ACCESS_DENIED` 5 | 識別確認が「一致に見える」なら `DELETE_FAILED`、不一致・失敗なら停止 | PoC 7 #3・#5・#6・#7・#11。ACL 拒否、対象のディレクトリ化・ディレクトリ junction 化、削除保留中が**同じ 5** になり、コードだけでは区別できない。識別確認が必須 |
| オープン | `ERROR_FILE_NOT_FOUND` 2、`ERROR_PATH_NOT_FOUND` 3 | 停止 | PoC 7 #8・#9・#10・#14。対象消失で 2、親の消失・親のファイル化・リンク先のない junction で 3。識別確認も同じコードで失敗する |
| オープン | reparse・名前解決関連 (`ERROR_CANT_ACCESS_FILE` 1920、`ERROR_CANT_RESOLVE_FILENAME` 1921、`ERROR_NOT_A_REPARSE_POINT` 4390 など)、クラウド関連 (`ERROR_CLOUD_FILE_*`) | 停止 | PoC では観測していない。表に無いコードと同じく停止とすることを明示する |
| オープン | その他全て | 停止 | 未知・曖昧 |
| オープン成功 | — | 段階2の再検証へ | PoC 7 #4・#13・#15・#16。差し替えは段階2で検出する |
| 再検証・再比較・最終確認 | 全ての失敗・不一致 | 停止 | SPEC §8.4 |
| 削除 (`SetFileInformationByHandle`) | 全ての失敗 (read-only による `ERROR_ACCESS_DENIED` 5 を含む) | 停止 | PoC 1〜3・6 で read-only の付与は指示の失敗 (5) になった。原因を識別できないため停止 (`PLAN_DECISIONS.md` DEC-12) |
| 成立確認 | `DeletePending` が true でない、または取得できない | 停止 (「削除された可能性あり」と報告) | SPEC §8.3 の 6、§8.4。API の成功だけでは成立としない |

PoC 7 の各状況での実測 (識別確認は、SPEC では 32 と 5 のときだけ行うが、PoC では全状況で実行して判定を記録した):

| # | 状況 | 削除用オープン | 識別確認の判定 | §8.4 の分類 |
|---|---|---|---|---|
| 1 | 他プロセスが `GENERIC_WRITE` (共有 R\|W\|D) で開いている | 32 | 一致に見える | `DELETE_FAILED` |
| 2 | 他プロセスが `GENERIC_READ` (共有 R のみ) で開いている | 32 | 一致に見える | `DELETE_FAILED` |
| 3 | ACL: 対象の READ_DATA を拒否 | 5 | 一致に見える | `DELETE_FAILED` |
| 4 | ACL: 対象の DELETE を拒否 (親の DELETE_CHILD は許可) | **成功** | 一致に見える (PoC で比較した項目では段階2の比較も差なし) | 段階2へ (下記。E-2 の実測では、初回分類の前の拒否は削除、確認待ち中の拒否は段階2で停止) |
| 5 | ACL: 対象の DELETE と親の DELETE_CHILD を拒否 | 5 | 一致に見える | `DELETE_FAILED` |
| 6 | 対象がディレクトリに差し替えられた | 5 | 不一致 (File ID、`Directory`) | 停止 |
| 7 | 削除保留中 (他プロセスが削除を指示してハンドルを保持) | 5 | 失敗 (識別確認のオープンも 5) | 停止 |
| 8 | 対象消失 | 2 | 失敗 (2) | 停止 |
| 9 | 親ディレクトリ消失 | 3 | 失敗 (3) | 停止 |
| 10 | 親がファイルに差し替えられた | 3 | 失敗 (3) | 停止 |
| 11 | 対象がディレクトリ junction に差し替えられた | 5 | 不一致 (File ID、`Directory`、reparse) | 停止 |
| 12 | 対象がファイル symlink に差し替えられた | **未確認** (作成に必要な特権がなく再現できなかった) | — | 表に従う (オープンが成功すれば段階2の reparse・File ID 比較、失敗すれば上表のコードの行) |
| 13 | 途中のディレクトリが junction に差し替えられた (同じファイルに到達) | 成功 | 不一致 (最終パス) | 段階2で最終パス不一致 → 停止 |
| 14 | 途中のディレクトリが、リンク先の存在しない junction に差し替えられた | 3 | 失敗 (3) | 停止 |
| 15 | 同名の別ファイルに差し替えられた | 成功 | 不一致 (File ID) | 段階2で File ID 不一致 → 停止 |
| 16 | 親ディレクトリを他プロセスが `FILE_LIST_DIRECTORY` (共有 R) で保持 (対象は未使用) | 成功 | 一致に見える (段階2も差なし) | 段階2へ (妨げにならない) |
| 17 | クラウド placeholder | **未確認** (再現環境なし) | — | 表に従う (reparse・クラウド関連のエラーは停止。オープンが成功すれば段階2の reparse 比較) |

**#4 (対象の DELETE だけを ACL で拒否) の扱い**: 親が DELETE_CHILD を許可していると、削除用オープンは成功し、段階1では検出できない。ACL 自体はスナップショットの比較項目に含まれない。E-2 の実測 (`PLAN_VALIDATION.md` の「E-2 実測」、`PLAN_TESTS.md` D22) で、拒否した時点によって次の2通りになることを確認した。

- 初回分類の前に拒否した場合: 段階1・2・5がすべて成功し (同じハンドルの `DeletePending` = true)、対象は削除される。利用者が対象の DELETE を拒否していても、親の許可によって削除される。
- 確認待ち中に拒否した場合: ACL の変更で対象の `ChangeTime` が変わるため、段階2の再検証で不一致になり停止する。対象は残る。

上表の #4 の「段階2の比較も差なし」は、PoC で比較した項目 (File ID、親 File ID、最終パス、`Directory`、`DeletePending`) に限った結果であり、`ChangeTime` を含む SPEC §8.3 の 2 の全項目ではない。扱いは、Windows の通常の ACL の挙動として受け入れることに確定した (案 A。全バイト一致を2回確認したファイルで、利用者の親ディレクトリに対する権限の範囲内であるため。ファイルの ACL は比較対象にしない。`PLAN_DECISIONS.md` DEC-12)。この理由は Strict に適用される。Fast では、同じ扱いを SPEC §15.4 と DEC-22 に従って維持する (削除用ハンドル、削除方式、`DELETE_FAILED` と停止の境界は両モードで同じ)。この ACL の扱い自体を理由として、別のファイルを削除対象とするものではない。段階5が失敗した場合 (実測では起きていない) は段階5の行により停止する。

識別確認は、オープン失敗時にだけ `FILE_READ_ATTRIBUTES` のみのハンドルで行う (SPEC §8.4)。「一致」は識別確認の時点でスナップショットと一致する通常ファイルに見えるという意味で、拒否された個体との同一性や拒否の理由は保証しない。そのため一致しても `DELETE_FAILED` (削除しない) より強い扱いにはしない。識別確認自体が失敗した場合も停止とする。

テストのため、解析完了直後・確認待ち・再オープン直前・再検証直後・再比較中・最終確認直前・削除指示直前に処理を差し込めるフックと、エラーコードを注入できる偽ファイルシステムを内部に設ける。製品 CLI の公開機能にはしない。Fast では再比較を行わないため、再比較中のフックは呼ばれない。

## 5. 完了判定と成果物

- [`PLAN_TESTS.md`](PLAN_TESTS.md) の Core、Win、手動の必須テストを実行し、失敗または未実施の項目を区別する。手動の項目は [`MANUAL_TESTS.md`](MANUAL_TESTS.md) (M 系) の手順で行う。
- E2E (`PLAN_TESTS.md` §10 の X 系) を、ビルド済みの exe と、`UNEXTRACT_E2E_EXE` に指定した publish 版の単一ファイル exe の両方で実行し、全件が合格する。
- [`PLAN_VALIDATION.md`](PLAN_VALIDATION.md) に G1〜G4 と SPEC §13 の PoC の結果と実施環境を記録し、必須テスト対応表の全項目にテストがあることを確認する。.NET のパッチ更新で ZipArchive の挙動が変わっても SPEC §5.2 の結果が変わらないことを Z・C 系テストで継続的に確認する。
- README に、完全削除で復旧手段がないこと、内容比較候補の異常や共有違反での全体中止、64 GiB 上限、MISSING とサイズ不一致の内容を読まないこと、削除直前再検証とその停止、dry-run との差、I/O が約2倍になること (Strict)、独自 ZIP 構造パーサを作らない理由、CRC の位置づけ、SPEC §12 の限界を書く。Fast については、`--fast` (`--dry-run`・`--yes` との併用)、分類 `SAME_SIZE`、Fast の契約 (Strict と共通の安全なファイル解決のうえで、パスとサイズだけで判定すること。SPEC §15.5)、保証しない事項 (内容の一致、正常に展開できること、暗号化・破損・未対応方式でないこと、宣言内容と実データの整合、初回判定から削除までの内容の不変。同一パス・同一サイズで内容の異なるファイルが削除され得ること)、警告の表示箇所 (本書 §4 の「Fast モード」)、Strict との性能差 (Fast は ZIP と target の内容を読まず、削除直前の再比較もしない) を書く。
- `src/`、`tests/`、`docs/`、`scripts/`、`.github/` に双方向制御文字 (U+202A〜U+202E、U+2066〜U+2069、U+200E、U+200F、U+061C) と不可視文字 (U+200B〜U+200D、U+2060〜U+2064、U+FEFF、U+00AD) が実際の文字として残っていないことを grep で確認する (結果が空であること)。テストやコードで必要な場合は `\u202E` のようなエスケープ表記で書く。
- 履歴は [`PLAN_HISTORY.md`](PLAN_HISTORY.md) に記す。仕様変更が必要なら SPEC を先に変更する。
