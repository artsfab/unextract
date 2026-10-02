# unextract MVP 技術検証の記録

仕様の正は [`SPEC.md`](SPEC.md)。本書は技術調査と実測の結果を記録する。旧 v4 の調査は通過記録として扱わない。ここでの実測は製品コードではなく使い捨ての検証プログラムによるもので、実装後は [`PLAN_TESTS.md`](PLAN_TESTS.md) の自動テストで再確認する。

ユーザー名・SID・セッション ID は伏せている (`<user>` などで表記)。

## 実施環境 (2026-10-02)

- Windows 11 Home 10.0.26300、NTFS (システムドライブ上の一時ディレクトリ)
- .NET SDK 10.0.401、Microsoft.NETCore.App 10.0.12
- fixture は Python 3.13 の `zipfile` で作成し、ヘッダーのフィールドをバイト単位で書き換えた。DD 付き ZIP は .NET の `ZipArchive` で非シーク可能なストリームへ書いて作成した
- 検証時点ではリポジトリに `global.json` が無く、SDK は固定されていなかった。上記はインストール済みの SDK・ランタイムでの結果である。その後、同じ SDK 10.0.401 に `global.json` で固定した (`PLAN_DECISIONS.md` DEC-15)
- V1〜V7 では実ファイルの削除を伴う検証は行っていない。V4〜V6 の「他プロセス」相当の操作は、同一プロセス内の別ハンドルで行った。削除と別プロセスを伴う検証は後述の「削除 PoC」(SPEC §13) で行った

## ゲート状態

段階 F の時点 (2026-10-02) の状態。テスト結果はローカル (「実施環境」と同じ環境、Release ビルド) の `dotnet test` で、Core 504 件・Windows 92 件・Cli 22 件が全て合格した (2回連続。「段階 F」の節)。CI では初回の実行 (コミット `78c7609`) が成功し、Core 504 件・Windows 92 件・Cli 22 件・E2E 22 件の合格をログで確認した (「CI の初回実行」の節)。ランナーの管理者権限の有無は未確認である。

| ゲート | 状態 | 根拠 | 残り |
|---|---|---|---|
| G1 | 通過 | 調査 (V1、V3) と Z 系テスト (Z01〜Z09) の合格 | なし。.NET のパッチ更新後も Z 系テストで継続確認する |
| G2 | 通過 | 調査 (V2、V2a) と C 系テスト (C01〜C14) の合格 | なし。C13 はランタイムの挙動が V2 と変わったら記録を更新する |
| G3 | 通過 (未確認の事項を除く) | 非破壊の実測 (V4〜V7)、SPEC §13 の PoC 1〜8、実 NTFS の T 系・D 系テストの合格、「E-2 実測」(D22 を含む)。PoC 7 の結果は `PLAN.md` §4 の対応表に反映済み | 未確認: PoC 7 のファイル symlink への差し替え、クラウド placeholder、EFS で暗号化された対象の削除、USN 機能を持たないファイルシステム。SPEC §14 のとおり成立と見なさず、判定できなければ削除しない側に倒す。D22 は案 A で確定し実測済み (初回分類の前の拒否は削除、確認待ち中の拒否は段階2で停止。`PLAN_DECISIONS.md` DEC-12) |
| G4 | 通過 | 値と適用範囲は SPEC §11 で確定。R 系テスト (R01〜R08) の合格 | なし (benchmark は不要) |

Fast モード (SPEC §15) には新しいゲートを設けない (`PLAN.md` §2)。上の状態とテストの件数は、Fast を追加する前の (Strict だけの) 実装とテストによるものである。その後、Fast 本体とテスト、M08 / O07 の PTY 自動化、publish 版 E2E wrapper の実装・検証が完了した。最新実績は「Fast / PTY / publish E2E の最終確認」(2026-10-03 記録) に記す。

## V1. 名前の復号

| fixture | `entryNameEncoding` 未指定 | CP437 を指定 |
|---|---|---|
| フラグなし、CP437 バイト (`café░`) | U+FFFD に置換 (UTF-8 として復号) | `café░` |
| フラグ付き、正しい UTF-8 | 正しく復号 | 正しく復号 (フラグ付きは UTF-8) |
| フラグ付き、不正な UTF-8 (`FF FE`) | 例外なし、U+FFFD ×2 | 同左 |
| フラグなし、UTF-8 バイト | UTF-8 として復号 | CP437 として復号 (別名になる) |

結論: CP437 を `entryNameEncoding` に必ず渡す。不正 UTF-8 は例外にならないため、復号名の U+FFFD を FATAL とする (SPEC §4.1)。

## V2. 内容の検証とランタイムの保証範囲

`ZipArchiveEntry.Open()` で終端まで読み、バイト数と CRC-32 を unextract 側で計算して比較した。

| fixture | ランタイムの挙動 | unextract の検出 |
|---|---|---|
| CRC-32 を CD と LH の両方、または CD だけ書き換え | 例外なし。7000 バイト全て返る | CRC 不一致 |
| Deflate データの途中を破損 | `InvalidDataException` (メッセージは「unsupported compression method」と不正確) | 例外 |
| 圧縮サイズを半分にし Deflate を途中で切る | 例外なし。13 バイトで終わる | バイト数不足 |
| 最終ブロックのない Deflate (sync flush で終わる) | 例外なし。全データを返す | 内容は正しく、CRC も一致 (問題なし) |
| Deflate 後に余分なバイト | 無視して正常 | 問題なし |
| 宣言 `Length` を 10 減らす (Deflate) | `Length` で出力が切られる。例外なし | CRC 不一致 |
| 宣言 `Length` を 10 増やす (Deflate) | 実データ分だけ返る。例外なし | バイト数不足 |
| 宣言 `Length` を 10 減らす / 増やす (Stored) | 宣言値でなく圧縮サイズ分を返す。例外なし | `Length` 超過 / 不足 |
| 暗号化フラグ (bit 0) + Deflate | `IsEncrypted` = true だが `Open()` は成功し平文として読める | `IsEncrypted` を事前確認 |
| 圧縮方式 LZMA (14)、BZip2 (12)、AES (99) | `Open()` で `InvalidDataException` | 例外 |
| 2回目の `Read` (終端後) | 0 を返す | — |

公開 API: `ZipArchiveEntry.Crc32` (uint、Central Directory の CRC-32 フィールド。.NET Core 2.1 以降)、`IsEncrypted`、`Length`、`CompressedLength`、`ExternalAttributes` は公開されている。圧縮方式・作成元 OS・生の名前バイトは公開されていない。`System.IO.Compression.dll` (10.0.12) の型一覧に読み取り時の CRC 検証ストリームは存在しない (`Crc32Helper` は内部型で書き込み用)。

結論: ランタイムは CRC 不一致、Deflate の途中切れ、宣言サイズとの不一致、暗号化を検出しない。期待 CRC は公開 API で得られるため、独自パーサや reflection なしで unextract が CRC-32 とバイト数を検証できる。CRC 検証は MVP の必須要件として維持する (SPEC §5.3)。CRC は ZIP 作成者が書き換えられるため、偶発的な破損・切り詰め・記録と実データの食い違いの検出に限って意味を持ち、敵対的な ZIP への防御ではない。

## V2a. `ZipArchiveEntry.Crc32` の導入版

| 確認方法 | 結果 |
|---|---|
| learn.microsoft.com の API ページ (対象版の一覧) | netcore-2.1、netcore-2.2、netcore-3.0、netcore-3.1、net-5.0〜net-11.0、netstandard-2.1。.NET Framework と netstandard-2.0 は含まれない。描画された "Applies to" 表は空だったため、ページのメタデータの対象版一覧で確認した |
| dotnet/dotnet-api-docs の `ZipArchiveEntry.xml` | `System.IO.Compression` 8.0.0.0〜11.0.0.0 と `netstandard` 2.1.0.0 を列挙。古い版は列挙から外されているだけで、導入版を示すものではない |
| NuGet の参照アセンブリのメタデータに `get_Crc32` があるか | `Microsoft.NETCore.App` 2.0.0 (`ref/netcoreapp2.0`): 無い。2.1.0 (`ref/netcoreapp2.1`): ある。`NETStandard.Library` 2.0.3 の `netstandard.dll`: 無い。`NETStandard.Library.Ref` 2.1.0: ある |
| .NET 10.0.12 ランタイム (SDK 10.0.401) | reflection で public の `UInt32 Crc32 { get; }` を確認。検証プログラムは `entry.Crc32` を reflection なしで直接参照してコンパイル・実行でき、Central Directory の値を返した |

結論: 公開 API としての導入は .NET Core 2.1 と .NET Standard 2.1。採用する .NET 10 (LTS) に含まれる。「.NET Core 2.1 以降」という従来の記述は正しかったが、.NET Standard 2.1 にもあり .NET Framework には無いことを明記した。

## V3. 形式の受理範囲

| fixture | 結果 |
|---|---|
| ZIP64 extra 付きエントリ (強制) | 読める、CRC・長さ一致 |
| 65,540 エントリ (ZIP64 EOCD) | 読める |
| Data Descriptor (bit 3) 付き | 読める。`Crc32`・`Length` は CD の正しい値 |
| SFX (4 KiB の前置データ、オフセット調整済み) | 読める |
| 前置データのみ (オフセット未調整) | `InvalidDataException` (EOCD とエントリ数の不一致) |
| 末尾ごみ付き | 読める |

## V4. ハンドルからの情報取得 (非破壊)

`CreateFileW(DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL)` で開いたハンドル1つから、次が全て成功した。

- `FileIdInfo`: ボリュームシリアルと 128 ビット File ID。大小文字違いの名前で開いても同じ ID
- `FileStandardInfo`: `EndOfFile`、`NumberOfLinks` (hardlink で 2)、`DeletePending`、`Directory`
- `FileBasicInfo`: 属性、`LastWriteTime`、`ChangeTime`
- `FileAttributeTagInfo`: reparse tag
- `FileStreamInfo`: `FILE_READ_DATA` なしでも列挙できる。ADS 付きで `::$DATA,:Zone.Identifier:$DATA`
- `GetFinalPathNameByHandleW(0)`: `plain.TXT` で開いても実名の `Plain.txt` を返す (case strict に使える)

このハンドルを保持している間、別ハンドルでの `GENERIC_WRITE` オープンと `MoveFileW` による改名は `ERROR_SHARING_VIOLATION` (32) で失敗した。

## V5. target ルートの保持 (非破壊)

ディレクトリを `FILE_SHARE_READ | FILE_SHARE_WRITE`、`FILE_FLAG_BACKUP_SEMANTICS` で開いて保持した。

- アクセスが `FILE_READ_ATTRIBUTES` だけでは、保持中にそのディレクトリを改名できた (共有モードの判定に参加しない)
- `FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES` では改名が 32 で失敗した
- 配下のサブディレクトリの改名は、ルートを保持していても成功する。SPEC では最終パスの比較で検出する

## 参照した公式資料

- `ZipArchiveEntry.Crc32` (learn.microsoft.com、System.IO.Compression) — 公開プロパティ。ページの対象版は netcore-2.1〜net-11.0 と netstandard-2.1 で、.NET Framework は含まれない。詳細は V2a
- `FILE_DISPOSITION_INFORMATION_EX` (Windows Driver Kit) — `FILE_DISPOSITION_DELETE` 0x1、`POSIX_SEMANTICS` 0x2、`IGNORE_READONLY_ATTRIBUTE` 0x10。DELETE アクセスが必要。POSIX semantics では削除したハンドルを閉じた時点で名前が名前空間から消え、既存ハンドルはデータにアクセスし続けられる。`STATUS_CANNOT_DELETE` は read-only または mapped view
- `FILE_DISPOSITION_INFO` (Win32) — `SetFileInformationByHandle` で使う。`FILE_FLAG_DELETE_ON_CLOSE` で開いたハンドルには効かない。`FileDispositionInfoEx` の最小対応 Windows 版は今回参照した資料では確認していない (SPEC §13 の PoC 1・2 で Windows 11 での動作を確認する)
- `NtQueryInformationFile` (WDK) — `FileBasicInformation`・`FileAttributeTagInformation` は `FILE_READ_ATTRIBUTES`、`FileStandardInformation`・`FileNameInformation` は特定のアクセス不要

## V6. 削除用ハンドルの構成での予備実測 (非破壊)

SPEC §8.1 の削除用ハンドル構成 (`GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE`、`FILE_SHARE_READ`、`OPEN_REPARSE_POINT | OPEN_NO_RECALL`) で、削除せずに次を確認した。他プロセスの操作は同一プロセス内の別ハンドルで代用した (共有モードの判定はハンドル単位で行われるため同じ結果になると推定するが、他プロセスでの確認は SPEC §13 の PoC 5・6 で行う)。

| 操作 | 結果 |
|---|---|
| 別ハンドルが `GENERIC_WRITE` (共有は全許可) で開いている対象を開く | `ERROR_SHARING_VIOLATION` (32) |
| 別ハンドルが `GENERIC_READ` (`FILE_SHARE_READ` のみ) で開いている対象を開く | 32 (相手が `FILE_SHARE_DELETE` を許していないため) |
| 存在しないファイル / 存在しない親の下 / 親がファイルのパスを開く | 2 / 3 / 3 |
| 同じハンドルからの内容の読み取り | 成功 |
| 同じハンドルでの `FileIdInfo`・`FileStandardInfo`・`FileStreamInfo` | 成功 |
| 同じハンドルでの `FSCTL_READ_FILE_USN_DATA` (入力 MinMajorVersion 2、MaxMajorVersion 3) | 成功。USN_RECORD_V3 が返り、`ParentFileReferenceNumber` は親ディレクトリを `FileIdInfo` で取得した 128 ビット File ID と一致。USN ジャーナルが有効なシステムドライブでの結果 |
| 保持中に別ハンドルで `file:newads` を作成 | **成功** |
| 保持中に別ハンドルを `FILE_WRITE_ATTRIBUTES` で開く / `SetFileAttributesW` で read-only を付与 | **成功** / **成功** |
| 保持中に `CreateHardLinkW` | **成功** (同じハンドルで見たリンク数が 2 になった) |

結論: 共有モードで書き込み・改名・削除は排除できるが、ADS 作成・属性変更・hardlink 追加は排除できない。SPEC §8.3 の 4 で削除直前に最終確認を行い、残る短い間を SPEC §12 の限界として記載した。

## V7. ディレクトリハンドルからの列挙 (実名確認、非破壊)

ディレクトリ (`MixedCase.TXT`、`LongNameDirectory`、`LongNameDirectory` を指す junction `JunctionDir` を含む) を `FILE_SHARE_READ | FILE_SHARE_WRITE`、`FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT` で開き、`GetFileInformationByHandleEx(FileIdExtdDirectoryRestartInfo → FileIdExtdDirectoryInfo)` で列挙した。`dir /x` では8.3 名 `MIXEDC~1.TXT`、`LONGNA~1`、`JUNCTI~1` が存在することを確認した。

| 確認 | 結果 |
|---|---|
| `FILE_LIST_DIRECTORY \| FILE_READ_ATTRIBUTES` のハンドル | 列挙成功。終端は `ERROR_NO_MORE_FILES` (18) |
| `FILE_READ_ATTRIBUTES` だけのハンドル | 列挙は `ERROR_ACCESS_DENIED` (5) で失敗 |
| 返る名前 | ディスク上の大小文字どおりのロング名 (`MixedCase.TXT`)。8.3 名は項目として返らない。`.` と `..` も返るため照合から除く |
| junction | 属性 0x410、reparse tag 0xA0000003 (`IO_REPARSE_TAG_MOUNT_POINT`) |
| File ID | 各項目の 128 ビット File ID が得られる |

次は V7 の時点では未確認で、SPEC §13 の PoC 8 で確認した (本書の「削除 PoC」): 8.3 名・大小文字違いのパスを ZIP の成分名にした場合の分類の結果、ディレクトリ単位で大文字小文字を区別する設定のディレクトリでの挙動、列挙中のディレクトリ変更の影響。

## 削除 PoC (SPEC §13、2026-10-02 実施)

SPEC §13 の PoC 1〜8 を、作業専用の一時ディレクトリ `%TEMP%\unextract-poc` と、その中に新規作成した VHD だけで行った。PoC コードは使い捨てで、リポジトリには入れていない。ZIP は使っていない (2回目の比較に相当する読み取りは、同じハンドルからの target 読み取りだけを確認した)。

### PoC の実施環境

- Windows 11 Home 10.0.26300 (`ver` は 10.0.26300.9457)、.NET SDK 10.0.401、Microsoft.NETCore.App 10.0.12。PoC は `net10.0-windows` の使い捨てコンソールアプリで、kernel32 を `DllImport` で呼んだ (製品で使う `LibraryImport` ではない)。PoC 4 の代替調査の1件だけ ntdll を呼んだ (報告のみ、下記)
- ボリューム: (1) システムボリューム C: (NTFS、USN ジャーナル有効) 上の `C:\Users\<user>\AppData\Local\Temp\unextract-poc`、(2) 同じディレクトリ内に作成した `test.vhdx` (64 MB、可変長、NTFS、ラベル UNXPOC、Z: に割り当て。USN ジャーナルは作成直後から非アクティブで `fsutil usn queryjournal` は 1179)
- 実行ユーザーは非管理者トークン。VHD の作成・マウント・`fsutil`・デタッチだけを、UAC で承認された管理者 PowerShell スクリプトで行った
- 「別プロセス」は、同じ実行ファイルを `Process.Start` で別プロセスとして起動し、標準入出力でコマンドを送って操作した (PID が異なることを毎回確認)

### 安全装置と作業範囲

- 起動時に `%TEMP%\unextract-poc` の各祖先 (`C:\` から) が reparse point でないことをハンドルで確認し、ルートの最終パスが入力と一致することを確認した。ルートは `FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES` (共有 R|W) で実行中保持した
- 書き込み・削除・改名・リンク作成の前に、対象を `FILE_FLAG_OPEN_REPARSE_POINT` で開き、ハンドルの `GetFinalPathNameByHandleW` の結果が許可領域の内側であること (`\` 境界付きの前方一致。比較する文字列はハンドルから得たもの) と、ルートから対象の親までの各ディレクトリが reparse point でないことを確認した。削除・改名はその確認をしたハンドルに対して行った
- **残る隙間**: 祖先ディレクトリの確認は、パスで開き直して行う。このため、祖先の確認から対象の操作までの間に短い TOCTOU が残る。作業領域を操作したのは PoC 自身だけである
- **後始末の再帰削除**: reparse point はリンクそのものを削除し、たどらない (列挙の属性で判定)。修正前は、再帰の入口 (サブディレクトリを開く時点) で対象が reparse point に変わっていないかの再確認がなく、その場合の挙動は未実測だった。後始末の実行前に、入口で `FileAttributeTagInfo` を確認し、reparse point ならリンクだけを削除して戻る処理を追加した
- **flags の区別**: 後始末の削除 (`Guard.Delete`) は `FileDispositionInfoEx` に 0x13 (`DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE`) を使う。read-only のテストファイルを消すためである。測定側の `DispEx` 呼び出しは全て確認し、flags は 0x1、0x3 (参考として 0x0、0x2、0x8) だけで、`IGNORE_READONLY_ATTRIBUTE` (0x10) を含むものは無かった
- **手順上の不備**: PoC 1〜3 の初回実行で、B の「同じ名前で新規作成」の1回だけ `CheckCreate` (作成前の安全確認) を通さずに作成した。編集用スクリプトが実行されていなかったためである。作成先は確認済みの作業ディレクトリ内だった。修正後に PoC 1〜3 を再実行し、本書の値は再実行の結果である (初回と同じ結果)
- VHD は、作成前に `test.vhdx` が存在しないこと、作成直後であること、`Get-DiskImage` のイメージパス → ディスク番号 → パーティション → ボリュームの対応、ボリュームのラベル・ファイルシステム・サイズ、作成前後のボリューム集合の差がちょうどこの1個であること、システムボリュームでないことを確認した。その後マーカーファイルを置き、PoC 側でボリューム GUID とマーカーを照合してから許可領域に加えた。`fsutil` (USN 照会、`setCaseSensitiveInfo`) と `icacls` の付与は、この VHD のボリュームに対してだけ行った。USN ジャーナルは元から非アクティブだったため、削除操作はしていない
- VHD は管理者スクリプトでデタッチし (`Dismount-DiskImage`、事前にボリューム GUID を照合)、`test.vhdx` を削除した。`Attached = False`、ファイル無し、Z: 無しを確認した

### PoC 1〜3: 削除の成立、disposition flags、`DeletePending`

SPEC §8.1 の削除用ハンドル (`GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE`、`FILE_SHARE_READ`、`FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL`) を変えずに使い、`SetFileInformationByHandle(FileDispositionInfoEx)` を呼んだ。C: と VHD (USN 非アクティブ) の両方で同じ結果だった。

指示の前に同じハンドルで、内容の全読み取り (6600 バイト一致)、File ID、親 File ID (`FSCTL_READ_FILE_USN_DATA`)、リンク数、ADS 一覧、属性、最終パスが全て取得できた。

他のハンドルなし:

| flags | 結果 | 指示後・クローズ前 (同じハンドル) | 指示後・クローズ前 (名前) | クローズ後 (名前) |
|---|---|---|---|---|
| `DELETE` (0x1) | 成功 | `DeletePending` = true、**`NumberOfLinks` = 0**、File ID・親 File ID・最終パス・ストリーム一覧は取得でき、内容も読める | 列挙には出る。`GetFileAttributesW` と新規オープンは 5 | 消える (2、列挙に出ない) |
| `DELETE \| POSIX_SEMANTICS` (0x3) | 成功 | 同上 | 同上 | 消える (2、列挙に出ない) |

別のハンドル (同一プロセス) が `GENERIC_READ` で開いている場合:

| 相手の共有 | flags | 削除用オープン | 自分のクローズ後 (名前) | 相手のハンドル | 相手のクローズ後 |
|---|---|---|---|---|---|
| R (DELETE を共有しない) | 0x1 / 0x3 | 32 | — | — | — |
| R\|D | 0x1 | 成功、指示成功 | **名前が残る** (列挙に出る、オープンは 5、同名の新規作成も 5) | `DeletePending` = true、読み続けられる | 消える |
| R\|D | 0x3 | 成功、指示成功 | 消える。同名の新規作成も成功 | 読み続けられる。最終パスは `C:\$Extend\$Deleted\…` | (同名で作り直したファイルが残る) |
| R\|W\|D | 0x1 / 0x3 | R\|D と同じ | R\|D と同じ | R\|D と同じ | R\|D と同じ |

read-only 属性 (`IGNORE_READONLY_ATTRIBUTE` を指定しない):

| flags | 削除用オープン | 指示 | `DeletePending` | クローズ後 |
|---|---|---|---|---|
| 0x1 / 0x3 | 成功 | 失敗、5 (`ERROR_ACCESS_DENIED`) | false のまま | ファイルは残る |
| 従来の `FileDispositionInfo` (TRUE) | 成功 | 失敗、5 | false | 残る |

参考:

- 同じハンドルへの従来の `FileDispositionInfo` (TRUE) も成功し、0x1 と同じ挙動だった (PoC 1 の代替)
- 0x3 を指示した後に flags 0 で指示し直すと、`DeletePending` は false に戻り、クローズ後もファイルは残った
- flags 0x0、0x2 (POSIX のみ)、0x8 (`ON_CLOSE` のみ) は、呼び出しが**成功を返す**が `DeletePending` は false のままで、何も削除されなかった

判定:

- PoC 1 (削除の成立): **成立**。§8.1 の構成のハンドルから、0x1 と 0x3 のどちらでも削除できた
- PoC 2 (必要最小限の flags): **成立に必要な最小限は `FILE_DISPOSITION_FLAG_DELETE` (0x1) 単独**と実測で確定できる。POSIX semantics は削除の成立に必要ない。違いは、`FILE_SHARE_DELETE` 付きで開いている他のハンドルがある場合に、名前がいつ消えるかだけである (0x1 は全ハンドルが閉じるまで残り、0x3 は自分のクローズで消える)。どちらを採用するかは、この違いを踏まえて人間が判断する (その後、SPEC §8.3 の 5 で 0x3 に確定した。理由は `PLAN_DECISIONS.md` DEC-10)
- PoC 3 (成立の観測): **成立**。指示後・クローズ前に同じハンドルの `FILE_STANDARD_INFO.DeletePending` が true になる。失敗 (read-only) では false のままだった。クローズ後は同じハンドルで観測できない。名前が消えたかどうかは、他のハンドルの有無と flags によって変わる (上表)
- 公式仕様との照合: `FILE_DISPOSITION_INFORMATION_EX` の記述 (POSIX semantics では削除したハンドルのクローズで名前が消え、既存のハンドルはデータにアクセスし続けられる) と一致した。`NumberOfLinks` が 0 になる点と `$Extend\$Deleted` への移動は、今回参照した資料では確認していない (実測のみ)

### PoC 4: 同じハンドルからの情報取得、USN 非アクティブのボリューム

| 項目 | C: (USN 有効) | VHD Z: (USN 非アクティブ、照会は 1179) |
|---|---|---|
| 内容の全読み取り | 成功、一致 | 成功、一致 |
| `FileIdInfo` | 成功 | 成功 |
| `FSCTL_READ_FILE_USN_DATA` (Min 2 / Max 3) | 成功、USN_RECORD_V3 | **成功**、USN_RECORD_V3、`Usn` = 0 |
| 上記の `ParentFileReferenceNumber` と、親ディレクトリを開いて得た `FileIdInfo` | 一致 | 一致 |
| `FileStandardInfo` (リンク数 2 = hardlink 付きの検証ファイル) | 成功 | 成功 |
| `FileBasicInfo`・`FileAttributeTagInfo` | 成功 | 成功 |
| `FileStreamInfo` | `::$DATA,:extra:$DATA` | 同左 |
| `GetFinalPathNameByHandleW(0)` | 成功 | 成功 (`Z:\w4\parent\t.txt`) |

`FSCTL_READ_FILE_USN_DATA` は、`FILE_READ_ATTRIBUTES` だけのハンドル (§8.4 の識別確認の構成) でも成功した (PoC 7 で使用)。

親 File ID の別の取得手段 (採用はしない。報告のみ。どちらのボリュームでも同じ結果):

- 親ディレクトリを列挙用に開き `FileIdExtdDirectoryInfo` で列挙すると、`.` の項目にそのディレクトリ自身の 128 ビット File ID が入っていた。ただし、親ディレクトリをパスで開く必要があり、ファイルのハンドルだけからは得られない
- ファイルのハンドルに `NtQueryInformationFile(FileHardLinkInformation)` を使うと、リンクごとに 64 ビットの `ParentFileId` が返った。ntdll の呼び出しであり、SPEC §8.1 は ntdll の未文書 API を使わないと定めている
- `FindFirstFileNameW` はリンク名だけを返し、親の ID は返さない (文書による。実測していない)

判定: **成立**。USN ジャーナルが非アクティブのボリュームでも、親 File ID が取得できた。SPEC §13 の「親 File ID だけが取れない環境」は今回の環境では発生しなかった。USN 機能を持たないファイルシステムや、ジャーナルを一度も作成していない別の状態での挙動は確認していない。

### PoC 5: 別プロセスとの共有モード

(a) 別プロセスが先に開いている対象を、削除用ハンドルで開く:

| 別プロセスのオープン | 削除用オープン | 備考 |
|---|---|---|
| `GENERIC_READ\|GENERIC_WRITE`、共有 R (エディタ相当) | 32 | |
| `GENERIC_WRITE`、共有 R\|W\|D | 32 | |
| `GENERIC_READ`、共有 R | 32 | 相手が DELETE を共有しない |
| `GENERIC_READ`、共有 R\|W | 32 | 同上 |
| `GENERIC_READ`、共有 R\|W\|D (インデクサ・バックアップ相当) | **成功** | 削除の指示も成功 (`DeletePending` = true) |
| `GENERIC_READ`、共有 R\|D | **成功** | 同上 |
| `FILE_READ_ATTRIBUTES`、共有 R\|W\|D (AV のメタデータ参照相当) | 成功 | |
| `FILE_READ_ATTRIBUTES`、共有 0 | 成功 | 共有モードの判定に参加しない |
| `DELETE`、共有 R\|W\|D | 32 | |

(b) 削除用ハンドルを開いている間に、別プロセスが試す:

| 別プロセスの操作 | 結果 |
|---|---|
| `GENERIC_WRITE` (共有 R\|W\|D)、`GENERIC_READ\|GENERIC_WRITE` (共有 R)、`FILE_WRITE_DATA` | 32 |
| `GENERIC_READ`、共有 R / R\|W | 32 |
| `GENERIC_READ`、共有 R\|W\|D / R\|D | 成功 (読み取りのみ) |
| `FILE_READ_ATTRIBUTES` / `FILE_WRITE_ATTRIBUTES` | 成功 |
| `DELETE` のオープン、`MoveFileExW`、`DeleteFileW` | 32 |

いずれの場合も、削除用ハンドルから見た File ID・メタデータ・内容は変わらなかった。

(c) 別プロセスが `GENERIC_READ` (共有 R\|W\|D) で開いたまま、こちらが削除してクローズした場合: 0x1 では名前が残り (列挙に出る、オープンは 5)、別プロセスのクローズで消えた。0x3 ではこちらのクローズで名前が消えた。どちらも、別プロセスは `DeletePending` = true を観測し、内容を最後まで読めた。

判定: **成立**。書き込みのために開いている別プロセスがあれば削除用オープンは 32 で失敗し、削除用ハンドルを開いている間は、別プロセスは書き込み・改名・削除のために開けない。読み取りだけで `FILE_SHARE_DELETE` を許す別プロセスとは共存し、その場合でも削除は成立する。

### PoC 6: 削除用ハンドルを開いている間の、別プロセスからのメタデータ変更

| 別プロセスの操作 | 結果 | 最終確認 (§8.3 の 4) での検出 | 検出後もあえて削除した場合 (測定のみ) |
|---|---|---|---|
| ADS 作成 (`file:newads`) | 成功 | ストリーム一覧が変化 (`ChangeTime` も変化) | 指示成功。クローズ後、名前は消えた |
| `SetFileAttributesW` で READONLY を付与 | 成功 | 属性 0x20→0x21 | 指示が 5 で失敗し、ファイルは残った |
| `SetFileAttributesW` で HIDDEN を付与 | 成功 | 属性 0x20→0x22 (HIDDEN は許可属性だが、スナップショットとの比較で不一致になる) | 指示成功 |
| `CreateHardLinkW` | 成功 | リンク数 1→2 | 指示成功。削除後もリンク数 1 で `DeletePending` = true、クローズ後は元の名前だけが消え、hardlink の名前とデータは残った |

判定: SPEC §13 の「できる場合」に該当する。別プロセスからでも全て成功し、いずれも同じハンドルでの最終確認で検出できた。最終確認から削除の指示までの間に起きた場合の結果は、SPEC §12 の記述 (ADS はファイルと一緒に消える、hardlink は他の名前にデータが残る、read-only は削除の失敗) と一致した。窓そのものの長さは測定していない。

予備実測 V6 (同一プロセスの別ハンドル) との差: **なし**。PoC 5・6 の別プロセスでの結果は、V6 の全項目と同じだった。

### PoC 7: 削除用オープンのエラーコードと識別確認

各状況で、比較用ハンドル (§8.1) から File ID・親 File ID・最終パス・`Directory`・`DeletePending` のスナップショットを取った後、状況を作り、削除用オープンを試した。続けて、§8.4 の識別確認 (`FILE_READ_ATTRIBUTES` のみ、`BACKUP_SEMANTICS | OPEN_REPARSE_POINT | OPEN_NO_RECALL`) を**全ての状況で**実行し、どう判定するかを記録した (SPEC では、識別確認は 32 と 5 のときだけ行う)。他プロセスの操作は別プロセスで行った。

| # | 状況 | 削除用オープン | 識別確認の判定 | 識別確認の詳細 | §8.4 の分類 (表の適用結果) |
|---|---|---|---|---|---|
| 1 | 別プロセスが `GENERIC_WRITE` (共有 R\|W\|D) で開いている | 32 | 一致に見える | | `DELETE_FAILED` |
| 2 | 別プロセスが `GENERIC_READ` (共有 R のみ) で開いている | 32 | 一致に見える | | `DELETE_FAILED` |
| 3 | ACL: 対象の READ_DATA を拒否 | 5 | 一致に見える | | `DELETE_FAILED` |
| 4 | ACL: 対象の DELETE を拒否 (親の DELETE_CHILD は許可のまま) | **成功** | 一致に見える | 段階2の比較も差なし (PoC で比較した File ID、親 File ID、最終パス、`Directory`、`DeletePending` に限る。`ChangeTime` を含めた結果は「E-2 実測」) | オープン成功 (段階2 へ) |
| 5 | ACL: 対象の DELETE と親の DELETE_CHILD を拒否 | 5 | 一致に見える | | `DELETE_FAILED` |
| 6 | 対象がディレクトリに差し替えられた | 5 | 不一致 | File ID、`Directory` false→true | 停止 |
| 7 | 削除保留中 (別プロセスが `FileDispositionInfo` を設定してハンドルを保持) | 5 | **失敗** | 識別確認のオープン自体が 5 | 停止 |
| 8 | 対象消失 (改名で退避) | 2 | 失敗 | オープン 2 | 停止 |
| 9 | 親ディレクトリ消失 (改名で退避) | 3 | 失敗 | オープン 3 | 停止 |
| 10 | 親がファイルに差し替えられた | 3 | 失敗 | オープン 3 | 停止 |
| 11 | 対象が junction (ディレクトリ) に差し替えられた | 5 | 不一致 | File ID、`Directory`、reparse | 停止 |
| 12 | 対象がファイル symlink に差し替えられた | **未実施** | | symlink を作成できなかった (1314、特権なし・開発者モード無効) | |
| 13 | 途中のディレクトリが junction に差し替えられた (同じファイルに到達) | 成功 | 不一致 | 最終パスだけが不一致 (親 File ID は同じ) | オープン成功 (段階2 で最終パス不一致) |
| 14 | 途中のディレクトリが、リンク先の存在しない junction に差し替えられた | 3 | 失敗 | オープン 3 | 停止 |
| 15 | 同名の別ファイルに差し替えられた | 成功 | 不一致 | File ID | オープン成功 (段階2 で File ID 不一致) |
| 16 | 親ディレクトリを別プロセスが `FILE_LIST_DIRECTORY` (共有 R) で保持 (対象は未使用) | 成功 | 一致に見える | 段階2の比較も差なし | オープン成功 |
| 17 | クラウド placeholder | **未実施** | | 再現環境なし | |

実機で確認した事実:

- 観測したエラーコードは 32、5、2、3 の4種類だけだった。5 は、ACL による拒否、対象がディレクトリ・ディレクトリ junction、削除保留中で共通であり、**コードだけではこれらを区別できない**
- 識別確認は、5 の状況のうち ACL 拒否を「一致に見える」と判定し、ディレクトリ化・junction 化を「不一致」、削除保留中を「失敗」(識別確認のオープン自体が 5) と判定した。共有違反 (32) は2件とも「一致に見える」だった
- 識別確認の判定は「その時点でスナップショットと一致する通常ファイルに見えるか」だけである。拒否の理由 (使用中か権限不足か) は判定していない。拒否されたオープンと識別確認が同じ個体を見たかどうかも保証しない (§8.4 の限界のとおり)。今回の結果はこの限界を超えるものではない

### PoC 8: 実名確認と、削除フェーズでの差し替え検出

(a)〜(c) 実名確認 (§6.2 の手順: 列挙用ハンドルで `FileIdExtdDirectoryInfo` を列挙し、序数比較。途中のディレクトリは開き直して File ID と最終パスを照合) を C: で行った。8.3 名は `FileIdBothDirectoryInfo` で確認した (`LONGFI~1.TXT`、`LONGDI~1`)。

| ZIP 側の名前 | 結果 | 参考: パスで `CreateFileW` した場合 |
|---|---|---|
| `SubDir/MixedCase.txt` | 見つかる | 開ける |
| `SubDir/mixedcase.txt`、`subdir/MixedCase.txt`、`SUBDIR/MIXEDCASE.TXT` | `MISSING` | 開ける (実名 `SubDir\MixedCase.txt` に到達) |
| `LONGFI~1.TXT`、`LONGDI~1/in.txt` (8.3 名) | `MISSING` | 開ける (ロング名に到達) |
| `Junc/in.txt` (`Junc` は junction) | `SKIPPED_SPECIAL_FILE` (列挙の属性 0x410、tag 0xA0000003) | リンク先の `jtarget\in.txt` に到達 |
| `Junc` | `SKIPPED_SPECIAL_FILE` | |

(d) 削除フェーズでの差し替え (比較用ハンドルのスナップショットと、削除用ハンドルの値を比較):

| 差し替え | 削除用オープン | 不一致になった項目 |
|---|---|---|
| 祖父ディレクトリ `A` を、退避した `A` を指す junction に差し替え | 成功 | 最終パスだけ |
| 親ディレクトリ `B` を、退避した `B` を指す junction に差し替え | 成功 | 最終パスだけ |
| 祖父 `A` を、同名の別ディレクトリ (中に `B/t.txt` の複製) に差し替え | 成功 | File ID、親 File ID |
| 親 `B` を同名の別ディレクトリに差し替え、**同じファイルを移動**して入れる | 成功 | **親 File ID だけ** (File ID と最終パスは一致) |
| 祖父 `A` を大文字小文字だけ違う `a` に改名 | 成功 | 最終パスだけ (序数比較) |

(e) 列挙中のディレクトリ変更 (別プロセス): 名前が不変の 5,000 ファイルがあるディレクトリで、別プロセスがファイルの作成・改名・削除を続ける間に、8秒ずつ列挙を繰り返した。

| 条件 | 結果 |
|---|---|
| バッファ 4 KiB (1回の呼び出しで約30項目)、181回の列挙 (別プロセスの操作 5,072回) | 不変の名前の見落とし 0回、同じ名前の重複 0回、列挙エラー 0回 |
| バッファ 64 KiB、189回の列挙 (操作 4,973回) | 同上 |
| 別プロセスが対象ファイルを `s_010001` ↔ `s_000001x` に改名し続ける (9,612回)、バッファ 4 KiB、2,347回の列挙 | 旧名だけ 714、新名だけ 781、**両方の名前で出る 426、どちらの名前でも出ない 426** |

- 列挙中に改名された項目は、1回の列挙で見落とされることも、2つの名前で重複して出ることもある。これは実測した事実である
- 見落とされた場合、その名前は `MISSING` になり、内容は読まず削除もしない。列挙で見つかった名前が直後に改名されていた場合は、続く比較用オープンが失敗する (2) か、File ID が一致しないため FATAL になる。これは §6.1 の手順3と PoC 7 #8・#15 の結果からの**推定**で、この競合そのものを組み合わせた実測はしていない。後から、同名の別ファイルへの差し替えが起きた場合は、削除フェーズの再オープンで File ID の不一致として検出した (PoC 7 #15)

(f) ディレクトリ単位で大文字小文字を区別する NTFS ディレクトリ (VHD の `Z:\cs`。`fsutil file setCaseSensitiveInfo` で有効化):

| 確認 | 結果 |
|---|---|
| `Foo` がある状態で `foo` を `CREATE_NEW` | 成功し、両方が共存 (別の File ID) |
| 列挙 | `Foo`、`foo`、`Dir`、`dir` が別の項目として返る |
| ZIP `Foo` / `foo` | それぞれ正しい個体に対応 (列挙の File ID = 比較用オープンの File ID、最終パスが序数一致、内容も各個体のもの)。削除用オープンも同じ個体を開いた |
| ZIP `Dir/x.txt` / `dir/x.txt` | それぞれ正しい個体に対応 |
| ZIP `FOO`、`DIR/x.txt` | `MISSING` |
| パスで `CreateFileW("FOO")` | 2 (このディレクトリではパスでの検索も大文字小文字を区別する) |

(g) 大きいディレクトリの列挙 (任意項目、C:): 空ファイル 100,000 個のディレクトリを、64 KiB バッファで全件列挙するのに 115〜176 ms (3回) かかった。参考として、パスでの1件ずつのオープン 1,000 回は 43 ms だった。

判定: (a)〜(c) は規則どおり**成立**。削除フェーズの差し替えは、最終パスまたは親 File ID の比較で全て不一致になり、**成立**。ディレクトリ単位で大文字小文字を区別するディレクトリでも、正しい個体に対応した。

### PoC の結果一覧

| PoC | 結果 | SPEC §13 の「不成立の場合の扱い」 |
|---|---|---|
| 1 | 成立 | 該当しない |
| 2 | 成立。最小限は 0x1 (POSIX は成立に不要。名前が消える時点だけが異なる) | 該当しない |
| 3 | 成立 (クローズ前の `DeletePending` = true) | 該当しない |
| 4 | 成立 (USN 非アクティブでも親 File ID を取得) | 該当しない |
| 5 | 成立 | 該当しない |
| 6 | 「できる」(別プロセスから全て成功、最終確認で全て検出) | 「できる場合」の記載どおり (§12) |
| 7 | 一覧を作成。ファイル symlink とクラウド placeholder は未実施 | PoC 実施後の文書修正で `PLAN.md` §4 の対応表に反映した |
| 8 | 成立 | 該当しない |

### SPEC と異なる結果、または SPEC が想定していなかった発見

SPEC の修正案は書かない。事実だけを記す。扱いは人間が判断する。

1. 削除を指示した後、同じハンドル (と他のハンドル) の `FILE_STANDARD_INFO.NumberOfLinks` は **0** になる (`DeletePending` = true と同時)。SPEC は、削除の成立確認 (§8.3 の 6) で `DeletePending` だけに触れている
2. `FileDispositionInfoEx` は、`DELETE` ビットを含まない flags (0x0、0x2、0x8) でも**成功を返し**、何も削除しない。この場合は `DeletePending` = false で判別できた
3. 削除保留中の対象では、削除用オープンだけでなく §8.4 の識別確認のオープンも 5 で失敗する。そのため、識別確認で `DeletePending` = true を観測することはなく、判定は「失敗」になる (§8.4 の分類は停止で変わらない)
4. エラーコード 5 は、ACL による拒否、対象のディレクトリ化・ディレクトリ junction 化、削除保留中に共通である。これらの区別は識別確認の結果だけによる
5. 対象ファイルの DELETE を ACL で拒否しても、親ディレクトリが DELETE_CHILD を許可していると、削除用オープンは成功する。親の DELETE_CHILD も拒否すると 5 になる
6. read-only の対象では、削除用オープンは成功し、削除の指示が 5 (`ERROR_ACCESS_DENIED`) で失敗する (`DeletePending` は false のまま)
7. flags 0x1 では、`FILE_SHARE_DELETE` 付きで開いている他のハンドル (別プロセスのインデクサ・バックアップ相当を含む) があると、自分のクローズ後も名前が削除保留のまま残る。その間、その名前のオープンも同名の新規作成も 5 になる。0x3 ではその場合でも自分のクローズで名前が消え、相手のハンドルの最終パスは `C:\$Extend\$Deleted\…` になる。SPEC §13 の 2 は、POSIX が使えない場合に名前が残ることを想定しているが、POSIX が使える場合の 0x1 と 0x3 の違いは記述していない
8. 削除の指示はクローズ前なら取り消せる (flags 0 で指示し直すと `DeletePending` が false に戻り、ファイルは残る)
9. USN ジャーナルが非アクティブのボリュームでも、`FSCTL_READ_FILE_USN_DATA` は成功し、`Usn` = 0 の USN_RECORD_V3 で正しい親 File ID を返した。`FILE_READ_ATTRIBUTES` だけのハンドルでも成功した
10. 親ディレクトリを同名の別ディレクトリに差し替え、同じファイルを移動して入れた場合、File ID と最終パスは一致し、**親 File ID だけが不一致**になった (親 File ID の比較がなければ検出できない差し替え)
11. 途中のディレクトリを junction に差し替えた場合と、大文字小文字だけの改名の場合は、削除用オープンが成功し、**最終パスだけ**が不一致になった (親 File ID は一致)
12. 列挙中に改名された項目は、1回の列挙で見落とされることも、旧名と新名の両方で出ることもある (2,347回中 426回ずつ)。§6.2 はこの挙動に触れていない。名前が不変の項目の見落としは観測しなかった
13. ディレクトリ単位で大文字小文字を区別するディレクトリでは、パスでの `CreateFileW` も大文字小文字を区別する (`FOO` は 2)。実名確認の結果は §6.2 の 4 の記述どおりだった

### 実測と推定の区別

- 実測: 上の各表の値。PoC 5・6・7 の他プロセスの操作は、全て独立した別プロセスで行った
- 推定: PoC 8 (e) の「列挙で見つけた名前が直後に改名された場合は FATAL になる」。PoC 4 の、USN 機能を持たないファイルシステムでの挙動 (未確認)。`FindFirstFileNameW` の挙動 (文書のみ)
- 未実施: PoC 7 のファイル symlink (作成に必要な特権がなく 1314)、クラウド placeholder (再現環境なし)

### 後始末

- VHD: 管理者スクリプトでデタッチし、`test.vhdx` を削除済み (`Attached = False`、ファイル無し、Z: 無し)
- 作業ディレクトリ `w123`、`w4`、`w56`、`w7`、`w8` は、本記録の作成時点では未削除だった。`w7` と `w8` には、作業領域内を指す junction が6個あった (1個はリンク先が存在しない)
- (本記録の作成後に追記) 安全装置を通した再帰削除 (reparse point はリンクだけを削除) で、`w123` → `w4` → `w56` → `w7` → `w8` の順に削除した。全て終了コード 0 で、安全装置の違反は 0 件だった。`w8` (約 105,000 ファイル) は 32.3 秒かかった。他のディレクトリの所要時間は、計測用コマンドが無く記録できなかった。削除後、作業領域内の reparse point は 0 件 (`dir /AL /S` と PowerShell で確認)
- `%TEMP%` 直下の項目を前後で比較したところ、GUID 名の `.tmp` が1件入れ替わった。PoC の削除は全て作業領域内であることをハンドルで確認してから行っているため、他のアプリによるものと推定する。リポジトリの変更は本書と `PLAN_HISTORY.md` だけである
- 削除前に、結果ログ `results\` を `C:\Users\<user>\unextract-poc-results\` に退避した (元は削除せずにコピー)。7 ファイル、22,439 バイトで、元とコピーのファイル数・合計サイズが一致し、ファイルごとの SHA-256 の差分は 0 件だった
- ルート `%TEMP%\unextract-poc` (PoC のソース、ビルド出力、VHD 用スクリプトとログ、`vhd.json`、結果ログの元) は、削除前に次を確認した: `poc.exe` のプロセスが無いこと、ルート自体の `LinkType` が空であること、`dir /AL /S` で reparse point が見つからないこと。その後、利用者の承認を得て `cmd /c rmdir /s /q` で削除した。削除後に `Test-Path` が False であること、退避先が 7 ファイル・22,439 バイトのまま残っていることを確認した
- 最初の `rmdir` は、Git Bash が引数の `/q` をパス `Q:` に変換したため、引数エラーになり何も削除しなかった (exit 1、直後の `Test-Path` は True)。`MSYS_NO_PATHCONV=1` を付けて同じコマンドを再実行し、exit 0 で削除した
- 承認の状況 (利用者からの報告): PoC 実行ファイル (`./bin/poc.exe`) の直接実行は Claude Code の ask ルールの対象外で、実行時に承認を求められなかった。PoC 1〜8 の測定中の削除と、作業ディレクトリ `w123`〜`w8` の後始末も、これに含まれる。結果の退避の前に利用者が ask ルール (`Bash(./bin/poc.exe:*)`、`Bash(*poc.exe*)`、`Bash(cmd:*)`、`Bash(rmdir:*)`) を追加し、以後は承認を求められるようになった。VHD の作成・デタッチは、これとは別に UAC で承認された

## C-3 実機確認 (dry-run、2026-10-02)

段階 C-3 で CLI を組み込んだ後、ビルド済みの `unextract.exe` (Debug、`src\Unextract.Cli\bin\Debug\net10.0-windows\`) を `--dry-run` だけで実行した。target は作業専用の一時ディレクトリ (Claude Code のセッション用 scratchpad の下の `manual-c3b\`) に Python の `zipfile` で作った fixture だけで、実在のユーザーデータは使っていない。出力は Git Bash からパイプで受け取った標準出力 (UTF-8) である。対話的なコンソールでの表示は確認していない。

1. 全カテゴリーを含む ZIP (target に `same.txt` 一致、`changed.txt` 1 バイト違い、`docs\ads.txt` に `Zone.Identifier`、ZIP にない `unrelated.txt`)。終了コード 0。`unrelated.txt` は表示されない。

```text
MATCHED (1):
  same.txt
MODIFIED (1):
  changed.txt
MISSING (1):
  missing.txt
SKIPPED_SPECIAL_FILE (1):
  docs/ads.txt
DIRECTORY (1):
  docs/
合計: 5 エントリ (MATCHED 1、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)
--dry-run のため削除しません。
```

2. CRC 不一致 (5 エントリ中 3 番目の `bad.txt` の Central Directory の CRC-32 だけを書き換え、target には同じサイズの `bad.txt`)。終了コード 1。判定済み 2 件のパス、原因エントリと原因、未判定 2 件の件数、削除0件を表示した。

```text
判定済み: 2 エントリ
MATCHED (2):
  a.txt
  b.txt
MODIFIED (0):
MISSING (0):
SKIPPED_SPECIAL_FILE (0):
DIRECTORY (0):
FATAL: エントリ #3 "bad.txt": エントリの CRC-32 が一致しません
未判定: 2 エントリ
削除開始前に中止しました。削除0件。
```

3. 日本語名を含む ZIP (全エントリに UTF-8 フラグ (bit 11) あり。`zipfile` の `flag_bits` で確認)。終了コード 0。日本語と `café░` が文字化け・例外なく表示された。

```text
MATCHED (2):
  資料/報告書.txt
  café░.txt
MODIFIED (0):
MISSING (2):
  資料/写真一覧.csv
  ファイル名.txt
SKIPPED_SPECIAL_FILE (0):
DIRECTORY (0):
合計: 4 エントリ (MATCHED 2、MODIFIED 0、MISSING 2、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0)
--dry-run のため削除しません。
```

CLI は実行中だけ `Console.OutputEncoding` を UTF-8 (BOM なし) にし、終了時に元へ戻す。実行ファイルの直接実行で承認画面が出たかどうかは、実行した側 (Claude) からは観測できないため記録していない。

## E-2 実測 (削除フェーズ、2026-10-02)

段階 E-2 で削除フェーズを実装した後、`tests/Unextract.Windows.Tests` の実 NTFS テストと、ビルド済み `unextract.exe` の手動実行で確認した。環境は「実施環境」と同じ (Windows 11 Home 10.0.26300、システムドライブ C: の NTFS、非管理者トークン、.NET SDK 10.0.401)。実削除は、テストが作った一意な fixture (`tests\Unextract.Windows.Tests\bin\Debug\net10.0-windows\fixtures\<テスト名>-<GUID>\`) と、Claude Code のセッション用 scratchpad の下に新しく作った `manual-e2\` の中のファイルだけで行った。テストでは、削除の指示の直前に毎回、テスト側のガード (`DeletionGuard`) が削除用ハンドルの最終パスが fixture 内であることと、fixture から対象までの各成分が reparse point でないことを確認した (違反0件)。「別プロセス」は、テストアセンブリのビルド済み実行ファイル (`Unextract.Windows.Tests.exe`) を `Process.Start` で起動して使った。

### D22: 対象の DELETE だけを ACL で拒否 (親の DELETE_CHILD は許可)

`icacls <対象> /deny <自分の SID>:(DE)` で拒否し、テストの終わりに `/remove:d` で戻した (対象が削除された場合は戻す対象なし)。拒否する時点を変えた2通りを実測した。

| 拒否した時点 | 段階1 (削除用オープン) | 段階2 (再検証) | 段階5 (削除の指示) | 結果 |
|---|---|---|---|---|
| 初回分類の前 (スナップショットは拒否後の状態) | 成功 | 差なし | **成功**。同じハンドルの `DeletePending` = true | 削除された (`DELETED`)。前後の a.txt・c.txt も削除、ZIP にないファイルと ZIP は残った。終了状態は成功 (0) |
| 確認待ち中 (`PLAN_TESTS.md` の D22 の記述どおり) | 成功 | **不一致 (`ChangeTime`)** で停止 | 到達しない | 対象は残り (DENY は付いたまま、後で除去)、以後は未処理。終了状態はエラー (1) |

- 段階5は、オープン時に DELETE アクセスが与えられていれば、ファイル自体の ACL が DELETE を拒否していても成功した。`PLAN_DECISIONS.md` DEC-12 の推定 (成功する可能性が高い) と一致し、扱い (Windows の通常の ACL の挙動として受け入れる) の前提どおりだった。
- ACL の変更は対象の `ChangeTime` を更新する。そのため、確認待ち中に ACL を変えた場合は、削除フェーズの同一性の再検証 (SPEC §8.3 の 2) で停止する。PoC 7 #4 の「段階2の比較も差なし」は、PoC で比較した項目 (File ID、親 File ID、最終パス、`Directory`、`DeletePending`) についての結果であり、`ChangeTime` を含む §8.3 の 2 の全項目ではない。どちらの時点でも誤削除はなかった。

### 実測で分かったこと (テストの組み方に関わるもの)

- 親ディレクトリの DACL を `icacls /deny` で変えると、子のファイルの ACL も書き直され (継承の再適用)、子の `ChangeTime` が変わった。D11 (対象の DELETE と親の DELETE_CHILD を拒否) で、確認待ち中に親の DACL を変えると、無関係な先頭の MATCHED (a.txt) が `ChangeTime` の不一致で停止した (安全側の停止)。テストでは親の DELETE_CHILD の拒否を初回分類の前に設定するよう改めた。
- 削除用ハンドル (DELETE アクセスを持つ) を開いている間に、同じプロセスから新しい ADS を `FileShare.Read` で作ろうとすると 32 (共有違反) になった。共有に DELETE を含めると (`FileShare.ReadWrite | FileShare.Delete`。PoC 6 の別プロセスと同じ R|W|D) 作成でき、最終確認でストリーム一覧の不一致として検出された (D16)。
- D10 (内容を書き換えた後、同じハンドルで `FILE_BASIC_INFO` の作成・アクセス・更新・変更日時と属性を元の値に書き戻す) では、書き戻した値がハンドルを閉じた後も保たれ、同一性の再検証を通過し、2回目の全バイト比較で停止した。

### Win テストの結果 (実削除を伴うもの)

D02、D04 (4通り)、D05 (ADS、hardlink、read-only、system、対象の junction 化、途中の junction 化、途中の同名の別ディレクトリへの置換)、D07、D09/D23 (別プロセスの書き込み・改名のオープンは全て 32)、D10、D11 (別プロセスの書き込み中・`FILE_SHARE_DELETE` なしの読み取り中は 32、READ_DATA 拒否・DELETE と親の DELETE_CHILD の拒否は 5。いずれも識別確認で一致に見えて `DELETE_FAILED`、後続は削除)、D14 (対象消失 2、親の消失 3、ディレクトリ化 5 + 識別確認の File ID 不一致、削除保留中 5 + 識別確認も 5)、D16 (ADS、hardlink、read-only)、D19 (親 File ID だけの不一致)、D20 (junction、大文字小文字だけの改名で最終パスだけの不一致)、D21 (指示が 5 で失敗、対象は残り未処理 1)、D22 (上表)。全て期待どおりで、誤削除はなかった。

未確認のまま残るもの: ファイル symlink への差し替え (作成に必要な特権がない)、クラウド placeholder (再現環境がない)、EFS で暗号化された対象の削除 (今回は実施していない)。SPEC §14 のとおり成立と見なさない。

### 手動確認 (Debug ビルドの `unextract.exe`)

Debug ビルドの `src\Unextract.Cli\bin\Debug\net10.0-windows\unextract.exe` を、Git Bash から scratchpad の `manual-e2\` の fixture (Python の `zipfile` で作成) に対して実行した。実在のユーザーデータは使っていない。標準出力と標準エラー出力を分けて受け取った (標準エラー出力をリダイレクトしたため進捗は表示されない)。実行ファイルの実行時の承認画面の有無は記録していない。

1. `run\`: ZIP は `same.txt`、`changed.txt`、`missing.txt`、`d/`、`d/deep.txt`、`emptydir/`。target には `same.txt` と `d\deep.txt` が一致、`changed.txt` が不一致、ZIP にない `unrelated.txt`。
   - `--dry-run`: 終了コード 0。標準エラー出力は空。target は変わらない。
   - 通常実行 (`--yes` なし) で標準入力に `n` をパイプで渡した: 標準入力がリダイレクトされているため非対話として扱われ、確認プロンプトは出ずに中止した (「標準入力が対話的でなく --yes も無いため、確認できません。」「中止しました。削除0件。」)。終了コード 2。target は変わらない。**この実行では、対話的なコンソールで `n` を入力する確認はしていない** (標準入力が対話的にならない実行環境だったため)。対話的なコンソールでの `[y/N]` 入力、進捗表示、日本語表示は、`docs/MANUAL_TESTS.md` の M01〜M07 で確認した。
   - 通常実行 (`--yes`): 終了コード 0。`same.txt` と `d\deep.txt` だけが削除され、`changed.txt`、`unrelated.txt`、`d\`、`emptydir\` が残った。ZIP の SHA-256 は変わらない。

```text
MATCHED (2):
  same.txt
  d/deep.txt
MODIFIED (1):
  changed.txt
MISSING (1):
  missing.txt
SKIPPED_SPECIAL_FILE (0):
DIRECTORY (2):
  d/
  emptydir/
合計: 6 エントリ (MATCHED 2、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 2)
DELETE_FAILED (0):
削除済み 2、DELETE_FAILED 0、未処理 0
```

2. `fatal\`: ZIP は `a.txt`、`b.txt`、`bad.txt` (Central Directory の CRC-32 だけを書き換え)、`c.txt`。target には4つとも同じ内容。`--yes` で実行: 終了コード 1。先頭の MATCHED 2件があっても削除0件で、target の4ファイルは全て残った。

```text
(標準出力)
判定済み: 2 エントリ
MATCHED (2):
  a.txt
  b.txt
MODIFIED (0):
MISSING (0):
SKIPPED_SPECIAL_FILE (0):
DIRECTORY (0):
未判定: 1 エントリ
(標準エラー出力)
FATAL: エントリ #3 "bad.txt": エントリの CRC-32 が一致しません
削除開始前に中止しました。削除0件。
```

### ACL の後片付けの確認

作業後に `icacls fixtures /T /C` (Windows テストの fixture 全体、8,795 項目、処理失敗0) を確認し、`DENY` の ACE は1つも残っていなかった。P03 の `locked.zip`・`locked-target` も継承の ACE だけだった。

## 段階 F (仕上げ、2026-10-02)

環境は「実施環境」と同じ (Windows 11 Home 10.0.26300、非管理者トークン、.NET SDK 10.0.401)。

### ビルドとテスト (ローカル)

- `dotnet build -c Release`: 警告 0、エラー 0。
- `dotnet test -c Release --no-build --logger "console;verbosity=detailed"` を、`Unextract.Cli.csproj` の変更後に2回連続で実行し、2回とも Cli 22 件、Windows 92 件、Core 504 件 (計 618 件) が全て合格した。`前提不成立` の出力は 0 件 (8.3 名の生成、`fsutil file setCaseSensitiveInfo`、`compact /c`、`fsutil sparse setflag` がこの環境では全て成功した)。D22 の出力は「E-2 実測」の表と同じだった。
- テストの後、Windows テストの fixture の P03 (Debug 6 件、Release 3 件) を `icacls /T /C` で確認し、DENY の ACE は 0 件だった。Release の fixture 全体 (6,634 項目、処理失敗 0) でも DENY は 0 件だった。

### CI

`.github/workflows/ci.yml` を作成した (push と pull_request、`windows-latest`、`permissions: contents: read`、`actions/checkout@v4` → `actions/setup-dotnet@v4` (`global-json-file: global.json`) → `dotnet build -c Release` → `dotnet test -c Release --no-build --logger "console;verbosity=detailed"`)。ローカルでは YAML として読めることだけを確認し、ワークフローは実行していない。その後の GitHub 上での実行結果は「CI の初回実行」の節に記した (初回の実行は成功し、ログに `前提` の該当は 0 件。合格件数は Core 504、Windows 92、Cli 22、E2E 22 で、ログで確認した。管理者権限の有無は未確認)。

### 配布ビルド

- 設定: `src/Unextract.Cli/Properties/PublishProfiles/win-x64.pubxml` (win-x64、`SelfContained`、`PublishSingleFile`、`PublishTrimmed` = false、`IncludeNativeLibrariesForSelfExtract`)。バージョンは `Unextract.Cli.csproj` の `Version` (0.1.0) の1か所。
- `dotnet publish src/Unextract.Cli -p:PublishProfile=win-x64 -o <scratchpad>\publish` を実行した。出力はリポジトリの外 (Claude Code のセッション用 scratchpad) で、`unextract.exe` 73,758,671 バイトと pdb 3 個 (`unextract.pdb`、`Unextract.Core.pdb`、`Unextract.Windows.pdb`)。exe の FileVersion は 0.1.0.0、ProductVersion は `0.1.0+<コミット>`。リポジトリ内に書いたのは `bin/` と `obj/` だけ。
- 生成した exe を、scratchpad の `manual-f\` に Python の `zipfile` で新しく作った fixture に対して、Git Bash から `--dry-run`、続けて `--yes` の順に実行した (標準入力は `/dev/null`、標準出力と標準エラー出力は別のファイルに受けた)。実在のデータは使っていない。

| fixture | ZIP | target | `--dry-run` | `--yes` |
|---|---|---|---|---|
| `run` | `same.txt`、`changed.txt`、`missing.txt`、`d/`、`d/deep.txt`、`emptydir/` (UTF-8 フラグなし、ASCII 名) | `same.txt`・`d\deep.txt` が一致、`changed.txt` が不一致、ZIP にない `unrelated.txt` | 終了コード 0。MATCHED 2、MODIFIED 1、MISSING 1、DIRECTORY 2。target は不変 | 終了コード 0。「削除済み 2、DELETE_FAILED 0、未処理 0」。`changed.txt`、`unrelated.txt`、`d\` が残った |
| `ja` | `資料/報告書.txt`、`資料/写真一覧.csv`、`ファイル名.txt`、`café░.txt`、`メモ.txt` (全て UTF-8 フラグ付き) | `資料\報告書.txt`・`café░.txt` が一致、`メモ.txt` が不一致、ZIP にない `無関係.txt` | 終了コード 0。MATCHED 2、MODIFIED 1、MISSING 2。日本語名が文字化けせずに表示された | 終了コード 0。削除済み 2。`メモ.txt`、`無関係.txt`、`資料\` が残った |
| `cp437` | `café░.txt` (UTF-8 フラグなし。名前のバイトは CP437 の `63 61 66 82 B0 2E 74 78 74`)、`plain.txt` | 両方とも一致 | 終了コード 0。`café░.txt` を CP437 として復号した名前で MATCHED 2 | 終了コード 0。削除済み 2 |

- 3つの ZIP の SHA-256 は実行の前後で変わらなかった。標準エラー出力は全て空だった (進捗表示は標準エラー出力がリダイレクトされているときは出ないため、見え方は下記の手動確認の項目)。
- `cp437` の結果から、単一ファイルの exe でも `CodePagesEncodingProvider` による CP437 の復号が動くことを確認した (フラグなしの名前が U+FFFD を含まずに復号され、target の `café░.txt` と序数一致した)。

### fixture の掃除スクリプト

`scripts/clean-test-fixtures.ps1` を作成した。実行 (`-Execute`) はしていない。一覧モード (既定。読み取りのみ) を Windows PowerShell 5.1 で実行した結果: 終了コード 0、fixtures ディレクトリ 6 個 (Cli.Tests の Debug・Release、Core.Tests の Debug・Release、Windows.Tests の Debug・Release)、リンク (junction) 53 個 (リンク先は全て fixture 内にあり、全て存在する)、P03 の fixture 7 個 (自分の SID の DENY は全て 0)。「Nothing was changed.」と表示した。

### 手動確認のチェックリスト (利用者が実施)

手順と結果の記録欄は [`MANUAL_TESTS.md`](MANUAL_TESTS.md) (M01〜M07) に移した (ここにあったチェックリストは結果が未記入だった)。

## E2E (X 系、2026-10-02)

環境は「実施環境」と同じ (Windows 11 Home 10.0.26300、非管理者トークン、.NET SDK 10.0.401)。`tests/Unextract.E2E.Tests` (`PLAN_TESTS.md` §10) を追加し、ローカルで実行した。fixture はテストの出力先の `fixtures/<テスト名>-<GUID>/` だけで、実在のデータは使っていない。

- `dotnet build unextract.sln` (Debug): 警告 0、エラー 0。
- `dotnet test unextract.sln --no-build --logger "console;verbosity=detailed"` を2回連続で実行し、2回とも Core 504 件、Windows 92 件、Cli 22 件、E2E 22 件 (X01〜X12。Theory のケースを含む) が全て合格した。`前提不成立` の出力は 0 件。
- publish 版: `dotnet publish src/Unextract.Cli -p:PublishProfile=win-x64 -o <scratchpad>\publish-e2e` (リポジトリの外) の `unextract.exe` (73,758,671 バイト) を `UNEXTRACT_E2E_EXE` に指定して E2E を1回実行し、22 件全て合格した (X09 の CP437 名の照合を含む)。`UNEXTRACT_E2E_EXE` に存在しないパスを指定すると、exe が見つからない旨のメッセージで失敗することも確認した。
- stdout・stderr をリダイレクトして起動した exe の stdout を UTF-8 で読み、日本語名 (X08) と CP437 由来の名前 (X09) が U+FFFD なしで読めた。stderr がリダイレクトされているとき、進捗 (`Checking`、`Deleting`、改行を伴わない CR) は出なかった (X10)。stdin がリダイレクトされているときは、空・`n`・`y` のいずれを渡しても非対話として中止した (X03)。
- 実行後、E2E の fixture (1,296 項目) と Windows テストの fixture (17,639 項目) を `icacls /T /C` で確認し、DENY の ACE は 0 件、処理の失敗は 0 件だった (E2E は ACL を変えない)。
- CI (`.github/workflows/ci.yml`) に publish と publish 版での E2E のステップを加えた。ローカルでは実行していない。GitHub 上での結果は「CI の初回実行」の節に記した (publish 版の E2E は 22 件中 22 件合格)。

## CI の初回実行 (GitHub Actions、2026-10-02)

結果は人間が GitHub Actions の画面とログで確認したもの。

- 実行: `.github/workflows/ci.yml`、ランナーは `windows-latest`。初回の実行 (コミット `78c7609`、2026-10-02、トリガーは push) が成功した。所要時間は約1分18秒。
- 手順: Release ビルド、既存のテスト、単一ファイルの publish、`UNEXTRACT_E2E_EXE` を指定した E2E (X 系)。E2E は 22 件中 22 件合格した。
- ログを「前提」で検索した該当は 0 件だった (`前提不成立` の表示なし)。
- 注釈 (警告) が1件あった: 使用中の公式アクションが Node.js 20 を対象にしており、廃止予定であるという GitHub の通知。実行は成功しており、対応は見送る (アクションの新しいメジャーバージョンが出たときに更新する)。
- 未確認: ランナーで管理者権限が実際にあったかどうか。

## Fast / PTY / publish E2E の最終確認 (2026-10-03 記録)

以下は今回提示された実装・検証済みの実績を、実ファイルのテスト名・スクリプト名・処理と照合して同期した記録。文書同期作業では build / test を再実行していない。以前の節の件数・実施日は当時の記録として残す。

### Fast 本体とモード共通テスト

- Fast 本体は実装済み。SPEC §6.1 手順7 は Strict では content verification / full-byte comparison、Fast では body を読まず同サイズなら `SAME_SIZE`。§8.3 手順3 は Strict では2回目の full-byte comparison、Fast ではこの手順だけを省略し、手順1・2・4・5・6 は両モード共通。Fast 専用レイヤー、サービス、Win32 API は追加していない。
- 新規 ID: C15、R09、T17、D24、D25、D26、O05、O06、O07、X13、X14、X15 を検証済み。
- Fast 再利用監査で P04〜P07 の必要箇所、Z01〜Z09、R01〜R06、O03、O04 などの不足を補完済み。既存のモード共通安全性テストも `PLAN_TESTS.md` の再利用原則に従って両モードで実行済み。T12 の通常のハンドル解放・resource safety は両モード、「比較中 target-read 例外」の経路は Strict 専用。
- D04 の Fast content-only change は、今回の実 NTFS fixture では `File.WriteAllBytes` によって `LastWriteTime` が変わり、§8.3 手順2 の同一性再検証で停止した。仕様どおり。メタデータを保った content-only change は D25 (`DeletionIntegrationTests.D25_ContentOnlyDifferences`) が担当し、Strict は再比較で停止、Fast は検出せず削除した。D04 と D25 は異なる変更条件を検証しており、矛盾しない。
- Fast 固有ではない既存 Win-layer test の欠け (P01、P02、P05、P07 の確認後差し替え、Z06、Z08、T01、T10、C04〜C06、C08〜C12、D01、Y02、O01〜O03 など) は今回追加していない。テスト計画上の `Core+Win` は実施予定の種別であり、Win の全ケースを実装・検証済みとする記録ではない。本書の既存記録にも、存在しない Win test を Win で確認済みとした箇所は見つかっていない。

### body 非読取と CRC の確認方法

- Core: C15 の `RecordingContentProvider`、T17 の呼び出し記録・`ThrowOnRead`、D24 の呼び出し順・比較器の記録など、既存のフック・記録で直接確認した。Fast は ZIP entry body の `GetContent` / `Open()` を呼ばず、target body を初回分類でも削除フェーズでも読まず、§8.3 手順3 の recompare を呼ばない。
- CRC 計算そのものには専用観測フックを追加していない。comparer に到達しない、ZIP stream を `Open()` しない、CRC 期待値を参照しないことからの間接確認であり、CRC 計算そのものを直接観測したとはしない。C15 の期待値は維持した。
- Win 実 NTFS: body 非読取の多くは D25 / C15 / T17 などの結果による間接確認。Core の観測を Win API 上の直接観測と同一視しない。新しい観測 API は追加していない。

### M08 / O07 の Windows PTY 自動化と cleanup

- `PtyConfirmationTests.M08_O07_InteractiveWarningAndCancelWithN` が `yn-n` 相当の fixture を一時生成して Fast / Strict を実行する。テスト専用依存 `Porta.Pty 2.2.2` は E2E test project のみにあり、製品コードには PTY 関連依存が無い。
- Fast: 実際に `[y/N]` が出る、PLAN 指定と一致する警告がヘッダーと確認直前の計2回出る、最後の警告から確認文・`[y/N]` までに別出力が無いことを確認。`[y/N]` 表示後に `n` を送り、終了コード 2、target fixture 非削除を確認した。
- Strict: 実際に `[y/N]` が出て、Fast warning が存在しない。`n` を送り、終了コード 2、target fixture 非削除を確認した。
- PTY では stdout / stderr が同一端末に流れるため、ヘッダー警告の stdout 所属は O06 が担当する。`--yes`、非対話条件、偽プロンプトの経路は既存 Core テストが担当する。PTY の `y` ケースは追加していない。実端末のフォント・折り返し・視認性は自動化対象外で、M08 の手動記録欄に残す。
- 正常終了、assertion / 起動失敗 / 例外時とも cleanup を行う。PTY / process tree の終了・Dispose 完了後に、テスト自身が作った GUID 付きディレクトリだけを削除する。cleanup failure は黙殺せず、元の失敗情報・terminal output を保持する。
- PTY 単独を3回実行し、毎回 Fast / Strict の2件とも成功。各回の新規 fixture 残存は 0。意図的な exe 起動失敗でも fixture 残存 0。過去の旧 PTY fixture は手動で掃除済み。

### publish 版 E2E wrapper と cleanup

標準実行方法:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-e2e-tests.ps1
```

- `scripts/run-e2e-tests.ps1` は OS temp 直下の `unextract-e2e-publish-<GUID>` に既存 `win-x64` profile で Release publish し、exe 存在確認後に wrapper プロセス内で `UNEXTRACT_E2E_EXE` を設定して E2E project 全体 (PTY を含む) を実行する。`finally` で元の環境変数を復元し、自分が作った temp publish だけを削除する。
- 外部 exe、既存 `bin/` / `obj/` は削除しない。削除対象を `UNEXTRACT_E2E_EXE` から逆算しない。`UnextractProcess.ResolveExe` は環境変数があればその exe、未設定なら通常 build 出力を使用する。E2E test 自身は publish しない。
- 正常系3回: E2E / PTY 成功、終了コード 0。各回で今回の publish 残存 0、PTY fixture 残存 0、環境変数復元、外部 exe の hash 不変を確認した。
- 失敗系: publish failure、test failure、publish 後 exe 不在、test failure + cleanup failure、test success + cleanup failure を確認済み。publish / test の元の失敗 code を保持し、cleanup failure が元の失敗を隠さないことを確認した。元の失敗が無く wrapper / cleanup だけが失敗した場合は終了コード 1。
- `dotnet test unextract.sln` は通常 build を含む全体 test、wrapper は配布形態の publish 版 E2E / PTY 検証で、役割が異なる。その他の fixture は既存の手動 cleanup 方針を維持する。

### 最終 build / test 実績

| 対象 | passed | failed | skipped |
|---|---|---|---|
| Core | 732 | 0 | 0 |
| Windows | 155 | 0 | 0 |
| CLI | 30 | 0 | 0 |
| E2E (PTY を含む) | 27 | 0 | 0 |
| 合計 | **944** | **0** | **0** |

`dotnet build unextract.sln`: warning 0 / error 0。PTY 単独は Fast / Strict 2件が3回連続全成功、各回 fixture 残存 0。publish wrapper は正常系3回成功、各回 publish 残存 0・PTY fixture 残存 0。

## 必須テスト対応表

最初の依頼の必須12項目と、第2回の依頼で追加した項目、Fast モード (SPEC §15) の追加で加えた項目それぞれに対応する [`PLAN_TESTS.md`](PLAN_TESTS.md) のテスト ID。

「Fast」以外の行の期待結果は Strict のものである。そのうち Strict と共通の安全性を確かめるテストは、`PLAN_TESTS.md` 冒頭の「モード違いの再利用の原則」に従い、同じ fixture で `--fast` を付けても実行する (対象のテスト ID と期待結果の読み替えは同書が正)。SPEC §5.2 の内容検証、§8.3 の手順3、`MATCHED` の表示に依存するテスト (P01、C01〜C14、R07、R08、T01、D03、D10、D13、D23、O01、X01〜X12) は Strict だけで行い、Fast 側の観点は「Fast」の行のテストで確かめる。

| # | 必須項目 | テスト ID |
|---|---|---|
| 1 | 同じ壊れたエントリについて target が MISSING / サイズ不一致 / サイズ一致の挙動。前2者は内容を読まず FATAL にならない。サイズ一致はランタイム・API が検出する異常で FATAL | C01〜C08 (各行の3列)、C09 |
| 2 | 上記の `--dry-run` と通常実行で、確認・削除フェーズ前までの初回分類が一致 | Y01 (C01〜C08 の全列を含む)、本書冒頭の dry-run 一致の原則 |
| 3 | 削除開始前 FATAL では、それ以前に MATCHED があっても削除0件 | P01、P02、T11 |
| 4 | 結果表示後・確認待ち中の外部変更・置換を、削除直前再検証で検出して停止 | D04、D05、D10、D19、D20 |
| 5 | 削除直前に再オープンし、そのハンドルで安全確認し、同じハンドルから削除 | D03 (呼び出し順)、D09、D17、D18、D23、SPEC §13 PoC 1・4 |
| 6 | File ID の変化で停止 | D04 (同名で作り直し・置換)、D14 (オープン成功後の File ID 不一致) |
| 7 | ADS / hardlink / 属性 / reparse 状態の変化で停止 | D05、D16 |
| 8 | メタデータ 128 MiB 境界 (ちょうど / +1) | R03、R04 |
| 9 | 宣言展開量合計 64 GiB 境界 (全エントリ MISSING でも適用) | R06 |
| 10 | FATAL 途中終了時: 判定済みパス、原因エントリ、未判定は件数のみ、削除0件 | O02、P01 |
| 11 | 正常完走時は全カテゴリー・全パス表示 | O01 |
| 12 | CRC (必須として維持): CRC 不一致を検出する | C01、C04、C11 (初回比較)、D13 (再比較)、C12 (DD の期待 CRC)、C14 (公開 API の参照) |
| 追加 | 比較後・再オープン前に、File ID・サイズ・更新日時を保ったまま内容だけを書き換える → 再比較で検出し停止 | D10 |
| 追加 | 削除用ハンドルを開いている間、他プロセスは対象を書き込みで開けない | D09、D23 |
| 追加 | 再比較中の ZIP 側の異常 (CRC 不一致、サイズ超過、終端欠落) → 停止、`DELETE_FAILED` にしない | D13 |
| 追加 | 削除用オープンの共有違反・アクセス拒否 (同一性に疑義なし) → `DELETE_FAILED` で続行 | D11 |
| 追加 | 削除用オープンで同一性に疑義 (消失、reparse 化、親の差し替え、File ID 不一致) → 停止 | D14、D19、D20 |
| 追加 | 未知・曖昧なエラー → 停止 | D15 |
| 追加 | `--dry-run` と通常実行の初回分類の一致 | Y01 |
| 追加 | dry-run は再オープン・再検証・2回目の比較・削除を行わず、外部変更で通常実行だけが停止し得る (意図した差) | Y02 |
| 追加 | 親成分の分類表 (存在しない・通常ファイル → MISSING、reparse → SKIPPED、判定不能 → FATAL) | T02、T03、T04 |
| 追加 | 実名確認 (ハンドルからの列挙と序数比較): 大小文字違い・8.3 名は MISSING、列挙・検証の失敗は FATAL、無関係な名前を保持しない | T05、T06、T13、T14、T15、SPEC §13 PoC 8 |
| 追加 | ファイルエントリの種別 `0x4000`、ディレクトリエントリの種別 `0x8000` は全体 FATAL | Z04a、Z04b (受理側は Z05) |
| 追加 | 識別確認が一致に見える場合は `DELETE_FAILED` (削除しない)、不一致・失敗は停止 | D11、D14、D15 |
| 追加 | 比較中の共有違反で全体 FATAL | T11、P02 |
| PoC 反映 | 削除指示の flags は `DELETE \| POSIX_SEMANTICS` (0x3)、`IGNORE_READONLY_ATTRIBUTE` を含まない | D17 |
| PoC 反映 | 削除の成立は `DeletePending` で判定し、API の成功だけでは成立としない | D18 |
| PoC 反映 | 列挙での同名の重複は最初の1件だけを採用、見落としは `MISSING` | T14 |
| PoC 反映 | ディレクトリ単位で大文字小文字を区別するディレクトリで、各個体に正しく対応 | T15 |
| PoC 反映 | 親 File ID だけが不一致の差し替え / 最終パスだけが不一致の差し替えで、それぞれ停止 | D19、D20 |
| PoC 反映 | 段階5 (削除指示) の失敗 (read-only の付与) で、そのファイルを残し以後を停止 | D21、D08 |
| PoC 反映 | 対象の DELETE だけを ACL で拒否した場合 (段階1は成功) の段階5の結果 (扱いは確定。E-2 で実測済み: 初回分類の前の拒否は削除、確認待ち中の拒否は段階2で停止。どちらも誤削除なし) | D22 |
| Fast | Strict と共通の安全性 (入力エラー、内容検証に依存しない FATAL、`MISSING`、`SKIPPED_SPECIAL_FILE`、削除0件、削除フェーズの停止、`DELETE_FAILED`、識別確認) が `--fast` でも同じ | `PLAN_TESTS.md` の「モード違いの再利用の原則」の対象 (P02〜P08、Z01〜Z09、R01〜R06、T02〜T16 (T10 の比較中の読取失敗・T12 の比較中 target-read 例外を除く)、D01、D02、D04〜D09、D11、D14〜D22、Y01、Y02、O02〜O04) を `--fast` で |
| Fast | 内容検証で失敗するエントリでも、サイズ一致なら FATAL にならず `SAME_SIZE`。どの target の状態でも ZIP エントリを開かず CRC を計算しない | C15 |
| Fast | 実測展開量を計上せず、その上限で FATAL にならない | R09 |
| Fast | 同一内容・1 byte 変更・0 byte は `SAME_SIZE`、サイズ違いは `MODIFIED`。ZIP エントリと target の内容を読まない | T17 |
| Fast | 削除フェーズは SPEC §8.3 の手順3 (再比較) だけを行わず、同じハンドルで再検証・最終確認・削除・成立確認を行う。削除指示は Strict と同じ | D24 |
| Fast | 同じサイズで内容の違うファイル (初回分類の前から異なる / 確認待ち中にメタデータを保ったまま書き換えた) が削除される (仕様どおりの挙動) | D25、T17、X14 |
| Fast | `--fast` の解析 (重複は入力エラー、`--dry-run`・`--yes`・`-y` と併用可、なしは Strict、使い方に `[--fast]`) | D26 |
| Fast | `--dry-run` と通常実行の初回分類の一致は同じモード同士 | Y01、Y02 (Fast 同士)、X13 |
| Fast | 表示するカテゴリー (Strict は `SAME_SIZE` を、Fast は `MATCHED` を出さない) | O05、X13 |
| Fast | 警告: 結果表示に至る Fast の実行では解析結果の一覧の先頭行 (標準出力) に出し、結果表示に至らない終了と Strict では出さない | O06、X13〜X15 |
| Fast | 警告: Fast の確認プロンプトの直前の行に出し (確認プロンプトと同じ経路)、`--yes`・非対話と Strict では出さない | O07 (Core+E2E PTY。M08 の機能部分を自動確認済み。実端末の視覚確認は [`MANUAL_TESTS.md`](MANUAL_TESTS.md)) |
| Fast | exe での Fast の実行 (`--dry-run`、`--yes`、Strict では FATAL になる CRC 不一致の fixture、`--fast` の重複、事前検証の FATAL、削除候補0件) | X13、X14、X15 |
